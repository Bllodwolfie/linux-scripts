using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScriptSuite.Models;
using ScriptSuite.Services;

namespace ScriptSuite.ViewModels;

/// <summary>Stage 7b dashboard: one card per script (Run, Schedule, live
/// schedule summary, last manual run), Recent runs + Scheduled runs history,
/// and the self-test runner. No settings editors by design (configs stay
/// file-edited) — see the 7b scoping decision. All stores are injectable so
/// Stage7bSelfTest can run the whole dashboard layer against temp paths.</summary>
public partial class MainViewModel : ViewModelBase
{
    private readonly ManifestCatalog _catalog;
    private readonly BashScriptExecutor _executor;
    private readonly PolkitElevationService _elevation;
    private readonly RunHistoryStore _history;
    private readonly Func<string, string> _configPathFor;
    private readonly RiskConsentStore _consents;

    public ScheduleStore Schedules { get; }
    public RiskConsentStore Consents { get; }

    public ObservableCollection<ScriptCardViewModel> Cards { get; } = new();
    public ObservableCollection<string> RecentRuns { get; } = new();
    public ObservableCollection<string> RecentScheduled { get; } = new();

    [ObservableProperty] private string _greeting = "ScriptSuite (Linux — Stage 7b: dashboard + package)";
    [ObservableProperty] private string _stage2Log = "Self-tests not run yet.";
    [ObservableProperty] private bool _isSelfTestRunning;

    /// <summary>Set by the view: opens the schedule dialog for a card and
    /// returns whether the schedule changed. Null in headless contexts.</summary>
    public Func<ScriptCardViewModel, Task<bool>>? ShowScheduleDialog { get; set; }

    public MainViewModel(
        ManifestCatalog? catalog = null,
        BashScriptExecutor? executor = null,
        PolkitElevationService? elevation = null,
        RunHistoryStore? history = null,
        ScheduleStore? schedules = null,
        RiskConsentStore? consents = null,
        Func<string, string>? configPathFor = null)
    {
        bool production = catalog is null;
        if (production)
            AppPaths.EnsureConfigsSeeded();

        _catalog = catalog ?? new ManifestCatalog(AppPaths.ManifestsDir);
        _executor = executor ?? new BashScriptExecutor(_catalog);
        _consents = consents ?? new RiskConsentStore(AppPaths.RiskConsentsPath);
        _elevation = elevation ?? new PolkitElevationService(_catalog, _consents);
        _history = history ?? new RunHistoryStore(AppPaths.HistoryDbPath);
        Schedules = schedules ?? new ScheduleStore(AppPaths.SchedulesPath);
        Consents = _consents;
        _configPathFor = configPathFor ?? AppPaths.ConfigPathFor;

        foreach (var manifest in _catalog.All)
        {
            var card = new ScriptCardViewModel(manifest, _executor, _elevation,
                _history, Schedules, _consents, _configPathFor(manifest.Id),
                OpenScheduleAsync);
            card.Ran += (_, _) => RefreshHistories();
            Cards.Add(card);
        }
        RefreshHistories();
    }

    public async Task OpenScheduleAsync(ScriptCardViewModel card)
    {
        if (ShowScheduleDialog is null) return;
        if (await ShowScheduleDialog(card))
        {
            card.RefreshSchedule();
            RefreshHistories();
        }
    }

    [RelayCommand]
    public void Refresh()
    {
        foreach (var card in Cards)
            card.RefreshSchedule();
        RefreshHistories();
    }

    public void RefreshHistories()
    {
        var latest = _history.GetLatestByScript();
        foreach (var card in Cards)
            card.RefreshLastRun(latest);

        RecentRuns.Clear();
        foreach (var e in _history.GetRecent(10))
            RecentRuns.Add($"{e.StartedAt}  {e.ScriptId}  {e.Outcome}"
                + (string.IsNullOrWhiteSpace(e.Summary) ? "" : $"  — {e.Summary}"));
        if (RecentRuns.Count == 0)
            RecentRuns.Add("No manual runs yet.");

        RecentScheduled.Clear();
        foreach (var e in _history.GetRecentScheduled(10))
            RecentScheduled.Add($"{e.StartedAt}  {e.ScriptId}  {e.Outcome}  ({e.Trigger})"
                + (string.IsNullOrWhiteSpace(e.Summary) ? "" : $"  — {e.Summary}"));
        if (RecentScheduled.Count == 0)
            RecentScheduled.Add("No scheduled runs yet.");
    }

    [RelayCommand]
    public async Task RunSelfTestAsync()
    {
        if (IsSelfTestRunning) return;
        IsSelfTestRunning = true; Stage2Log = "Running self-tests…";
        try
        {
            var lines = await Task.Run(() =>
            {
                var all = new List<string> { "== Stage 2: TempCleanup ==" };
                all.AddRange(Stage2SelfTest.Run()); all.Add(""); all.Add("== Stage 3: EmptyTrash ==");
                all.AddRange(Stage3SelfTest.Run()); all.Add(""); all.Add("== Stage 4 (headless): ClearJournal + elevation ==");
                all.AddRange(Stage4SelfTest.Run()); all.Add(""); all.Add("== Stage 5: Downloads/Screenshots/EmptyFolder ==");
                all.AddRange(Stage5SelfTest.Run()); all.Add(""); all.Add("== Stage 6: SoftwareInventory/SystemHealthReport ==");
                all.AddRange(Stage6SelfTest.Run()); all.Add(""); all.Add("== Stage 7a: systemd scheduling ==");
                all.AddRange(Stage7aSelfTest.Run()); all.Add(""); all.Add("== Stage 7b: dashboard ==");
                all.AddRange(Stage7bSelfTest.Run()); return all;
            });
            Stage2Log = string.Join("\n", lines);
        }
        catch (Exception ex) { Stage2Log = $"SELF-TEST CRASHED: {ex.Message}"; }
        finally { IsSelfTestRunning = false; }
    }
}
