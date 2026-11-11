using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScriptSuite.Models;
using ScriptSuite.Services;

namespace ScriptSuite.ViewModels;

public partial class ScheduledTasksViewModel : ViewModelBase
{
    private readonly ManifestCatalog _catalog;
    private readonly ScheduleStore _schedules;
    private readonly RunHistoryStore _history;
    private readonly Func<string, Task>? _editScheduleAction;

    public ObservableCollection<ScheduledTaskRowViewModel> Tasks { get; } = new();

    [ObservableProperty] private string _countText = "";
    [ObservableProperty] private bool _showEmptyMessage;

    public string EmptyMessage => "No automated schedules configured yet. Click 'Configure' on any script below to set up a systemd user timer.";

    public ScheduledTasksViewModel(
        ManifestCatalog catalog,
        ScheduleStore schedules,
        RunHistoryStore history,
        Func<string, Task>? editScheduleAction = null)
    {
        _catalog = catalog;
        _schedules = schedules;
        _history = history;
        _editScheduleAction = editScheduleAction;
        Refresh();
    }

    public void Refresh()
    {
        Tasks.Clear();
        var latest = _history.GetLatestByScript();
        int activeCount = 0;

        foreach (var m in _catalog.All)
        {
            var entry = _schedules.Get(m.Id);
            bool timerActive = SystemdScheduleService.TaskExists(m.Id);
            bool hasSchedule = entry is not null || timerActive;
            if (hasSchedule) activeCount++;

            string scheduleSummary = "Not scheduled";
            if (entry is not null)
            {
                scheduleSummary = SystemdScheduleService.SummaryFor(entry);
            }
            else if (timerActive)
            {
                scheduleSummary = ReadTimerSummary(m.Id);
            }

            string lastRanText = "Never ran";
            string lastOutcome = "";
            if (latest.TryGetValue(m.Id, out var h) && !string.IsNullOrEmpty(h.StartedAt))
            {
                lastRanText = $"{ScriptCardViewModel.FormatAgo(h.StartedAt)} ({h.Outcome})";
                lastOutcome = h.Outcome;
            }

            Tasks.Add(new ScheduledTaskRowViewModel
            {
                ScriptId = m.Id,
                DisplayName = m.DisplayName,
                Category = m.Category,
                Description = m.Description,
                HasSchedule = hasSchedule,
                ScheduleSummary = scheduleSummary,
                LastRanAt = lastRanText,
                LastOutcome = lastOutcome,
                ScheduleTime = entry?.TimeOfDay,
                ScheduleInterval = entry is not null ? $"{entry.Interval} {entry.Unit}" : "",
                EditCommand = new AsyncRelayCommand(() => EditScheduleAsync(m.Id))
            });
        }

        CountText = $"{activeCount} of {_catalog.All.Count} script(s) scheduled";
        ShowEmptyMessage = activeCount == 0;
    }

    private static string ReadTimerSummary(string scriptId)
    {
        try
        {
            string path = Path.Combine(SystemdScheduleService.UnitDir, SystemdScheduleService.TimerNameFor(scriptId));
            if (File.Exists(path))
            {
                foreach (var line in File.ReadAllLines(path))
                {
                    if (line.StartsWith("Description=ScriptSuite schedule:", StringComparison.OrdinalIgnoreCase))
                        return line.Substring("Description=ScriptSuite schedule:".Length).Trim();
                }
            }
        }
        catch { }
        return "Active (systemd timer)";
    }

    private async Task EditScheduleAsync(string scriptId)
    {
        if (_editScheduleAction is not null)
        {
            await _editScheduleAction(scriptId);
            Refresh();
        }
    }

    [RelayCommand]
    public void Close() => RequestClose?.Invoke();

    public event Action? RequestClose;
}
