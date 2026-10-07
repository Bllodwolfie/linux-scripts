using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScriptSuite.Models;
using ScriptSuite.Services;

namespace ScriptSuite.ViewModels;

/// <summary>Run All: sequential dry-run preview over the included scripts
/// (never elevated — Windows parity), collapsible per-script sections, the
/// v2.1.0 top-line aggregate summary, then strictly sequential execution with
/// per-leg history inserts and an outcome tally. No per-item checkboxes in the
/// preview (Windows parity — selection lives on the dashboard card checkboxes).
/// Linux policy deviation (explicitly ordered, NOT Windows behavior): admin
/// scripts are excluded from the batch unless risk-consented. Windows runs
/// them with a per-leg UAC prompt instead.</summary>
public partial class RunAllViewModel : ViewModelBase
{
    private readonly BashScriptExecutor _executor;
    private readonly PolkitElevationService _elevation;
    private readonly RiskConsentStore _consents;
    private readonly RunHistoryStore _history;
    private readonly Func<string, string> _configPathFor;
    private readonly Action<Action> _marshal;
    private int _finalized;

    public ObservableCollection<RunScriptPreviewItem> Sections { get; } = new();

    [ObservableProperty] private string _summaryText = "Gathering previews…";
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private bool _progressVisible;
    [ObservableProperty] private bool _confirmEnabled;
    [ObservableProperty] private bool _isRunning;

    public event Action? RequestClose;

    public RunAllViewModel(
        IReadOnlyList<ScriptManifest> order,
        BashScriptExecutor executor,
        PolkitElevationService elevation,
        RiskConsentStore consents,
        RunHistoryStore history,
        Func<string, string> configPathFor,
        Action<Action>? marshal = null)
    {
        _executor = executor;
        _elevation = elevation;
        _consents = consents;
        _history = history;
        _configPathFor = configPathFor;
        _marshal = marshal ?? (a => Dispatcher.UIThread.Post(a));
        foreach (var m in order)
            Sections.Add(new RunScriptPreviewItem(m));
    }

    /// <summary>Aggregate preview summary — verbatim port of Windows
    /// RunAllPreviewWindow.BuildSummaryText (v2.1.0 top-line behavior).</summary>
    public static string BuildSummaryText(int deleteCount, int moveCount, int skipCount,
        int reportOnlyCount, int finalizedCount, int totalCount)
    {
        var parts = new List<string>();
        if (deleteCount > 0) parts.Add($"{deleteCount:N0} to delete");
        if (moveCount > 0) parts.Add($"{moveCount:N0} to move");
        if (skipCount > 0) parts.Add($"{skipCount:N0} to skip");
        string suffix = reportOnlyCount > 0
            ? $" (+ {reportOnlyCount:N0} report-only script{(reportOnlyCount == 1 ? "" : "s")} — no preview)"
            : "";
        if (finalizedCount < totalCount)
            return parts.Count == 0
                ? "Gathering previews…"
                : "Gathering previews so far: " + string.Join(", ", parts) + suffix + "…";
        if (parts.Count == 0 && reportOnlyCount == 0)
            return "Nothing to do right now.";
        if (parts.Count == 0)
            return "Nothing to do right now" + suffix + ".";
        return "Across all scripts: " + string.Join(", ", parts) + suffix + ".";
    }

    /// <summary>Gathers every section's preview sequentially on a worker.
    /// Call once from the view's Opened.</summary>
    public async Task LoadAsync()
    {
        ConfirmEnabled = false;
        _finalized = 0;
        foreach (var section in Sections)
        {
            var m = section.Manifest;
            if (!m.SupportsDryRun)
            {
                _marshal(() =>
                {
                    section.PreviewMessage = "No preview — this script only generates a report and changes nothing.";
                    section.FinalizePreview();
                });
                _finalized++;
                _marshal(UpdateSummary);
                continue;
            }
            try
            {
                var (items, result) = await Task.Run(() =>
                    _executor.GetDryRunItems(m.Id, _configPathFor(m.Id)));
                _marshal(() =>
                {
                    foreach (var item in items)
                        section.PreviewItems.Add(item);
                    foreach (var line in result.Logs)
                        section.AddLog(line);
                    if (items.Count == 0 && result.Outcome == RunOutcome.Failed)
                        section.PreviewMessage = "Preview failed: " + FirstErrorLine(result.Logs);
                    else if (items.Count == 0)
                        section.PreviewMessage = "Nothing to do right now.";
                    section.FinalizePreview();
                });
            }
            catch (Exception ex)
            {
                _marshal(() =>
                {
                    section.PreviewMessage = "Preview failed: " + ex.Message;
                    section.FinalizePreview();
                });
            }
            _finalized++;
            _marshal(UpdateSummary);
        }
        _marshal(() =>
        {
            UpdateSummary(Sections.Count);
            ConfirmEnabled = true;
        });
    }

