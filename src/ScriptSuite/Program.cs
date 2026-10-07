using Avalonia;
using System;
using System.Runtime.InteropServices;
using ScriptSuite.Models;
using ScriptSuite.Services;

namespace ScriptSuite;

sealed class Program
{
    // flock(2) constants for the scheduled-run single-instance guard.
    private const int LOCK_EX = 2;
    private const int LOCK_NB = 4;
    private const int LOCK_UN = 8;
    private const int O_RDWR = 2;
    private const int O_CREAT = 64;

    [DllImport("libc.so.6", SetLastError = true)]
    private static extern int flock(int fd, int operation);

    // Raw open(2)/close(2): .NET FileStream enforces FileShare on Unix via
    // flock under the hood, so opening the lock file with FileStream would
    // throw before our explicit flock ever runs. Raw fds carry no locks.
    [DllImport("libc.so.6", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int open(string pathname, int flags, uint mode);

    [DllImport("libc.so.6", SetLastError = true)]
    private static extern int close(int fd);

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Headless elevated child (launched via pkexec by PolkitElevationService):
        // run one script's execute phase and write the result JSON for the
        // parent to read back. Mirrors windows-scripts --elevated-run.
        if (args.Length > 0 && args[0] == "--elevated-run")
        {
            Environment.Exit(RunElevatedChild(args));
            return;
        }

        // Headless scheduled run (launched by a systemd user timer):
        // run one script via its saved config and record into ScheduledRuns.
        // A held flock means the dashboard (or another trigger) is mid-run:
        // record SkippedBusy instead of queueing, like windows-scripts.
        if (args.Length > 1 && args[0] == "--scheduled-run")
        {
            Environment.Exit(RunScheduled(args[1]));
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static int RunElevatedChild(string[] args)
    {
        string? scriptId = null, configPath = null, resultPath = null;
        string? liveLogPath = null, includeOnlyPath = null;
        for (int i = 1; i < args.Length; i++)
        {
            string? next = i + 1 < args.Length ? args[i + 1] : null;
            switch (args[i])
            {
                case "--config-path" when next is not null: configPath = next; i++; break;
                case "--result-path" when next is not null: resultPath = next; i++; break;
                case "--live-log" when next is not null: liveLogPath = next; i++; break;
                case "--include-only" when next is not null: includeOnlyPath = next; i++; break;
                default:
                    if (scriptId is null && !args[i].StartsWith("--"))
                        scriptId = args[i];
                    break;
            }
        }
        if (scriptId is null || configPath is null || resultPath is null)
        {
            Console.Error.WriteLine("Usage: ScriptSuite --elevated-run <id> --config-path P --result-path P [--live-log P] [--include-only P]");
            return 2;
        }

        var catalog = new ManifestCatalog(AppPaths.ManifestsDir);
        var consents = new RiskConsentStore(AppPaths.RiskConsentsPath);
        var elevation = new PolkitElevationService(catalog, consents);
        return elevation.RunElevatedChild(scriptId, configPath, resultPath, liveLogPath, includeOnlyPath);
    }

    /// <summary>Scheduled-run entrypoint: executes one script headless and
    /// records the outcome in ScheduledRuns (never in manual RunHistory).
    /// Returns 0 on Success/Warning/SkippedBusy, 1 on Failed/unknown script.</summary>
    private static int RunScheduled(string scriptId)
    {
        var startedAt = DateTime.Now;
        var history = new RunHistoryStore(AppPaths.HistoryDbPath);
        int lockFd = -1;
        try
        {
            Directory.CreateDirectory(AppPaths.DataRoot);
            lockFd = open(Path.Combine(AppPaths.DataRoot, "scheduled.lock"), O_RDWR | O_CREAT, 0x1A4);
            if (lockFd < 0)
                throw new System.IO.IOException("Cannot open scheduled lock file.");
            if (flock(lockFd, LOCK_EX | LOCK_NB) != 0)
            {
                history.InsertScheduled(scriptId, startedAt, startedAt, DateTime.Now,
                    "SkippedBusy", "Skipped — app was busy.", "timer");
                Console.WriteLine($"Scheduled run {scriptId}: skipped — app was busy.");
                return 0;
            }

            var catalog = new ManifestCatalog(AppPaths.ManifestsDir);
            if (catalog.Find(scriptId) is null)
            {
                history.InsertScheduled(scriptId, startedAt, startedAt, DateTime.Now,
                    "Failed", $"Unknown script id: {scriptId}.", "timer");
                Console.Error.WriteLine($"Scheduled run: unknown script id '{scriptId}'.");
                return 1;
            }

            var executor = new BashScriptExecutor(catalog);
            var result = executor.Execute(scriptId, AppPaths.ConfigPathFor(scriptId), dryRun: false);
            string outcome = result.Outcome switch
            {
                RunOutcome.Success => "Success",
                RunOutcome.Warning => "Warning",
                RunOutcome.Cancelled => "Cancelled",
                _ => "Failed",
            };
            history.InsertScheduled(scriptId, startedAt, startedAt, DateTime.Now,
                outcome, RunHistoryStore.BuildSummary(result.Logs), "timer");
            foreach (var line in result.Logs)
                Console.WriteLine(line);
            return result.Outcome == RunOutcome.Failed ? 1 : 0;
        }
        catch (Exception ex)
        {
            try
            {
                history.InsertScheduled(scriptId, startedAt, startedAt, DateTime.Now,
                    "Failed", "Scheduled runner error: " + ex.Message, "timer");
            }
            catch { }
            Console.Error.WriteLine($"Scheduled run {scriptId} failed: {ex.Message}");
            return 1;
        }
        finally
        {
            if (lockFd >= 0)
            {
                try { flock(lockFd, LOCK_UN); } catch { }
                try { close(lockFd); } catch { }
            }
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
