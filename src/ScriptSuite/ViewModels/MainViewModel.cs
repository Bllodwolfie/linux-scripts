using CommunityToolkit.Mvvm.ComponentModel;
using ScriptSuite.Services;

namespace ScriptSuite.ViewModels;

/// <summary>Stage 1 scaffold view-model: proves AppPaths (XDG), config seeding,
/// manifest catalog load, history DB init, and theme store all work on Linux.
/// Full dashboard (tiles, run, settings, history) lands in later stages.</summary>
public partial class MainViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _greeting = "ScriptSuite (Linux scaffold — Stage 1)";

    [ObservableProperty]
    private string _statusLines = "initializing…";

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
}
