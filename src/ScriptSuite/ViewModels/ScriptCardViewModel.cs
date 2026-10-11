using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScriptSuite.Converters;
using ScriptSuite.Models;
using ScriptSuite.Services;

namespace ScriptSuite.ViewModels;

/// <summary>One dashboard card: a script plus its live schedule/last-run state.
/// Run opens the Run window through the parent-provided hook (live output,
/// preview, cancel); with no hook (headless tests) it runs inline via
/// RunInlineAsync. Window-free and headless-testable either way.</summary>
public partial class ScriptCardViewModel : ViewModelBase
{
    private readonly BashScriptExecutor _executor;
    private readonly PolkitElevationService _elevation;
    private readonly RunHistoryStore _history;
    private readonly ScheduleStore _schedules;
    private readonly RiskConsentStore _consents;
    private readonly string _configPath;
    private readonly Func<ScriptCardViewModel, Task> _openSchedule;
    private readonly Func<ScriptCardViewModel, Task> _openRun;
    private readonly Func<ScriptCardViewModel, Task> _openSettings;
    private readonly DashboardStateStore? _dashboard;

    public ScriptManifest Manifest { get; }
    public string DisplayName => Manifest.DisplayName;
    public string Description => Manifest.Description;
    public bool RequiresAdmin => Manifest.RequiresAdmin;

    [ObservableProperty] private bool _hasSchedule;
    [ObservableProperty] private string _scheduleSummary = "";
    [ObservableProperty] private bool _hasLastRun;
    [ObservableProperty] private string _lastRunAgo = "";
    [ObservableProperty] private string _lastRunOutcome = "";
    [ObservableProperty] private string _runStatus = "";
    [ObservableProperty] private bool _isRunning;

    /// <summary>Dashboard "Run All" include checkbox. Persisted immediately
    /// to dashboard.json (Windows parity); null store = headless default true.</summary>
    [ObservableProperty] private bool _runAllIncluded = true;

    partial void OnRunAllIncludedChanged(bool value) =>
        _dashboard?.SetRunAll(Manifest.Id, value);

    /// <summary>Tile hidden flag. Persisted immediately to dashboard.json
    /// (Windows parity); null store = headless default false.</summary>
    [ObservableProperty] private bool _isHidden;

    /// <summary>Pushed by the parent when the global "Show hidden" toggles.</summary>
    [ObservableProperty] private bool _showHidden;

    /// <summary>Hidden tiles vanish unless the dashboard shows hidden ones.</summary>
    public bool IsVisible => !IsHidden || ShowHidden;

    /// <summary>Hidden-but-shown tiles render dimmed (Windows parity: 0.55).</summary>
    public double TileOpacity => IsHidden ? 0.55 : 1.0;

    partial void OnIsHiddenChanged(bool value)
    {
        _dashboard?.SetHidden(Manifest.Id, value);
        OnPropertyChanged(nameof(IsVisible));
        OnPropertyChanged(nameof(TileOpacity));
    }

    partial void OnShowHiddenChanged(bool value) => OnPropertyChanged(nameof(IsVisible));

    public event EventHandler? Ran;

    public ScriptCardViewModel(
        ScriptManifest manifest,
        BashScriptExecutor executor,
        PolkitElevationService elevation,
        RunHistoryStore history,
        ScheduleStore schedules,
        RiskConsentStore consents,
        string configPath,
        Func<ScriptCardViewModel, Task> openSchedule,
        Func<ScriptCardViewModel, Task>? openRun = null,
        DashboardStateStore? dashboard = null,
        Func<ScriptCardViewModel, Task>? openSettings = null)
    {
        Manifest = manifest;
        _executor = executor;
        _elevation = elevation;
        _history = history;
        _schedules = schedules;
        _consents = consents;
        _configPath = configPath;
        _openSchedule = openSchedule;
        _openRun = openRun ?? (card => card.RunInlineAsync());
        _openSettings = openSettings ?? (_ => Task.CompletedTask);
        _dashboard = dashboard;
        _runAllIncluded = dashboard?.IsRunAllEnabled(manifest.Id) ?? true;
        _isHidden = dashboard?.IsHidden(manifest.Id) ?? false;
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

    /// <summary>Latest MANUAL run for this script (RunHistory holds manual
    /// runs only; timer and self-test rows live in ScheduledRuns/scratch
    /// DBs and never surface here). Ported from windows-scripts.</summary>
    public void RefreshLastRun(IReadOnlyDictionary<string, RunHistoryEntry> latest)
    {
        if (latest.TryGetValue(Manifest.Id, out var e))
        {
            HasLastRun = true;
            LastRunAgo = FormatAgo(e.StartedAt);
            LastRunOutcome = e.Outcome;
        }
        else
        {
            HasLastRun = false;
            LastRunAgo = "";
            LastRunOutcome = "";
        }
    }

    /// <summary>Badge style-class predicates for the last-run outcome
    /// (App.axaml badge-* styles follow the theme live).</summary>
    public bool IsSuccessBadge => Converters.OutcomeBadge.IsSuccess(LastRunOutcome);
    public bool IsWarningBadge => Converters.OutcomeBadge.IsWarning(LastRunOutcome);
    public bool IsErrorBadge => Converters.OutcomeBadge.IsError(LastRunOutcome);
    public bool IsInfoBadge => Converters.OutcomeBadge.IsInfo(LastRunOutcome);

    partial void OnLastRunOutcomeChanged(string value)
    {
        OnPropertyChanged(nameof(IsSuccessBadge));
        OnPropertyChanged(nameof(IsWarningBadge));
        OnPropertyChanged(nameof(IsErrorBadge));
        OnPropertyChanged(nameof(IsInfoBadge));
    }

    /// <summary>Relative "8 days ago" wording, ported verbatim from
    /// windows-scripts ScriptRowViewModel.FormatAgo (same storage format
    /// "yyyy-MM-dd HH:mm:ss", same fallbacks).</summary>
    internal static string FormatAgo(string? startedAt)
    {
        if (string.IsNullOrWhiteSpace(startedAt))
            return "";
        if (!DateTime.TryParseExact(startedAt.Trim(), "yyyy-MM-dd HH:mm:ss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var when))
            return startedAt.Length >= 10 ? startedAt[..10] : startedAt;
        var span = DateTime.Now - when;
        if (span.TotalSeconds < 0) return when.ToString("yyyy-MM-dd");
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} minute{((int)span.TotalMinutes == 1 ? "" : "s")} ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours} hour{((int)span.TotalHours == 1 ? "" : "s")} ago";
        if (span.TotalDays < 1.5) return "yesterday";
        if (span.TotalDays < 30) return $"{(int)span.TotalDays} days ago";
        return when.ToString("yyyy-MM-dd");
    }

    [RelayCommand]
    public Task RunAsync()
    {
        if (IsRunning) return Task.CompletedTask;
        return _openRun(this);
    }

    /// <summary>Legacy inline run (headless path): executes on a worker
    /// thread and records into manual RunHistory. Production uses the Run
    /// window instead; the window inserts history itself.</summary>
    public async Task RunInlineAsync()
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

    [RelayCommand]
    public Task OpenSettingsAsync() => _openSettings(this);

    [RelayCommand]
    public void Hide()
    {
        if (IsRunning) return;
        IsHidden = true;
    }

    [RelayCommand]
    public void Show()
    {
        if (IsRunning) return;
        IsHidden = false;
    }
}
