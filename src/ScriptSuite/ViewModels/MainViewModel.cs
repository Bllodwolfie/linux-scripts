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
    private readonly DashboardStateStore _dashboard;

    public ScheduleStore Schedules { get; }
    public RiskConsentStore Consents { get; }

    public ObservableCollection<ScriptCardViewModel> Cards { get; } = new();
    public ObservableCollection<ScriptCategoryViewModel> Categories { get; } = new();
    public ObservableCollection<string> RecentRuns { get; } = new();
    public ObservableCollection<string> RecentScheduled { get; } = new();

    [ObservableProperty] private string _stage2Log = "Self-tests not run yet.";
    [ObservableProperty] private bool _isSelfTestRunning;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _runAllEnabled;

    /// <summary>Header theme toggle state/tooltip (ThemeManager is the source
    /// of truth; refreshed on toggle and at construction). The toggle is
    /// icon-only (moon in Dark, sun in Latte), like windows-scripts.</summary>
    [ObservableProperty] private bool _isDarkTheme = true;
    [ObservableProperty] private string _themeTip = "Switch to Latte theme";

    /// <summary>Dashboard overview panel (the Windows ? panel exists but its
    /// text is never set — here it carries a real overview).</summary>
    [ObservableProperty] private bool _dashboardHelpVisible;

    public string DashboardHelpText =>
        "Each tile is one script: Run opens the run window (dry-run preview with checkboxes, "
        + "live output, Cancel, outcome banner), Settings edits its config file, Schedule sets "
        + "a systemd user timer, Hide removes the tile (Show hidden brings it back dimmed). "
        + "Run All runs every checked, visible script in one batch — EmptyTrash first, admin scripts "
        + "only with risk consent. History lists manual runs with their log files; Scheduled lists "
        + "timer runs. Every change saves immediately; use the ? panels for per-window details.";

    [RelayCommand]
    public void ToggleTheme()
    {
        ThemeManager.Apply(ThemeManager.CurrentTheme == "Dark" ? "Latte" : "Dark");
        RefreshThemeState();
    }

    [RelayCommand]
    public void ToggleDashboardHelp() => DashboardHelpVisible = !DashboardHelpVisible;

    private void RefreshThemeState()
    {
        bool dark = ThemeManager.CurrentTheme == "Dark";
        IsDarkTheme = dark;
        ThemeTip = dark ? "Switch to Latte theme" : "Switch to Dark theme";
    }

    /// <summary>Global "Show hidden" toggle: reveals hidden tiles in place
    /// (dimmed, with a Show button). Pushed into every card.</summary>
    [ObservableProperty] private bool _showHidden;

    partial void OnShowHiddenChanged(bool value)
    {
        foreach (var card in Cards)
            card.ShowHidden = value;
    }

    /// <summary>Set by the view: opens the schedule dialog for a card and
    /// returns whether the schedule changed. Null in headless contexts.</summary>
    public Func<ScriptCardViewModel, Task<bool>>? ShowScheduleDialog { get; set; }

    /// <summary>Set by the view: opens the Run window for a run ViewModel.
    /// Null in headless contexts (cards then run inline).</summary>
    public Func<ScriptRunViewModel, Task>? ShowRunWindow { get; set; }

    /// <summary>Set by the view: shows manual run history. Null headless.</summary>
    public Func<HistoryViewModel, Task>? ShowHistory { get; set; }

    /// <summary>Set by the view: shows scheduled-run history. Null headless.</summary>
    public Func<ScheduledHistoryViewModel, Task>? ShowScheduledHistory { get; set; }

    public MainViewModel(
        ManifestCatalog? catalog = null,
        BashScriptExecutor? executor = null,
        PolkitElevationService? elevation = null,
        RunHistoryStore? history = null,
        ScheduleStore? schedules = null,
        RiskConsentStore? consents = null,
        Func<string, string>? configPathFor = null,
        DashboardStateStore? dashboard = null)
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
        _dashboard = dashboard ?? new DashboardStateStore(AppPaths.DashboardStatePath);

        foreach (var manifest in _catalog.All)
        {
            var card = new ScriptCardViewModel(manifest, _executor, _elevation,
                _history, Schedules, _consents, _configPathFor(manifest.Id),
                OpenScheduleAsync, OpenRunAsync, _dashboard, OpenSettingsAsync);
            card.Ran += (_, _) => RefreshHistories();
            card.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ScriptCardViewModel.RunAllIncluded)
                    || e.PropertyName == nameof(ScriptCardViewModel.IsHidden))
                    RefreshRunAllEnabled();
            };
            Cards.Add(card);
        }
        foreach (var (category, scripts) in _catalog.GroupByCategory())
        {
            var cat = new ScriptCategoryViewModel(category);
            foreach (var manifest in scripts)
            {
                var card = Cards.FirstOrDefault(c => c.Manifest.Id == manifest.Id);
                if (card is not null)
                    cat.Rows.Add(card);
            }
            Categories.Add(cat);
        }
        RefreshHistories();
        RefreshRunAllEnabled();
        RefreshThemeState();
    }

    /// <summary>Run All target set: enabled + visible + (non-admin or
    /// explicitly risk-consented). Admin exclusion without consent is a Linux
    /// policy addition — Windows runs them with per-leg UAC instead.</summary>
    public List<ScriptManifest> RunAllOrder() =>
        _catalog.BatchOrder(id =>
            _dashboard.IsRunAllEnabled(id) && !_dashboard.IsHidden(id)
            && (_catalog.Find(id) is not { RequiresAdmin: true } || _consents.HasConsent(id)));

    public void RefreshRunAllEnabled() =>
        RunAllEnabled = _catalog.All.Any(m =>
            _dashboard.IsRunAllEnabled(m.Id) && !_dashboard.IsHidden(m.Id)) && !IsRunning;

    partial void OnIsRunningChanged(bool value) => RefreshRunAllEnabled();

    [RelayCommand]
    public async Task OpenRunAllAsync()
    {
        if (IsRunning) return;
        var order = RunAllOrder();
        if (order.Count == 0 || ShowRunAll is null) return;
        IsRunning = true;
        try
        {
            var runAllVm = new RunAllViewModel(order, _executor, _elevation,
                _consents, _history, _configPathFor);
            await ShowRunAll(runAllVm);
        }
        finally
        {
            IsRunning = false;
            Refresh();
        }
    }

    /// <summary>Set by the view: opens the Run All preview window.
    /// Null in headless contexts.</summary>
    public Func<RunAllViewModel, Task>? ShowRunAll { get; set; }

    /// <summary>Set by the view: opens the Settings window for a script.
    /// Null in headless contexts (settings VMs are tested directly).</summary>
    public Func<ScriptManifest, ScriptConfigService, Task>? ShowSettings { get; set; }

    public Task OpenSettingsAsync(ScriptCardViewModel card) => ShowSettings is null
        ? Task.CompletedTask
        : ShowSettings(card.Manifest, new ScriptConfigService());

    /// <summary>Card Run entry: production opens the Run window (which runs
    /// and records history itself); headless runs inline.</summary>
    public async Task OpenRunAsync(ScriptCardViewModel card)
    {
        if (ShowRunWindow is null)
        {
            await card.RunInlineAsync();
            return;
        }
        var runVm = new ScriptRunViewModel(card.Manifest, _executor, _elevation,
            _consents, _history, _configPathFor(card.Manifest.Id));
        await ShowRunWindow(runVm);
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

    [RelayCommand]
    public Task OpenHistoryAsync() => ShowHistory is null
        ? Task.CompletedTask
        : ShowHistory(new HistoryViewModel(_history, _catalog, LogDetailResolver.Production()));

    [RelayCommand]
    public Task OpenScheduledHistoryAsync() => ShowScheduledHistory is null
        ? Task.CompletedTask
        : ShowScheduledHistory(new ScheduledHistoryViewModel(_history, _catalog));

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
                all.AddRange(Stage7bSelfTest.Run()); all.Add(""); all.Add("== Stage 8: run/history/run-all/hide ==");
                all.AddRange(Stage8SelfTest.Run()); all.Add(""); all.Add("== Stage 9: settings/wizard ==");
                all.AddRange(Stage9SelfTest.Run()); all.Add(""); all.Add("== Stage 10: theme switching ==");
                all.AddRange(Stage10SelfTest.Run()); return all;
            });
            Stage2Log = string.Join("\n", lines);
        }
        catch (Exception ex) { Stage2Log = $"SELF-TEST CRASHED: {ex.Message}"; }
        finally { IsSelfTestRunning = false; }
    }
}
