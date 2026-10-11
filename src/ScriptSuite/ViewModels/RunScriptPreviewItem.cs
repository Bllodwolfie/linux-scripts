using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ScriptSuite.Converters;
using ScriptSuite.Models;

namespace ScriptSuite.ViewModels;

/// <summary>Run phase of one Run All section (Windows parity).</summary>
public enum RunPhaseStatus
{
    Pending,
    Running,
    Success,
    Warning,
    Failed,
    Cancelled,
}

/// <summary>One script's section in the Run All preview: gathered dry-run
/// items, logs, collapsible preview with a collapsed one-line summary, and
/// the run-phase status. Ported from windows-scripts (CommunityToolkit style).
/// PreviewCollapseThreshold = 20: bigger previews start collapsed.</summary>
public partial class RunScriptPreviewItem : ViewModelBase
{
    public const int PreviewCollapseThreshold = 20;

    public ScriptManifest Manifest { get; }
    public string DisplayName => Manifest.DisplayName;
    public bool RequiresAdmin => Manifest.RequiresAdmin;
    public bool SupportsDryRun => Manifest.SupportsDryRun;
    public bool ShowAdminShield => Manifest.RequiresAdmin;

    public ObservableCollection<DryRunItem> PreviewItems { get; } = new();
    public ObservableCollection<string> Logs { get; } = new();

    public bool HasLogs => Logs.Count > 0;

    public bool HasPreviewItems => PreviewItems.Count > 0;

    [ObservableProperty] private bool _isPreviewExpanded = true;
    [ObservableProperty] private bool _isLogsExpanded;
    [ObservableProperty] private string? _previewMessage;
    [ObservableProperty] private string _previewCollapsedSummary = "";
    [ObservableProperty] private RunPhaseStatus _status = RunPhaseStatus.Pending;
    [ObservableProperty] private bool _isBusy;

    public string ToggleButtonText => IsPreviewExpanded ? "Collapse" : "Expand";
    public string PreviewToggleLabel => (IsPreviewExpanded ? "Hide" : "Show")
        + $" preview ({PreviewItems.Count:N0})";
    public string LogsToggleLabel => (IsLogsExpanded ? "Hide" : "Show")
        + $" logs ({Logs.Count:N0})";

    public string StatusText => Status switch
    {
        RunPhaseStatus.Running => "Running…",
        RunPhaseStatus.Success => "Success",
        RunPhaseStatus.Warning => "Warning",
        RunPhaseStatus.Failed => "Failed",
        RunPhaseStatus.Cancelled => "Cancelled",
        _ => "Pending",
    };

    /// <summary>Badge style-class predicates (App.axaml badge-* styles).</summary>
    public bool IsSuccessBadge => OutcomeBadge.IsSuccess(StatusText);
    public bool IsWarningBadge => OutcomeBadge.IsWarning(StatusText);
    public bool IsErrorBadge => OutcomeBadge.IsError(StatusText);
    public bool IsInfoBadge => OutcomeBadge.IsInfo(StatusText);

    public RunScriptPreviewItem(ScriptManifest manifest) => Manifest = manifest;

    public void AddLog(string line)
    {
        Logs.Add(line);
        OnPropertyChanged(nameof(HasLogs));
        OnPropertyChanged(nameof(LogsToggleLabel));
    }

    public void TogglePreview() => IsPreviewExpanded = !IsPreviewExpanded;

    public void ToggleLogs() => IsLogsExpanded = !IsLogsExpanded;

    /// <summary>Called once preview gathering for this script finishes:
    /// computes the collapsed one-line summary and the default expand state.</summary>
    public void FinalizePreview()
    {
        if (PreviewItems.Count == 0)
        {
            PreviewCollapsedSummary = "";
            IsPreviewExpanded = true;
        }
        else
        {
            var breakdown = PreviewItems
                .GroupBy(i => (i.Action ?? "").ToLowerInvariant())
                .OrderByDescending(g => g.Count())
                .Select(g => $"{g.Count():N0} {g.Key}");
            PreviewCollapsedSummary =
                $"{DisplayName} — {PreviewItems.Count:N0} items ({string.Join(", ", breakdown)})";
            IsPreviewExpanded = PreviewItems.Count <= PreviewCollapseThreshold;
        }
        OnPropertyChanged(nameof(HasPreviewItems));
        OnPropertyChanged(nameof(PreviewToggleLabel));
    }

    partial void OnIsPreviewExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(ToggleButtonText));
        OnPropertyChanged(nameof(PreviewToggleLabel));
    }

    partial void OnIsLogsExpandedChanged(bool value) =>
        OnPropertyChanged(nameof(LogsToggleLabel));

    partial void OnStatusChanged(RunPhaseStatus value)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(IsSuccessBadge));
        OnPropertyChanged(nameof(IsWarningBadge));
        OnPropertyChanged(nameof(IsErrorBadge));
        OnPropertyChanged(nameof(IsInfoBadge));
    }
}
