using System.Diagnostics;
using ScriptSuite.Models;

namespace ScriptSuite.Services;

/// <summary>
/// Creates/removes per-script systemd USER timers (the Linux counterpart of
/// windows-scripts ScheduledTaskService, which used Task Scheduler). One
/// tier only — there is no InteractiveToken/S4U split on Linux: every timer
/// is a user timer invoking this binary headless:
///   ExecStart=&lt;binary&gt; --scheduled-run &lt;id&gt;
/// Units live in ~/.config/systemd/user/scriptsuite-&lt;id&gt;.{service,timer}.
/// Mapping from the Every [N] [Days|Hours|Weeks] at [HH:mm] dialog model:
///   Days N=1   -> OnCalendar daily at HH:mm            (wall-clock, exact)
///   Weeks N=1  -> OnCalendar Monday at HH:mm           (wall-clock, exact)
///   Hours N    -> OnUnitActiveSec N hours (Persistent)  (interval from enable)
///   Days/Weeks N&gt;1 -> OnUnitActiveSec N days/weeks (Persistent) — interval
///     measured from enable/save, NOT wall-clock HH:mm. This is the one
///     semantic compromise vs Task Scheduler (which anchors N-day intervals
///     to a start date); SummaryFor() describes the actual behavior so the UI
///     can present it honestly, and the dashboard (7b) may restrict N&gt;1.
/// All timers carry Persistent=true (catch up missed runs as the interactive
/// login session allows — user timers only run inside a user session).
/// Timeouts mirror Windows (5s query, 20s register) with kill-on-timeout.
/// </summary>
public sealed class SystemdScheduleService
{
    private const int QueryTimeoutMs = 5000;
    private const int RegisterTimeoutMs = 20000;

    public static string UnitDir =>
        Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
            "systemd", "user");

    public static string TimerNameFor(string scriptId) => $"scriptsuite-{Sanitize(scriptId)}.timer";
    public static string ServiceNameFor(string scriptId) => $"scriptsuite-{Sanitize(scriptId)}.service";

    private static string Sanitize(string scriptId)
    {
        var sb = new System.Text.StringBuilder();
        foreach (char c in scriptId)
            sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '-');
        return sb.Length > 0 ? sb.ToString() : "script";
    }

    private static string BinaryPath =>
        Environment.ProcessPath
        ?? Path.Combine(AppContext.BaseDirectory, "ScriptSuite");

    public static bool TaskExists(string scriptId)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("systemctl",
                $"--user --quiet is-enabled {TimerNameFor(scriptId)}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) return false;
            if (!p.WaitForExit(QueryTimeoutMs)) { try { p.Kill(); } catch { } return false; }
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    public static (bool ok, string? error) Register(ScheduleEntry entry)
    {
        try
        {
            if (!TimeSpan.TryParse(entry.TimeOfDay, out var ts))
                ts = new TimeSpan(9, 0, 0);
            int interval = entry.Interval < 1 ? 1 : entry.Interval;
            string hhmm = ts.ToString(@"hh\:mm");

            string timerBody = (entry.Unit, interval) switch
            {
                ("Days", 1) => $"OnCalendar=*-*-* {hhmm}:00",
                ("Weeks", 1) => $"OnCalendar=Mon *-*-* {hhmm}:00",
                ("Hours", _) => $"OnUnitActiveSec={interval}h",
                ("Days", _) => $"OnUnitActiveSec={interval}d",
                ("Weeks", _) => $"OnUnitActiveSec={interval}w",
                _ => $"OnCalendar=*-*-* {hhmm}:00",
            };

            string binary = BinaryPath;
            string service = $"""
                [Unit]
                Description=ScriptSuite scheduled run: {entry.ScriptId}

                [Service]
                Type=oneshot
                ExecStart={binary} --scheduled-run {entry.ScriptId}
                """;
            string timer = $"""
                [Unit]
                Description=ScriptSuite schedule: {SummaryFor(entry)}

                [Timer]
                {timerBody}
                Persistent=true

                [Install]
                WantedBy=timers.target
                """;

            Directory.CreateDirectory(UnitDir);
            File.WriteAllText(Path.Combine(UnitDir, ServiceNameFor(entry.ScriptId)), service + "\n");
            File.WriteAllText(Path.Combine(UnitDir, TimerNameFor(entry.ScriptId)), timer + "\n");

            if (!RunSystemctl("daemon-reload", RegisterTimeoutMs, out var err1))
                return (false, "daemon-reload: " + err1);
            if (!RunSystemctl($"enable --now {TimerNameFor(entry.ScriptId)}", RegisterTimeoutMs, out var err2))
                return (false, "enable: " + err2);
            return TaskExists(entry.ScriptId) ? (true, null) : (false, "timer not listed after enable");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public static (bool ok, string? error) Unregister(string scriptId)
    {
        try
        {
            // Best effort: a half-registered timer must still end up removed.
            RunSystemctl($"disable --now {TimerNameFor(scriptId)}", RegisterTimeoutMs, out _);
            TryDelete(Path.Combine(UnitDir, TimerNameFor(scriptId)));
            TryDelete(Path.Combine(UnitDir, ServiceNameFor(scriptId)));
            RunSystemctl("daemon-reload", RegisterTimeoutMs, out _);
            return TaskExists(scriptId)
                ? (false, "timer still listed after unregister")
                : (true, null);
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public static string SummaryFor(ScheduleEntry entry)
    {
        int interval = entry.Interval < 1 ? 1 : entry.Interval;
        string hhmm = TimeSpan.TryParse(entry.TimeOfDay, out var ts) ? ts.ToString(@"hh\:mm") : "09:00";
        return (entry.Unit, interval) switch
        {
            ("Days", 1) => $"Daily at {hhmm}",
            ("Weeks", 1) => $"Weekly Monday at {hhmm}",
            ("Hours", 1) => "Hourly",
            ("Hours", _) => $"Every {interval} hours",
            ("Days", _) => $"Every {interval} days (from save)",
            ("Weeks", _) => $"Every {interval} weeks (from save)",
            _ => $"Daily at {hhmm}",
        };
    }

    private static bool RunSystemctl(string args, int timeoutMs, out string error)
    {
        error = "";
        try
        {
            using var p = Process.Start(new ProcessStartInfo("systemctl", "--user " + args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null) { error = "failed to start systemctl"; return false; }
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(); } catch { }
                error = "timeout";
                return false;
            }
            if (p.ExitCode != 0)
            {
                try { error = p.StandardError.ReadToEnd().Trim(); } catch { }
                if (string.IsNullOrEmpty(error)) error = $"systemctl exit {p.ExitCode}";
                return false;
            }
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
