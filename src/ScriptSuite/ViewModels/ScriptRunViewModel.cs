using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScriptSuite.Converters;
using ScriptSuite.Models;
using ScriptSuite.Services;

namespace ScriptSuite.ViewModels;

/// <summary>Run window logic: dry-run preview with per-item checkboxes (only
/// where the manifest declares supportsDryRun — report-only scripts run
/// immediately on open, like windows-scripts), live streamed output, Cancel,
/// and an outcome banner. The window inserts the finished run into manual
/// RunHistory; the dashboard refreshes when the dialog closes.
/// Linux deviations from windows-scripts: a real Cancel button exists (kills
/// the bash child tree; Windows has none), elevation wording is polkit, and
/// window-close mid-run is refused instead of allowed (Windows only blocked
/// the Close button + Escape).
/// Window-free and headless-testable: live lines are marshalled through an
/// injectable Action (UI thread in production, direct invoke in tests).</summary>
public partial class ScriptRunViewModel : ViewModelBase
{
    public enum Phase
    {
        LoadingPreview,
        Preview,
        Running,
        Finished,
    }

    private readonly ScriptManifest _manifest;
    private readonly BashScriptExecutor _executor;
    private readonly PolkitElevationService _elevation;
    private readonly RiskConsentStore _consents;
    private readonly RunHistoryStore _history;
    private readonly string _configPath;
    private readonly Action<Action> _marshal;
    private CancellationTokenSource? _cts;

    [ObservableProperty] private Phase _currentPhase = Phase.LoadingPreview;
    [ObservableProperty] private string _previewMessage = "Loading preview…";
    [ObservableProperty] private string _selectionCountText = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _confirmEnabled;
    [ObservableProperty] private bool _cancelVisible;
    [ObservableProperty] private bool _closeEnabled = true;
    [ObservableProperty] private bool _previewVisible = true;
    [ObservableProperty] private bool _helpVisible;

    public ObservableCollection<RunSelectionItem> Selections { get; } = new();
    public ObservableCollection<string> LogLines { get; } = new();
    /// <summary>Non-item preview lines (Rule, WARNs, export plan): the dry-run
    /// result's log output minus the per-item rows already shown as
    /// checkboxes. Rendered in the preview pane so the user sees retention,
    /// warnings and the export destination BEFORE Confirm.</summary>
    public ObservableCollection<string> PreviewNotes { get; } = new();

    /// <summary>False for global-effect scripts (ClearJournal): rows render
    /// informational, Confirm needs no selection, selection never narrows.</summary>
    public bool SelectionEnabled => _manifest.SupportsSelection;

    /// <summary>Badge style-class predicates for the outcome line
    /// (App.axaml badge-* styles follow the theme live).</summary>
    public bool IsSuccessBadge => OutcomeBadge.IsSuccess(StatusText);
    public bool IsWarningBadge => OutcomeBadge.IsWarning(StatusText);
    public bool IsErrorBadge => OutcomeBadge.IsError(StatusText);
    public bool IsInfoBadge => OutcomeBadge.IsInfo(StatusText);

    partial void OnStatusTextChanged(string value)
    {
        OnPropertyChanged(nameof(IsSuccessBadge));
        OnPropertyChanged(nameof(IsWarningBadge));
        OnPropertyChanged(nameof(IsErrorBadge));
        OnPropertyChanged(nameof(IsInfoBadge));
    }

    /// <summary>Close the hosting window. bool = unused; kept for parity
    /// with the dialog Close/Cancel/Remove pattern.</summary>
    public event Action? RequestClose;

    public ScriptRunViewModel(
        ScriptManifest manifest,
        BashScriptExecutor executor,
        PolkitElevationService elevation,
        RiskConsentStore consents,
        RunHistoryStore history,
        string configPath,
        Action<Action>? marshal = null)
    {
        _manifest = manifest;
        _executor = executor;
        _elevation = elevation;
        _consents = consents;
        _history = history;
        _configPath = configPath;
        _marshal = marshal ?? (a => Dispatcher.UIThread.Post(a));
    }

    public string Title => "Run Script";
    public string DisplayName => _manifest.DisplayName;
    public string Description => _manifest.DisplayName + " — " + _manifest.Description;
    public bool ShowAdminBadge => _manifest.RequiresAdmin;
    public string AdminNote => "Runs elevated (polkit prompt appears when you confirm).";
    public string HelpText => "Preview shows what would happen — Delete means to the XDG Trash "
        + "(you can restore), Skip means left alone, MoveTo means moved to another folder. "
        + "Uncheck any item to skip it for this run. Confirm is the point of no return — "
        + "it runs for real. Live output appears in Output below and a summary is saved to Run History.";
    public bool IsRunning => CurrentPhase == Phase.Running;

    /// <summary>Window-open entry point (call once, from the view's Opened).
    /// Preview path for dry-run-capable scripts; immediate run otherwise.</summary>
    public async Task LoadAsync()
    {
        if (_manifest.SupportsDryRun)
            await LoadPreviewAsync();
        else
            await RunAsync(null);
    }

