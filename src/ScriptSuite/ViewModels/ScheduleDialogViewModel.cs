using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScriptSuite.Models;
using ScriptSuite.Services;

namespace ScriptSuite.ViewModels;

/// <summary>Schedule dialog logic: the Every [N] [Days|Hours|Weeks] at [HH:mm]
/// model ported from windows-scripts ScheduleWindow. Supports choosing specific days
/// of the week (Mon-Sun checkboxes) for weekly schedules and live next run preview.</summary>
public partial class ScheduleDialogViewModel : ViewModelBase
{
    private static readonly string[] Units = { "Days", "Hours", "Weeks" };

    private readonly ScriptManifest _manifest;
    private readonly ScheduleStore _schedules;
    private readonly RiskConsentStore _consents;
    private readonly Func<ScheduleEntry, (bool ok, string? error)> _register;
    private readonly Func<string, (bool ok, string? error)> _unregister;

    [ObservableProperty] private string _intervalText = "1";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Unit))]
    [NotifyPropertyChangedFor(nameof(IsWeeksSelected))]
    private int _unitIndex;

    [ObservableProperty] private string _timeText = "09:00";
    [ObservableProperty] private bool _riskConsented;
    [ObservableProperty] private string _errorText = "";
    [ObservableProperty] private bool _helpVisible;
    [ObservableProperty] private string _nextRunText = "";

    [ObservableProperty] private bool _isMon;
    [ObservableProperty] private bool _isTue;
    [ObservableProperty] private bool _isWed;
    [ObservableProperty] private bool _isThu;
    [ObservableProperty] private bool _isFri;
    [ObservableProperty] private bool _isSat;
    [ObservableProperty] private bool _isSun;

    public bool IsWeeksSelected => Unit == "Weeks";
    public bool HasNextRunText => !string.IsNullOrWhiteSpace(NextRunText);
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);

    public string Title => "Schedule";
    public string DisplayTitle => $"Schedule — {_manifest.DisplayName}";
    public string Description => _manifest.Description;
    public string DisplayName => _manifest.DisplayName;
    public bool ShowRisk { get; }
    public bool ShowRemove { get; }

    public string AdminTimerWarning => _manifest.RequiresAdmin
        ? "Timers run as your user and cannot elevate: scheduled runs will be recorded as Failed. Run ClearJournal manually (pkexec prompt) instead."
        : "";

    public string Unit => Units[UnitIndex < 0 || UnitIndex >= Units.Length ? 0 : UnitIndex];

    public string HelpText =>
        "Every N Days/Hours/Weeks at HH:mm. When scheduling weekly, select one or more days of the week. "
        + "Multi-week schedules (e.g. every 2 weeks) trigger on the chosen days during alternating weeks. "
        + "This registers a systemd user timer that launches the app headlessly; no prompts "
        + "appear at run time. If the app is open when the trigger fires, that run is skipped and appears as SkippedBusy.";

    public event Action<bool?>? RequestClose;

    public ScheduleDialogViewModel(
        ScriptManifest manifest,
        ScheduleStore schedules,
        RiskConsentStore consents,
        Func<ScheduleEntry, (bool ok, string? error)>? register = null,
        Func<string, (bool ok, string? error)>? unregister = null)
    {
        _manifest = manifest;
        _schedules = schedules;
        _consents = consents;
        _register = register ?? SystemdScheduleService.Register;
        _unregister = unregister ?? SystemdScheduleService.Unregister;

        ShowRisk = manifest.RequiresAdmin;
        RiskConsented = consents.HasConsent(manifest.Id);
        ShowRemove = schedules.Has(manifest.Id);

        if (schedules.Get(manifest.Id) is { } existing)
        {
            IntervalText = existing.Interval.ToString();
            TimeText = existing.TimeOfDay;
            int i = Array.IndexOf(Units, existing.Unit);
            UnitIndex = i >= 0 ? i : 0;

            if (!string.IsNullOrWhiteSpace(existing.DayOfWeek))
            {
                var lower = existing.DayOfWeek.ToLowerInvariant();
                IsMon = lower.Contains("mon");
                IsTue = lower.Contains("tue");
                IsWed = lower.Contains("wed");
                IsThu = lower.Contains("thu");
                IsFri = lower.Contains("fri");
                IsSat = lower.Contains("sat");
                IsSun = lower.Contains("sun");
            }
        }

        if (!IsMon && !IsTue && !IsWed && !IsThu && !IsFri && !IsSat && !IsSun)
        {
            IsSun = true;
        }

        UpdateNextRunPreview();
    }

    partial void OnIsMonChanged(bool value) => UpdateNextRunPreview();
    partial void OnIsTueChanged(bool value) => UpdateNextRunPreview();
    partial void OnIsWedChanged(bool value) => UpdateNextRunPreview();
    partial void OnIsThuChanged(bool value) => UpdateNextRunPreview();
    partial void OnIsFriChanged(bool value) => UpdateNextRunPreview();
    partial void OnIsSatChanged(bool value) => UpdateNextRunPreview();
    partial void OnIsSunChanged(bool value) => UpdateNextRunPreview();
    partial void OnIntervalTextChanged(string value) => UpdateNextRunPreview();
    partial void OnUnitIndexChanged(int value) => UpdateNextRunPreview();
    partial void OnTimeTextChanged(string value) => UpdateNextRunPreview();

    public List<DayOfWeek> GetSelectedDaysOfWeek()
    {
        var list = new List<DayOfWeek>();
        if (IsMon) list.Add(DayOfWeek.Monday);
        if (IsTue) list.Add(DayOfWeek.Tuesday);
        if (IsWed) list.Add(DayOfWeek.Wednesday);
        if (IsThu) list.Add(DayOfWeek.Thursday);
        if (IsFri) list.Add(DayOfWeek.Friday);
        if (IsSat) list.Add(DayOfWeek.Saturday);
        if (IsSun) list.Add(DayOfWeek.Sunday);
        return list;
    }

    public string GetSelectedDaysString()
    {
        var list = new List<string>();
        if (IsMon) list.Add("Mon");
        if (IsTue) list.Add("Tue");
        if (IsWed) list.Add("Wed");
        if (IsThu) list.Add("Thu");
        if (IsFri) list.Add("Fri");
        if (IsSat) list.Add("Sat");
        if (IsSun) list.Add("Sun");
        return string.Join(",", list);
    }

    public void UpdateNextRunPreview()
    {
        if (!SystemdScheduleService.TryParseTimeOfDay(TimeText, out var timeOfDay))
        {
            NextRunText = "";
            return;
        }
        DateTime now = DateTime.Now;
        if (Unit == "Weeks")
        {
            var days = GetSelectedDaysOfWeek();
            if (days.Count == 0)
            {
                NextRunText = "";
                return;
            }

            DateTime? earliest = null;
            foreach (var d in days)
            {
                DateTime cand = now.Date;
                while (cand.DayOfWeek != d)
                    cand = cand.AddDays(1);
                DateTime candRun = cand.Add(timeOfDay);
                if (candRun <= now)
                    candRun = candRun.AddDays(7);

                if (earliest == null || candRun < earliest.Value)
                    earliest = candRun;
            }

            if (earliest is { } next)
            {
                NextRunText = $"Next run: {next.ToString("ddd dd.MM.yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture)}";
            }
        }
        else if (Unit == "Days")
        {
            DateTime cand = now.Date.Add(timeOfDay);
            if (cand <= now)
                cand = cand.AddDays(1);
            NextRunText = $"Next run: {cand.ToString("ddd dd.MM.yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture)}";
        }
        else if (Unit == "Hours")
        {
            int interval = int.TryParse(IntervalText.Trim(), out int iv) && iv > 0 ? iv : 1;
            DateTime next = now.AddHours(interval);
            NextRunText = $"Next run: ~{next.ToString("ddd dd.MM.yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture)}";
        }
    }

    [RelayCommand]
    public void ToggleHelp() => HelpVisible = !HelpVisible;

    [RelayCommand]
    public void Save()
    {
        ErrorText = "";
        if (!int.TryParse(IntervalText.Trim(), out int interval) || interval < 1 || interval > 365)
        {
            ErrorText = "Interval must be a whole number from 1 to 365.";
            return;
        }
        if (!SystemdScheduleService.TryParseTimeOfDay(TimeText, out _))
        {
            ErrorText = "Time must look like HH:mm (e.g. 09:00).";
            return;
        }
        if (Unit == "Weeks" && GetSelectedDaysOfWeek().Count == 0)
        {
            ErrorText = "Please select at least one day of the week.";
            return;
        }
        if (_manifest.RequiresAdmin && !RiskConsented)
        {
            ErrorText = "You must check 'I understand the risks' first.";
            return;
        }
        if (RiskConsented)
            _consents.SetConsent(_manifest.Id, true);

        var existingEntry = _schedules.Get(_manifest.Id);
        string? targetDay = Unit == "Weeks" ? GetSelectedDaysString() : null;
        bool scheduleChanged = existingEntry == null
            || existingEntry.Unit != Unit
            || existingEntry.Interval != interval
            || existingEntry.TimeOfDay != TimeText.Trim()
            || existingEntry.DayOfWeek != targetDay;

        var entry = new ScheduleEntry
        {
            ScriptId = _manifest.Id,
            Enabled = true,
            Unit = Unit,
            Interval = interval,
            TimeOfDay = TimeText.Trim(),
            DayOfWeek = targetDay,
            CreatedAt = scheduleChanged || string.IsNullOrEmpty(existingEntry?.CreatedAt)
                ? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                : existingEntry.CreatedAt,
        };
        var (ok, error) = _register(entry);
        if (!ok)
        {
            ErrorText = "Could not enable the timer: " + (error ?? "unknown error");
            return;
        }
        _schedules.Set(entry);
        RequestClose?.Invoke(true);
    }

    [RelayCommand]
    public void Remove()
    {
        ErrorText = "";
        var (ok, error) = _unregister(_manifest.Id);
        if (!ok)
        {
            ErrorText = "Could not remove the timer: " + (error ?? "unknown error");
            return;
        }
        _schedules.Remove(_manifest.Id);
        RequestClose?.Invoke(true);
    }

    [RelayCommand]
    public void Cancel() => RequestClose?.Invoke(false);
}
