using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScriptSuite.Services;

namespace ScriptSuite.ViewModels;

/// <summary>Stage 1 scaffold + Stage 2 self-test: proves AppPaths (XDG), config
/// seeding, manifest catalog load, history DB init, theme store, and — new in
/// Stage 2 — the full TempCleanup pipeline (config → Bash → logs → history).
/// Full dashboard (tiles, run, settings, history) lands in later stages.</summary>
public partial class MainViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _greeting = "ScriptSuite (Linux scaffold — Stage 2: TempCleanup E2E)";

    [ObservableProperty]
    private string _statusLines = "initializing…";

    [ObservableProperty]
    private string _stage2Log = "Stage 2 self-test not run yet.";

    [ObservableProperty]
    private bool _isSelfTestRunning;

    public MainViewModel()
    {
        try
        {
            AppPaths.EnsureConfigsSeeded();
            var catalog = new ManifestCatalog(AppPaths.ManifestsDir);
            var history = new RunHistoryStore(AppPaths.HistoryDbPath);
            var theme = new ThemeStore(AppPaths.ThemePath);
            var recent = history.GetRecent(1);
            StatusLines =
                $"Config : {AppPaths.AppConfigRoot}\n" +
                $"Data   : {AppPaths.DataRoot}\n" +
                $"Manifests: {AppPaths.ManifestsDir} ({catalog.All.Count} loaded)\n" +
                $"History: {AppPaths.HistoryDbPath} ({recent.Count} recent shown, table ready)\n" +
                $"Theme  : {theme.Theme}\n" +
                $"Scripts: {string.Join(", ", catalog.All.Select(m => m.Id))}";
        }
        catch (Exception ex)
        {
            StatusLines = $"Scaffold init failed: {ex.Message}";
        }
    }

    /// <summary>End-to-end proof of the Stage 2 pipeline against a scratch dir
    /// (never the real /tmp): seed old/new/ignored files, dry-run, real run,
    /// include-only run, history insert, verify, clean up.</summary>
    [RelayCommand]
    public async Task RunSelfTestAsync()
    {
        if (IsSelfTestRunning) return;
        IsSelfTestRunning = true;
        Stage2Log = "Running Stage 2 self-test…";
        try
        {
            var lines = await Task.Run(() => Stage2SelfTest.Run());
            Stage2Log = string.Join("\n", lines);
        }
        catch (Exception ex)
        {
            Stage2Log = $"SELF-TEST CRASHED: {ex.Message}";
        }
        finally
        {
            IsSelfTestRunning = false;
        }
    }
}
