using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using ScriptSuite.Models;

namespace ScriptSuite.Services;

/// <summary>Stage 7a proofs for systemd scheduling: unit generation +
/// register/list/unregister against the real user systemd, the headless
/// --scheduled-run path (direct, busy-guard, and a genuine timer firing),
/// ScheduleStore round-trip, and manual/scheduled history separation.
/// The firing probe uses a hand-written OnActiveSec timer (the production
/// service only speaks the Days/Hours/Weeks dialog model); user timers are
/// cleaned up in finally blocks. Returns report lines ending in SELF-TEST
/// PASS or SELF-TEST FAIL.</summary>
public static class Stage7aSelfTest
{
    private const int LOCK_EX = 2;
    private const int LOCK_NB = 4;
    private const int LOCK_UN = 8;

    [DllImport("libc.so.6", SetLastError = true)]
    private static extern int flock(int fd, int operation);

    public static List<string> Run()
    {
        var lines = new List<string>();
        void Check(bool ok, string label) => lines.Add((ok ? "PASS " : "FAIL ") + label);

        string binary = Path.GetFullPath(Path.Combine(AppPaths.ManifestsDir, "..", "ScriptSuite"));
        string scratch = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "stage7a-run");
        try
        {
            if (!File.Exists(binary))
            {
                lines.Add("FAIL runner binary not found: " + binary);
                lines.Add("SELF-TEST FAIL");
                return lines;
            }

            // --- 1-3. register/list/unregister (skip if the user already has one) ---
            if (SystemdScheduleService.TaskExists("TempCleanup"))
            {
                lines.Add("SKIP register/unregister (user already has a TempCleanup timer)");
            }
            else
            {
                var entry = new ScheduleEntry { ScriptId = "TempCleanup", Unit = "Days", Interval = 1, TimeOfDay = "09:00" };
                var (ok, error) = SystemdScheduleService.Register(entry);
                string timerPath = Path.Combine(SystemdScheduleService.UnitDir, "scriptsuite-TempCleanup.timer");
                string svcPath = Path.Combine(SystemdScheduleService.UnitDir, "scriptsuite-TempCleanup.service");
                bool filesOk = File.Exists(timerPath) && File.Exists(svcPath)
                    && File.ReadAllText(timerPath).Contains("OnCalendar=*-*-* 09:00:00")
                    && File.ReadAllText(svcPath).Contains("--scheduled-run TempCleanup");
                Check(ok, $"register daily timer (err={error ?? "none"})");
                Check(filesOk, "unit files carry OnCalendar daily + --scheduled-run ExecStart");
                Check(SystemdScheduleService.TaskExists("TempCleanup") && ListTimers().Any(l => l.Contains("scriptsuite-TempCleanup.timer")),
                    "timer listed by the user systemd instance");
                var (uok, uerr) = SystemdScheduleService.Unregister("TempCleanup");
                Check(uok && !File.Exists(timerPath) && !File.Exists(svcPath)
                        && !SystemdScheduleService.TaskExists("TempCleanup"),
                    $"unregister removes everything (err={uerr ?? "none"})");
            }

            // --- 4. ScheduleStore round-trip (isolated path) ---
            string storePath = Path.Combine(AppPaths.RuntimeRoot, $"scriptsuite_stage7a_sched_{Guid.NewGuid():N}.json");
            try
            {
                var store = new ScheduleStore(storePath);
                store.Set(new ScheduleEntry { ScriptId = "TempCleanup", Unit = "Hours", Interval = 3, TimeOfDay = "09:00" });
                var reloaded = new ScheduleStore(storePath);
                Check(reloaded.Has("TempCleanup") && reloaded.Get("TempCleanup")?.Interval == 3,
                    "ScheduleStore persists + reloads entries");
                reloaded.Remove("TempCleanup");
                Check(!new ScheduleStore(storePath).Has("TempCleanup"), "ScheduleStore removes entries");
            }
            finally { TryDelete(storePath); }

            // --- 5. SummaryFor variants (pure function) ---
            Check(SystemdScheduleService.SummaryFor(new ScheduleEntry { Unit = "Days", Interval = 1, TimeOfDay = "09:00" }) == "Daily at 09:00"
                    && SystemdScheduleService.SummaryFor(new ScheduleEntry { Unit = "Hours", Interval = 3 }) == "Every 3 hours"
                    && SystemdScheduleService.SummaryFor(new ScheduleEntry { Unit = "Weeks", Interval = 2 }) == "Every 2 weeks (from save)"
                    && SystemdScheduleService.SummaryFor(new ScheduleEntry { Unit = "Days", Interval = 2 }) == "Every 2 days (from save)",
                "SummaryFor covers daily/hourly/multi-day/multi-week wording");

            // --- 6. direct --scheduled-run (test config, backup/restore user config) ---
            string cfgPath = AppPaths.ConfigPathFor("TempCleanup");
            byte[]? backup = File.Exists(cfgPath) ? File.ReadAllBytes(cfgPath) : null;
            try
            {
                SeedScratch(scratch);
                File.WriteAllText(cfgPath, JsonSerializer.Serialize(new
                {
                    TargetFolder = scratch,
                    CutoffDays = 7,
                    IgnoreFolders = new string[0],
                }));
                var history = new RunHistoryStore(AppPaths.HistoryDbPath);
                int schedBefore = history.GetRecentScheduled(1000).Count;
                int manualBefore = history.GetRecent(1000).Count;
                int exit = RunBinary(binary, "--scheduled-run", "TempCleanup", timeoutMs: 60000, out string output);
                var after = history.GetRecentScheduled(1000);
                var row = after.FirstOrDefault();
                Check(exit == 0 && after.Count == schedBefore + 1
                        && row?.Outcome == "Success" && row?.Trigger == "timer"
                        && (row?.Summary?.Contains("deleted") == true),
                    $"direct scheduled run records Success/timer row (exit={exit}, summary={row?.Summary})");
                Check(!File.Exists(Path.Combine(scratch, "old.tmp")) && File.Exists(Path.Combine(scratch, "new.tmp")),
                    "direct scheduled run executed the script (old gone, new kept)");
                Check(history.GetRecent(1000).Count == manualBefore,
                    "manual RunHistory untouched by scheduled runs");
            }
            finally { RestoreBytes(cfgPath, backup); }

            // --- 7. busy guard: externally held lock -> SkippedBusy, script untouched ---
            try
            {
                SeedScratch(scratch);
                File.WriteAllText(cfgPath, JsonSerializer.Serialize(new
                {
                    TargetFolder = scratch,
                    CutoffDays = 7,
                    IgnoreFolders = new string[0],
                }));
                var history = new RunHistoryStore(AppPaths.HistoryDbPath);
                int schedBefore = history.GetRecentScheduled(1000).Count;
                Directory.CreateDirectory(AppPaths.DataRoot);
                using var lk = new FileStream(Path.Combine(AppPaths.DataRoot, "scheduled.lock"),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
                int fd = (int)lk.SafeFileHandle!.DangerousGetHandle();
                if (flock(fd, LOCK_EX | LOCK_NB) != 0)
                {
                    lines.Add("FAIL could not hold test lock (another run in progress?)");
                }
                else
                {
                    try
                    {
                        int exit = RunBinary(binary, "--scheduled-run", "TempCleanup", timeoutMs: 60000, out _);
                        var row = history.GetRecentScheduled(1000).FirstOrDefault();
                        Check(exit == 0 && history.GetRecentScheduled(1000).Count == schedBefore + 1
                                && row?.Outcome == "SkippedBusy",
                            $"lock held -> SkippedBusy row (exit={exit}, outcome={row?.Outcome})");
                        Check(File.Exists(Path.Combine(scratch, "old.tmp")),
                            "busy run never touched the script target");
                    }
                    finally { flock(fd, LOCK_UN); }
                }
            }
            finally { RestoreBytes(cfgPath, backup); }

            // --- 8. genuine firing: hand-written OnActiveSec probe timer ---
            try
            {
                SeedScratch(scratch);
                File.WriteAllText(cfgPath, JsonSerializer.Serialize(new
                {
                    TargetFolder = scratch,
                    CutoffDays = 7,
                    IgnoreFolders = new string[0],
                }));
                var history = new RunHistoryStore(AppPaths.HistoryDbPath);
                int schedBefore = history.GetRecentScheduled(1000).Count;
                string probeSvc = Path.Combine(SystemdScheduleService.UnitDir, "scriptsuite-stage7a-probe.service");
                string probeTmr = Path.Combine(SystemdScheduleService.UnitDir, "scriptsuite-stage7a-probe.timer");
                try
                {
                    Directory.CreateDirectory(SystemdScheduleService.UnitDir);
                    File.WriteAllText(probeSvc, "[Unit]\nDescription=Stage7a firing probe\n\n[Service]\nType=oneshot\n"
                        + $"ExecStart={binary} --scheduled-run TempCleanup\n");
                    File.WriteAllText(probeTmr, "[Unit]\nDescription=Stage7a firing probe timer\n\n[Timer]\n"
                        + "OnActiveSec=15\nPersistent=false\n\n[Install]\nWantedBy=timers.target\n");
                    RunCtl("daemon-reload");
                    RunCtl("enable --now scriptsuite-stage7a-probe.timer");
                    var deadline = DateTime.Now.AddSeconds(90);
                    int schedAfter = schedBefore;
                    while (DateTime.Now < deadline)
                    {
                        schedAfter = history.GetRecentScheduled(1000).Count;
                        if (schedAfter > schedBefore) break;
                        Thread.Sleep(3000);
                    }
                    Check(schedAfter == schedBefore + 1
                            && history.GetRecentScheduled(1000).FirstOrDefault()?.Outcome == "Success",
                        $"real timer firing executed + recorded (rows {schedBefore}->{schedAfter})");
                    Check(!File.Exists(Path.Combine(scratch, "old.tmp")),
                        "fired run executed the script target");
                }
                finally
                {
                    RunCtl("disable --now scriptsuite-stage7a-probe.timer");
                    TryDelete(probeSvc); TryDelete(probeTmr);
                    RunCtl("daemon-reload");
                }
            }
            finally { RestoreBytes(cfgPath, backup); }

            // --- 9. unknown id -> exit 1 ---
            {
                int exit = RunBinary(binary, "--scheduled-run", "NoSuchScript", timeoutMs: 60000, out string output);
                Check(exit == 1 && output.Contains("unknown script id"),
                    $"unknown id exits 1 with a clear error (exit={exit})");
            }

            lines.Add(lines.Any(l => l.StartsWith("FAIL")) ? "SELF-TEST FAIL" : "SELF-TEST PASS");
        }
        catch (Exception ex)
        {
            lines.Add("FAIL exception: " + ex.Message);
            lines.Add("SELF-TEST FAIL");
        }
        finally
        {
            try
            {
                string scratch2 = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "stage7a-run");
                if (Directory.Exists(scratch2)) Directory.Delete(scratch2, recursive: true);
            }
            catch { }
        }
        return lines;
    }

    // ------------------------------------------------------------ helpers

    private static void SeedScratch(string scratch)
    {
        if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
        Directory.CreateDirectory(scratch);
        File.WriteAllText(Path.Combine(scratch, "old.tmp"), "old");
        File.WriteAllText(Path.Combine(scratch, "new.tmp"), "new");
        File.SetLastWriteTime(Path.Combine(scratch, "old.tmp"), DateTime.Now.AddDays(-10));
    }

    private static void RestoreBytes(string path, byte[]? backup)
    {
        try
        {
            if (backup is null) { if (File.Exists(path)) File.Delete(path); }
            else File.WriteAllBytes(path, backup);
        }
        catch { }
    }

    private static int RunBinary(string binary, string arg1, string arg2, int timeoutMs, out string output)
    {
        // ArgumentList goes on the StartInfo BEFORE Process.Start (setting it
        // on the Process afterwards is too late — Stage 4 lesson, encoded).
        var psi = new ProcessStartInfo(binary)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(arg1);
        psi.ArgumentList.Add(arg2);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("failed to start runner binary");
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            output = "[TIMEOUT waiting for child]";
            return 124;
        }
        output = outTask.Result + errTask.Result;
        return p.ExitCode;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static List<string> ListTimers()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("systemctl", "--user list-timers --no-legend --no-pager")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) return new List<string>();
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10000);
            return output.Split('\n').ToList();
        }
        catch { return new List<string>(); }
    }

    private static void RunCtl(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("systemctl", "--user " + args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            p?.WaitForExit(20000);
        }
        catch { }
    }
}
