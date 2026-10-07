using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScriptSuite.Services;

namespace ScriptSuite.ViewModels;

/// <summary>Scheduled-run history: plain list, no detail routing, no filter
/// (Windows parity — its template is the same card minus expand/log).
/// "Skipped — app was busy" stays a distinct outcome, never silently dropped.
/// Manual runs live in the separate History window; the two are never merged.</summary>
public partial class ScheduledHistoryViewModel : ViewModelBase
{
    public ObservableCollection<HistoryRowViewModel> Rows { get; } = new();

    [ObservableProperty] private string _countText = "";
    [ObservableProperty] private bool _showEmptyMessage;

    public string EmptyMessage => "No scheduled runs yet.";

    public ScheduledHistoryViewModel(RunHistoryStore history, ManifestCatalog catalog)
    {
        foreach (var e in history.GetRecentScheduled(200))
        {
            var name = catalog.Find(e.ScriptId)?.DisplayName ?? e.ScriptId;
            Rows.Add(new HistoryRowViewModel
            {
                ScriptId = e.ScriptId,
                When = e.StartedAt,
                DisplayName = $"{name} ({e.Trigger})",
                Outcome = e.Outcome,
                Summary = e.Summary ?? $"Scheduled for {e.ScheduledFor}",
            });
        }
        CountText = $"{Rows.Count} scheduled run(s) recorded";
        ShowEmptyMessage = Rows.Count == 0;
    }

    [RelayCommand]
    public void Close() => RequestClose?.Invoke();

    public event Action? RequestClose;
}