    private async Task LoadPreviewAsync()
    {
        CurrentPhase = Phase.LoadingPreview;
        PreviewMessage = "Loading preview…";
        ConfirmEnabled = false;
        var (items, result) = await Task.Run(() =>
            _executor.GetDryRunItems(_manifest.Id, _configPath));
        foreach (var item in items)
        {
            var row = new RunSelectionItem(item) { Selectable = SelectionEnabled };
            row.PropertyChanged += OnSelectionChanged;
            Selections.Add(row);
        }
        PreviewNotes.Clear();
        foreach (var line in result.Logs)
        {
            // Per-item rows already render as checkboxes; everything else
            // (Rule, WARNs, export plan, summaries) belongs in the notes.
            if (line.StartsWith("OUTPUT: Action=", StringComparison.Ordinal)) continue;
            PreviewNotes.Add(line);
        }
        if (items.Count == 0 && result.Outcome == RunOutcome.Failed)
        {
            PreviewMessage = "Preview failed: " + FirstErrorLine(result.Logs);
            CurrentPhase = Phase.Preview;
            ConfirmEnabled = false;
            return;
        }
        if (items.Count == 0)
            PreviewMessage = "Nothing to do right now.";
        else
            PreviewMessage = "";
        CurrentPhase = Phase.Preview;
        UpdateSelectionCount();
    }

    private void OnSelectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RunSelectionItem.IsChecked))
            UpdateSelectionCount();
    }

    private void UpdateSelectionCount()
    {
        int sel = Selections.Count(s => s.IsChecked);
        SelectionCountText = $"{sel} of {Selections.Count} item(s) selected";
        ConfirmEnabled = CurrentPhase is Phase.Preview or Phase.Finished
            && !IsRunning && (!SelectionEnabled || sel > 0);
    }

    [RelayCommand]
    public void ToggleHelp() => HelpVisible = !HelpVisible;

    [RelayCommand]
    public async Task ConfirmAsync()
    {
        if (IsRunning) return;
        List<string> includeOnly;
        if (!SelectionEnabled)
        {
            // Global-effect script: the preview is informational, selection
            // never narrows (ClearJournal ignores --include-only-file).
            includeOnly = new List<string>();
        }
        else
        {
            var checkedRows = Selections.Where(s => s.IsChecked).ToList();
            if (checkedRows.Count == 0) return; // nothing checked (UI disables Confirm too)
            // Defence in depth (S1): Skip rows must never reach the include
            // file for any script — the scripts re-derive their verdicts,
            // but the transport must not contradict the preview either.
            includeOnly = checkedRows.Where(s => s.Item.Action != "Skip")
                .Select(s => s.Target).ToList();
            if (checkedRows.Count > 0 && includeOnly.Count == 0)
            {
                // Only Skip rows checked: nothing actionable. Honest no-op —
                // no spawn, no history (nothing ran); stay in Preview.
                StatusText = "Nothing to do — every checked row is Skip.";
                _marshal(() => LogLines.Add(
                    "Nothing to do — every checked row is Skip; nothing was started."));
                return;
            }
        }
        await RunAsync(includeOnly);
    }

    [RelayCommand]
    public void Cancel()
    {
        if (IsRunning)
            _cts?.Cancel();
    }

    [RelayCommand]
    public void Close() => RequestClose?.Invoke();

    private async Task RunAsync(IReadOnlyList<string>? includeOnly)
    {
        if (_manifest.RequiresAdmin && !_consents.HasConsent(_manifest.Id))
        {
            CurrentPhase = Phase.Finished;
            PreviewVisible = false;
            StatusText = "Check 'I understand the risks' on the dashboard card first.";
            return;
        }

        CurrentPhase = Phase.Running;
        OnPropertyChanged(nameof(IsRunning));
        ConfirmEnabled = false;
        CancelVisible = true;
        CloseEnabled = false;
        PreviewVisible = false;
        StatusText = "Running…";
        LogLines.Clear();

        var startedAt = DateTime.Now;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        void Append(string line) => _marshal(() => LogLines.Add(line));

        ScriptRunResult result;
        try
        {
            result = await Task.Run(() => _manifest.RequiresAdmin
                ? _elevation.RunElevated(_manifest.Id, _configPath, includeOnly, Append, token)
                : _executor.Execute(_manifest.Id, _configPath, dryRun: false, includeOnly, Append, token));
        }
        catch (Exception ex)
        {
            result = new ScriptRunResult
            {
                Outcome = RunOutcome.Failed,
                ScriptId = _manifest.Id,
                Logs = new List<string> { "Run error: " + ex.Message },
            };
            _marshal(() => LogLines.Add("Run error: " + ex.Message));
        }
        var finishedAt = DateTime.Now;

        _history.Insert(_manifest.Id, startedAt, finishedAt, result.Outcome,
            RunHistoryStore.BuildSummary(result.Logs));

        StatusText = result.Outcome switch
        {
            RunOutcome.Success => "Success",
            RunOutcome.Warning => "Completed with warnings",
            RunOutcome.Failed => "Failed",
            RunOutcome.Cancelled => "Cancelled",
            _ => "Done",
        };
        CurrentPhase = Phase.Finished;
        OnPropertyChanged(nameof(IsRunning));
        CancelVisible = false;
        CloseEnabled = true;
        ConfirmEnabled = false;
    }

    private static string FirstErrorLine(List<string> logs)
    {
        foreach (var l in logs)
            if (l.StartsWith("ERROR:", StringComparison.Ordinal)
                || l.StartsWith("SCRIPT ERROR:", StringComparison.Ordinal))
                return l;
        return logs.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? "unknown error";
    }
}
