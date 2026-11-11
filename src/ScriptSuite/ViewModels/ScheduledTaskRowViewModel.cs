using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScriptSuite.Converters;

namespace ScriptSuite.ViewModels;

public partial class ScheduledTaskRowViewModel : ViewModelBase
{
    public string ScriptId { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Category { get; init; } = "";
    public string Description { get; init; } = "";
    public bool HasSchedule { get; init; }
    public string ScheduleSummary { get; init; } = "";
    public string LastRanAt { get; init; } = "";
    public string LastOutcome { get; init; } = "";
    public string? ScheduleTime { get; init; }
    public string? ScheduleInterval { get; init; }
    public IAsyncRelayCommand? EditCommand { get; init; }

    public bool IsSuccessBadge => OutcomeBadge.IsSuccess(LastOutcome);
    public bool IsWarningBadge => OutcomeBadge.IsWarning(LastOutcome);
    public bool IsErrorBadge => OutcomeBadge.IsError(LastOutcome);
    public bool IsInfoBadge => OutcomeBadge.IsInfo(LastOutcome);
}
