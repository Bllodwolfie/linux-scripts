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
    private string _greeting = "ScriptSuite (Linux — Stage 5: file scripts E2E)";

    [ObservableProperty]
    private string _statusLines = "initializing…";

    [ObservableProperty]
    private string _stage2Log = "Self-tests not run yet (Stages 2–4). Interactive elevation checks (real Cancel/Allow clicks) run outside this button.";

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

    /// <summary>Headless end-to-end proofs (Stages 2–4). TempCleanup runs
    /// against a scratch dir; EmptyTrash against the real home Trash with
    /// gio-trashed test items; ClearJournal against the user journal plus
    /// consent-gate and direct child round-trip checks. The two tests that
    /// need a real polkit dialog (Cancel/Allow clicks) are interactive and
    /// live outside this button by design.</summary>
    [RelayCommand]
    public async Task RunSelfTestAsync()
    {
        if (IsSelfTestRunning) return;
        IsSelfTestRunning = true;
        Stage2Log = "Running self-tests…";
        try
        {
            var lines = await Task.Run(() =>
            {
                var all = new List<string> { "== Stage 2: TempCleanup ==" };
                all.AddRange(Stage2SelfTest.Run());
                all.Add("");
                all.Add("== Stage 3: EmptyTrash ==");
                all.AddRange(Stage3SelfTest.Run());
                all.Add("");
                all.Add("== Stage 4 (headless): ClearJournal + elevation ==");
                all.AddRange(Stage4SelfTest.Run());
                all.Add("");
                all.Add("== Stage 5: Downloads/Screenshots/EmptyFolder ==");
                all.AddRange(Stage5SelfTest.Run());
                return all;
            });
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