    private void UpdateSummary(int finalized)
    {
        int del = 0, mov = 0, skip = 0;
        foreach (var s in Sections)
            foreach (var item in s.PreviewItems)
                switch ((item.Action ?? "").ToLowerInvariant())
                {
                    case "delete": del++; break;
                    case "move": mov++; break;
                    case "skip": skip++; break;
                }
        int reportOnly = Sections.Count(s => !s.Manifest.SupportsDryRun);
        SummaryText = BuildSummaryText(del, mov, skip, reportOnly, finalized, Sections.Count);
    }

    private void UpdateSummary() => UpdateSummary(_finalized);

    [RelayCommand]
    public async Task ConfirmAsync()
    {
        if (IsRunning) return;
        IsRunning = true;
        ConfirmEnabled = false;
        ProgressVisible = true;

        int success = 0, warned = 0, failed = 0, cancelled = 0;
        int index = 0;
        foreach (var section in Sections)
        {
            index++;
            var m = section.Manifest;
            _marshal(() =>
            {
                section.Status = RunPhaseStatus.Running;
                section.IsBusy = true;
            });
            int n = index;
            _marshal(() => ProgressText = $"Running {m.DisplayName} ({n} of {Sections.Count})…");

            var startedAt = DateTime.Now;
            ScriptRunResult result;
            if (m.RequiresAdmin && !_consents.HasConsent(m.Id))
            {
                result = new ScriptRunResult
                {
                    Outcome = RunOutcome.Cancelled,
                    ScriptId = m.Id,
                    Logs = new List<string> { "Cancelled: admin script excluded from Run All without risk consent." },
                };
            }
            else
            {
                try
                {
                    result = await Task.Run(() => m.RequiresAdmin
                        ? _elevation.RunElevated(m.Id, _configPathFor(m.Id))
                        : _executor.Execute(m.Id, _configPathFor(m.Id), dryRun: false));
                }
                catch (Exception ex)
                {
                    result = new ScriptRunResult
                    {
                        Outcome = RunOutcome.Failed,
                        ScriptId = m.Id,
                        Logs = new List<string> { "Run error: " + ex.Message },
                    };
                }
            }
            var finishedAt = DateTime.Now;
            _history.Insert(m.Id, startedAt, finishedAt, result.Outcome,
                RunHistoryStore.BuildSummary(result.Logs));

            switch (result.Outcome)
            {
                case RunOutcome.Success: success++; break;
                case RunOutcome.Warning: warned++; break;
                case RunOutcome.Cancelled: cancelled++; break;
                default: failed++; break;
            }
            var status = result.Outcome switch
            {
                RunOutcome.Success => RunPhaseStatus.Success,
                RunOutcome.Warning => RunPhaseStatus.Warning,
                RunOutcome.Cancelled => RunPhaseStatus.Cancelled,
                _ => RunPhaseStatus.Failed,
            };
            _marshal(() =>
            {
                foreach (var line in result.Logs)
                    section.AddLog(line);
                section.Status = status;
                section.IsBusy = false;
            });
        }

        var parts = new List<string>();
        if (success > 0) parts.Add($"{success} succeeded");
        if (warned > 0) parts.Add($"{warned} with warnings");
        if (failed > 0) parts.Add($"{failed} failed");
        if (cancelled > 0) parts.Add($"{cancelled} cancelled");
        _marshal(() => ProgressText = parts.Count == 0
            ? "Run All finished — nothing ran."
            : "Run All finished — " + string.Join(", ", parts) + ".");
        IsRunning = false;
    }

    [RelayCommand]
    public void Close() => RequestClose?.Invoke();

    private static string FirstErrorLine(List<string> logs)
    {
        foreach (var l in logs)
            if (l.StartsWith("ERROR:", StringComparison.Ordinal))
                return l;
        return logs.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? "unknown error";
    }
}
