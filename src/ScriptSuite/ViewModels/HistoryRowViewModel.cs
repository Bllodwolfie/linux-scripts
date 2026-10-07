using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ScriptSuite.ViewModels;

/// <summary>One history card row. Read-only after construction except the
/// expand toggle. Ported from windows-scripts (CommunityToolkit style;
/// the legacy OutcomeBrush string was dropped — color comes from outcome).
/// Log routing (LogFilePath/Directory/ExpandedDetail/Hint) is filled by
/// LogDetailResolver, which reads the script's own report files.</summary>
public partial class HistoryRowViewModel : ViewModelBase
{
    public string ScriptId { get; init; } = "";
    public string When { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Outcome { get; init; } = "";
    public string? Summary { get; init; }
    public bool HasSummary => !string.IsNullOrEmpty(Summary);

    public string? LogFilePath { get; init; }
    public string? LogFileDirectory { get; init; }
    public bool HasLogFile => !string.IsNullOrEmpty(LogFilePath)
        || !string.IsNullOrEmpty(LogFileDirectory);
    public string? ExpandedDetail { get; init; }
    public string? LogFileHint { get; init; }

    [ObservableProperty] private bool _isExpanded;

    public string ExpandButtonText => IsExpanded ? "Hide detail" : "Show detail";

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(ExpandButtonText));

    [RelayCommand]
    public void Toggle() => IsExpanded = !IsExpanded;
}
