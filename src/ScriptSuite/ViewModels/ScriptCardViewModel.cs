using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScriptSuite.Models;
using ScriptSuite.Services;

namespace ScriptSuite.ViewModels;

/// <summary>One dashboard card: a script plus its live schedule/last-run state.
/// Run executes on a worker thread (blocking pkexec auth dialogs must never
/// sit on the UI thread) and records into manual RunHistory; Schedule opens
/// the schedule dialog through the parent-provided hook so this ViewModel
/// stays window-free and headless-testable.</summary>
public partial class ScriptCardViewModel : ViewModelBase
{
    private readonly BashScriptExecutor _executor;
    private readonly PolkitElevationService _elevation;
    private readonly RunHistoryStore _history;
    private readonly ScheduleStore _schedules;
    private readonly RiskConsentStore _consents;
    private readonly string _configPath;
    private readonly Func<ScriptCardViewModel, Task> _openSchedule;

    public ScriptManifest Manifest { get; }
    public string DisplayName => Manifest.DisplayName;
    public string Description => Manifest.Description;
    public bool RequiresAdmin => Manifest.RequiresAdmin;

    [ObservableProperty] private bool _hasSchedule;
    [ObservableProperty] private string _scheduleSummary = "";
    [ObservableProperty] private string _lastRunText = "Never run";
    [ObservableProperty] private string _runStatus = "";
    [ObservableProperty] private bool _isRunning;

    public event EventHandler? Ran;

    public ScriptCardViewModel(
        ScriptManifest manifest,
        BashScriptExecutor executor,
        PolkitElevationService elevation,
        RunHistoryStore history,
        ScheduleStore schedules,
        RiskConsentStore consents,
        string configPath,
        Func<ScriptCardViewModel, Task> openSchedule)
    {
        Manifest = manifest;
        _executor = executor;
        _elevation = elevation;
        _history = history;
        _schedules = schedules;
        _consents = consents;
        _configPath = configPath;
        _openSchedule = openSchedule;
        RefreshSchedule();
    }

    /// <summary>Risk opt-in for admin scripts (ClearJournal). Survives
    /// unschedule — stored separately, like windows-scripts.</summary>
    public bool RiskConsented
    {
        get => _consents.HasConsent(Manifest.Id);
        set
        {
            _consents.SetConsent(Manifest.Id, value);
            OnPropertyChanged();
        }
    }

    public void RefreshSchedule()
    {
        var entry = _schedules.Get(Manifest.Id);
        HasSchedule = entry is not null;
        ScheduleSummary = entry is not null ? "◷ " + SystemdScheduleService.SummaryFor(entry) : "";
    }

    public void RefreshLastRun(IReadOnlyDictionary<string, RunHistoryEntry> latest)
    {
        LastRunText = latest.TryGetValue(Manifest.Id, out var e)
            ? $"{e.Outcome} — {e.StartedAt}{(string.IsNullOrWhiteSpace(e.Summary) ? "" : ": " + e.Summary)}"
            : "Never run";
    }

    [RelayCommand]
    public async Task RunAsync()
    {
        if (IsRunning) return;
        if (RequiresAdmin && !RiskConsented)
        {
            RunStatus = "Check 'I understand the risks' first.";
            return;
        }
        IsRunning = true;
        RunStatus = "Running…";
        try
        {
            var started = DateTime.Now;
            ScriptRunResult result = RequiresAdmin
                ? await Task.Run(() => _elevation.RunElevated(Manifest.Id, _configPath))
                : await Task.Run(() => _executor.Execute(Manifest.Id, _configPath, dryRun: false));
            var finished = DateTime.Now;
            _history.Insert(Manifest.Id, started, finished, result.Outcome,
                RunHistoryStore.BuildSummary(result.Logs));
            RunStatus = $"{result.Outcome} ({(finished - started).TotalSeconds:0}s) — see Recent runs.";
            Ran?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            RunStatus = "Failed to start: " + ex.Message;
        }
        finally
        {
            IsRunning = false;
        }
    }

    [RelayCommand]
    public Task OpenScheduleAsync() => _openSchedule(this);
}
