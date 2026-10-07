using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScriptSuite.Services;

namespace ScriptSuite.ViewModels;

/// <summary>Manual run history: full list (newest first, 200 cap like Windows),
/// text + outcome filter, per-row log-routed detail. Scheduled runs live in
/// the separate ScheduledHistory window — never merged, like windows-scripts.
/// Linux addition vs Windows v2.0.0: Windows has no filter at all (its code
/// comments defer search/filter to "Phase 4"); the filter here is new.
/// Log files open with xdg-open (Windows: default shell handler).</summary>
public partial class HistoryViewModel : ViewModelBase
{
    private readonly List<HistoryRowViewModel> _all = new();
    private readonly Action<string> _launcher;

    public ObservableCollection<HistoryRowViewModel> Rows { get; } = new();

    public string[] OutcomeOptions { get; } =
        { "All", "Success", "Warning", "Failed", "Cancelled" };

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _outcomeFilter = "All";
    [ObservableProperty] private string _countText = "";
    [ObservableProperty] private bool _showEmptyMessage;

    public string EmptyMessage => "No runs recorded yet — run a script to see its history here.";

    public HistoryViewModel(
        RunHistoryStore history,
        ManifestCatalog catalog,
        LogDetailResolver resolver,
        Action<string>? launcher = null)
    {
        _launcher = launcher ?? DefaultLaunch;
        foreach (var e in history.GetRecent(200))
        {
            var displayName = catalog.Find(e.ScriptId)?.DisplayName ?? e.ScriptId;
            var (logPath, logDir, detail, hint) =
                resolver.BuildDetail(e.ScriptId, e.Summary, e.Outcome);
            _all.Add(new HistoryRowViewModel
            {
                ScriptId = e.ScriptId,
                When = e.StartedAt ?? "",
                DisplayName = displayName,
                Outcome = e.Outcome,
                Summary = e.Summary,
                LogFilePath = logPath,
                LogFileDirectory = logDir,
                ExpandedDetail = detail,
                LogFileHint = hint,
            });
        }
        ApplyFilter();
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnOutcomeFilterChanged(string value) => ApplyFilter();

    public void ApplyFilter()
    {
        Rows.Clear();
        string q = SearchText.Trim();
        foreach (var r in _all)
        {
            if (OutcomeFilter != "All" && r.Outcome != OutcomeFilter)
                continue;
            if (q.Length > 0
                && !r.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
                && !r.ScriptId.Contains(q, StringComparison.OrdinalIgnoreCase)
                && !(r.Summary ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)
                && !r.Outcome.Contains(q, StringComparison.OrdinalIgnoreCase))
                continue;
            Rows.Add(r);
        }
        CountText = $"{_all.Count} run(s) recorded";
        ShowEmptyMessage = Rows.Count == 0;
    }

    [RelayCommand]
    public void OpenLog(HistoryRowViewModel? row)
    {
        if (row is null) return;
        string? target = row.LogFilePath ?? row.LogFileDirectory;
        if (target is null) return;
        try
        {
            if (Directory.Exists(target)) { _launcher(target); return; }
            if (File.Exists(target)) { _launcher(target); return; }
            string? parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
                _launcher(parent);
        }
        catch { }
    }

    [RelayCommand]
    public void OpenFolder(HistoryRowViewModel? row)
    {
        if (row is null) return;
        string? target = row.LogFilePath ?? row.LogFileDirectory;
        if (target is null) return;
        try
        {
            string? dir = File.Exists(target) ? Path.GetDirectoryName(target) : target;
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                _launcher(dir);
        }
        catch { }
    }

    [RelayCommand]
    public void Close() => RequestClose?.Invoke();

    public event Action? RequestClose;

    private static void DefaultLaunch(string path)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "xdg-open",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add(path);
            Process.Start(psi);
        }
        catch { }
    }
}
