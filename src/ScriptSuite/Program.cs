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
        // Last-chance crash recorder first (local file, nothing sent): real
        // unhandled exceptions must leave a full trace, not a bare line.
        CrashLog.Install();

        // Test-only probe (no CLI surface): crash on purpose to prove the
        // recorder end to end. tests/test-crash-log.sh drives this.
        if (Environment.GetEnvironmentVariable("SCRIPTSUITE_CRASH_TEST") == "1")
            throw new InvalidOperationException("SCRIPTSUITE_CRASH_TEST probe");

        // Headless elevated child (launched via pkexec by PolkitElevationService):
        // run one script's execute phase and write the result JSON for the
        // parent to read back. Mirrors windows-scripts --elevated-run.
        if (args.Length > 0 && args[0] == "--elevated-run")
        {
            Environment.Exit(RunElevatedChild(args));
            return;
        }

        // Print-only preview of the exact argv RunElevated would spawn
        // (production path builder, no files, no spawn): keeps tests and the
        // validator pinned to what pkexec really receives.
        if (args.Length > 1 && args[0] == "--preview-elevated")
        {
            Environment.Exit(PreviewElevated(args));
            return;
        }

        // Print-only validation of --elevated-run argv (same shape, no run):
        // runs ElevatedArgValidator and prints ACCEPT, exit 0/2. No side
        // effects — the include file is only read, result paths only checked.
        if (args.Length > 0 && args[0] == "--validate-elevated")
        {
            Environment.Exit(ValidateElevated(args));
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

    /// <summary>Print-only: the exact argv RunElevated would spawn for this
    /// script (production ElevatedPaths builder, no files, no spawn).</summary>
    private static int PreviewElevated(string[] args)
    {
        string scriptId = args[1];
        string? configPath = null;
        for (int i = 2; i < args.Length; i++)
            if (args[i] == "--config-path" && i + 1 < args.Length) { configPath = args[i + 1]; i++; }
        var catalog = new ManifestCatalog(AppPaths.ManifestsDir);
        var consents = new RiskConsentStore(AppPaths.RiskConsentsPath);
        var elevation = new PolkitElevationService(catalog, consents);
        try
        {
            foreach (var a in elevation.PreviewArgv(scriptId, configPath ?? AppPaths.ConfigPathFor(scriptId)))
                Console.WriteLine(a);
            return 0;
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine("Preview refused: " + ex.Message);
            return 2;
        }
    }

    /// <summary>Print-only: run ElevatedArgValidator over --elevated-run
    /// shaped argv and print ACCEPT. Exit 0 accepted, 2 refused.</summary>
    private static int ValidateElevated(string[] args)
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
            Console.Error.WriteLine("Usage: ScriptSuite --validate-elevated <id> --config-path P --result-path P [--live-log P] [--include-only P]");
            return 2;
        }
        var catalog = new ManifestCatalog(AppPaths.ManifestsDir);
        try
        {
            ElevatedArgValidator.Validate(scriptId, configPath, resultPath,
                liveLogPath, includeOnlyPath, catalog,
                ElevatedArgValidator.TryGetPrivilegedCaller());
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine("Validate refused: " + ex.Message);
            return 2;
        }
        Console.WriteLine("ACCEPT");
        return 0;
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
        try
        {
            // Child-side argv validation (G-1): polkit authorizes the binary
            // + argv1 only, so hostile argv2+ must die here, not in the parent.
            ElevatedArgValidator.Validate(scriptId, configPath, resultPath,
                liveLogPath, includeOnlyPath, catalog,
                ElevatedArgValidator.TryGetPrivilegedCaller());
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine("Elevated child refused: " + ex.Message);
            return 2;
        }

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
        // E6: the store opens resiliently (corrupt/unwritable history.db
        // degrades to a temp database + warning, never a startup crash) and
        // every later write goes through Record (a dead store must not kill
        // a scheduled job either — e.g. a db that turned read-only after a
        // successful open).
        RunHistoryStore? history;
        try
        {
            var (store, warning) = RunHistoryStore.OpenResilient(AppPaths.HistoryDbPath);
            history = store;
            if (warning is not null)
                Console.Error.WriteLine($"Scheduled run {scriptId}: history unavailable — {warning}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Scheduled run {scriptId}: history unavailable ({ex.Message}) — continuing without recording.");
            history = null;
        }
        void Record(string outcome, string summary)
        {
            if (history is null) return;
            try
            {
                history.InsertScheduled(scriptId, startedAt, startedAt, DateTime.Now,
                    outcome, summary, "timer");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Scheduled run {scriptId}: history unavailable (cannot record outcome: {ex.Message}).");
            }
        }
        int lockFd = -1;
        try
        {
            Directory.CreateDirectory(AppPaths.DataRoot);
            lockFd = open(Path.Combine(AppPaths.DataRoot, "scheduled.lock"), O_RDWR | O_CREAT, 0x1A4);
            if (lockFd < 0)
                throw new System.IO.IOException("Cannot open scheduled lock file.");
            if (flock(lockFd, LOCK_EX | LOCK_NB) != 0)
            {
                Record("SkippedBusy", "Skipped — app was busy.");
                Console.WriteLine($"Scheduled run {scriptId}: skipped — app was busy.");
                return 0;
            }

            var catalog = new ManifestCatalog(AppPaths.ManifestsDir);
            if (catalog.Find(scriptId) is null)
            {
                Record("Failed", $"Unknown script id: {scriptId}.");
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
            Record(outcome, RunHistoryStore.BuildSummary(result.Logs));
            foreach (var line in result.Logs)
                Console.WriteLine(line);
            return result.Outcome == RunOutcome.Failed ? 1 : 0;
        }
        catch (Exception ex)
        {
            Record("Failed", "Scheduled runner error: " + ex.Message);
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
    // NOTE: .WithDeveloperTools() was removed — Avalonia 11.3 dropped that
    // API from the Diagnostics package (devtools moved to
    // AvaloniaUI.DiagnosticsSupport). Kept out so Debug builds compile.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
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
            ("10", "Stage 10: theme switching", Stage10SelfTest.Run),
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
