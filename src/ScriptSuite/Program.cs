using Avalonia;
using System;
using ScriptSuite.Services;

namespace ScriptSuite;

sealed class Program
{
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
