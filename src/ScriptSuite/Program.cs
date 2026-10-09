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

        // Headless self-tests (CI + `ScriptSuite --self-test`): the same
        // stages 2–9 the dashboard button runs, printed to stdout. Optional
        // second argument limits stages, e.g. `--self-test 8,9`.
        // Exit 0 only if every executed stage ends SELF-TEST PASS.
        if (args.Length > 0 && args[0] == "--self-test")
        {
            string? filter = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : null;
            Environment.Exit(RunSelfTests(filter));
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

    /// <summary>Headless runner for the in-app self-test stages. Prints the
    /// same "== Stage N ==" sections the dashboard button shows.</summary>
    private static int RunSelfTests(string? filter)
    {
        var wanted = filter?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool Want(string n) => wanted is null || wanted.Contains(n);
        var stages = new (string N, string Title, Func<List<string>> Run)[]
        {
            ("2", "Stage 2: TempCleanup", Stage2SelfTest.Run),
            ("3", "Stage 3: EmptyTrash", Stage3SelfTest.Run),
            ("4", "Stage 4 (headless): ClearJournal + elevation", Stage4SelfTest.Run),
            ("5", "Stage 5: Downloads/Screenshots/EmptyFolder", Stage5SelfTest.Run),
            ("6", "Stage 6: SoftwareInventory/SystemHealthReport", Stage6SelfTest.Run),
            ("7a", "Stage 7a: systemd scheduling", Stage7aSelfTest.Run),
            ("7b", "Stage 7b: dashboard", Stage7bSelfTest.Run),
            ("8", "Stage 8: run/history/run-all/hide", Stage8SelfTest.Run),
            ("9", "Stage 9: settings/wizard", Stage9SelfTest.Run),
        };
        bool ok = true;
        foreach (var (n, title, run) in stages)
        {
            if (!Want(n)) continue;
            Console.WriteLine("== " + title + " ==");
            List<string> lines;
            try
            {
                lines = run();
            }
            catch (Exception ex)
            {
                lines = new List<string> { "SELF-TEST CRASHED: " + ex.Message };
            }
            foreach (var line in lines)
                Console.WriteLine(line);
            Console.WriteLine("");
            if (!lines.Any(l => l.Trim() == "SELF-TEST PASS"))
                ok = false;
        }
        return ok ? 0 : 1;
    }
}
