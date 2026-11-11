using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScriptSuite.Services;

namespace ScriptSuite.ViewModels;

/// <summary>Unified run history: shows both Manual and Scheduled runs
/// (newest first, 200 query cap, paginated 100 per page for readability),
/// searchable with Trigger ("All", "Manual", "Scheduled") and Outcome filters.
/// Automatically prunes runs older than 4 weeks to avoid clutter.
/// Log files open with xdg-open.</summary>
public partial class HistoryViewModel : ViewModelBase
{
    private readonly List<HistoryRowViewModel> _all = new();
    private readonly List<HistoryRowViewModel> _filtered = new();
    private readonly Action<string> _launcher;

    public const int PageSize = 100;

    public ObservableCollection<HistoryRowViewModel> Rows { get; } = new();

    public string[] TriggerOptions { get; } =
        { "All", "Manual", "Scheduled" };

    public string[] OutcomeOptions { get; } =
        { "All", "Success", "Warning", "Failed", "Cancelled" };

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _triggerFilter = "All";
    [ObservableProperty] private string _outcomeFilter = "All";
    [ObservableProperty] private string _countText = "";
    [ObservableProperty] private bool _showEmptyMessage;

    [ObservableProperty] private int _currentPage = 1;
    [ObservableProperty] private int _totalPages = 1;
    [ObservableProperty] private bool _hasPagination;
    [ObservableProperty] private bool _hasPreviousPage;
    [ObservableProperty] private bool _hasNextPage;
    [ObservableProperty] private string _pageInfoText = "";

    public string EmptyMessage => "No runs recorded yet — run a script to see its history here.";

    public HistoryViewModel(
        RunHistoryStore history,
        ManifestCatalog catalog,
        LogDetailResolver resolver,
        Action<string>? launcher = null)
    {
        _launcher = launcher ?? DefaultLaunch;
        foreach (var e in history.GetRecent(200, "All"))
        {
            var displayName = catalog.Find(e.ScriptId)?.DisplayName ?? e.ScriptId;
            var (logPath, logDir, detail, hint) =
                resolver.BuildDetail(e.ScriptId, e.Summary, e.Outcome);
            _all.Add(new HistoryRowViewModel
            {
                ScriptId = e.ScriptId,
                When = e.StartedAt ?? "",
                DisplayName = displayName,
                TriggerType = e.TriggerType,
                TriggerDetail = e.TriggerDetail,
                Outcome = e.Outcome,
                Summary = e.Summary ?? (e.TriggerType == "Scheduled" && !string.IsNullOrEmpty(e.ScheduledFor) ? $"Scheduled for {e.ScheduledFor}" : null),
                LogFilePath = logPath,
                LogFileDirectory = logDir,
                ExpandedDetail = detail,
                LogFileHint = hint,
            });
        }
        ApplyFilter();
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnTriggerFilterChanged(string value) => ApplyFilter();
    partial void OnOutcomeFilterChanged(string value) => ApplyFilter();

    public void ApplyFilter()
    {
        _filtered.Clear();
        string q = SearchText.Trim();
        foreach (var r in _all)
        {
            if (TriggerFilter != "All" && !string.Equals(r.TriggerType, TriggerFilter, StringComparison.OrdinalIgnoreCase))
                continue;
            if (OutcomeFilter != "All" && !string.Equals(r.Outcome, OutcomeFilter, StringComparison.OrdinalIgnoreCase))
                continue;
            if (q.Length > 0
                && !r.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
                && !r.ScriptId.Contains(q, StringComparison.OrdinalIgnoreCase)
                && !(r.Summary ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)
                && !r.Outcome.Contains(q, StringComparison.OrdinalIgnoreCase)
                && !r.TriggerType.Contains(q, StringComparison.OrdinalIgnoreCase))
                continue;
            _filtered.Add(r);
        }
        CurrentPage = 1;
        RefreshPage();
    }

    private void RefreshPage()
    {
        TotalPages = Math.Max(1, (int)Math.Ceiling((double)_filtered.Count / PageSize));
        if (CurrentPage > TotalPages) CurrentPage = TotalPages;
        if (CurrentPage < 1) CurrentPage = 1;

        HasPagination = TotalPages > 1;
        HasPreviousPage = CurrentPage > 1;
        HasNextPage = CurrentPage < TotalPages;
        PageInfoText = $"Page {CurrentPage} of {TotalPages}";

        Rows.Clear();
        var pageItems = _filtered.Skip((CurrentPage - 1) * PageSize).Take(PageSize);
        foreach (var item in pageItems)
        {
            Rows.Add(item);
        }

        CountText = _filtered.Count == _all.Count
            ? $"{_all.Count} run(s) recorded"
            : $"{_filtered.Count} of {_all.Count} run(s) matched";
        ShowEmptyMessage = Rows.Count == 0;
    }

    [RelayCommand]
    public void PreviousPage()
    {
        if (CurrentPage > 1)
        {
            CurrentPage--;
            RefreshPage();
        }
    }

    [RelayCommand]
    public void NextPage()
    {
        if (CurrentPage < TotalPages)
        {
            CurrentPage++;
            RefreshPage();
        }
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
