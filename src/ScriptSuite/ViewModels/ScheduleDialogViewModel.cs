using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScriptSuite.Models;
using ScriptSuite.Services;

namespace ScriptSuite.ViewModels;

/// <summary>Schedule dialog logic: the Every [N] [Days|Hours|Weeks] at [HH:mm]
/// model ported from windows-scripts ScheduleWindow. Validation parity:
/// interval must be an integer 1–365, time must parse as a TimeSpan, admin
/// scripts require the risk opt-in. The register/unregister functions default
/// to the real SystemdScheduleService and are injectable for headless tests.
/// Admin scripts carry an explicit warning: user timers cannot elevate, so a
/// scheduled ClearJournal run records Failed at execution — manual runs use
/// pkexec instead. Save registers first and only persists the store entry on
/// success, so a failed enable can never leave a phantom schedule behind.</summary>
public partial class ScheduleDialogViewModel : ViewModelBase
{
    private static readonly string[] Units = { "Days", "Hours", "Weeks" };

    private readonly ScriptManifest _manifest;
    private readonly ScheduleStore _schedules;
    private readonly RiskConsentStore _consents;
    private readonly Func<ScheduleEntry, (bool ok, string? error)> _register;
    private readonly Func<string, (bool ok, string? error)> _unregister;

    [ObservableProperty] private string _intervalText = "1";
    [ObservableProperty] private int _unitIndex;
    [ObservableProperty] private string _timeText = "09:00";
    [ObservableProperty] private bool _riskConsented;
    [ObservableProperty] private string _errorText = "";

    /// <summary>Dialog result: true = saved/removed (caller refreshes),
    /// false = cancelled, null = stay open (validation/registration error).</summary>
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

        Title = "Schedule " + manifest.DisplayName;
        ShowRisk = manifest.RequiresAdmin;
        RiskConsented = consents.HasConsent(manifest.Id);
        ShowRemove = schedules.Has(manifest.Id);

        if (schedules.Get(manifest.Id) is { } existing)
        {
            IntervalText = existing.Interval.ToString();
            TimeText = existing.TimeOfDay;
            int i = Array.IndexOf(Units, existing.Unit);
            UnitIndex = i >= 0 ? i : 0;
        }
    }

    public string Title { get; }
    public string DisplayName => _manifest.DisplayName;
    public bool ShowRisk { get; }
    public bool ShowRemove { get; }

    public string AdminTimerWarning => _manifest.RequiresAdmin
        ? "Timers run as your user and cannot elevate: scheduled runs will be recorded as Failed. Run ClearJournal manually (pkexec prompt) instead."
        : "";

    public string Unit => Units[UnitIndex < 0 || UnitIndex >= Units.Length ? 0 : UnitIndex];

    [RelayCommand]
    public void Save()
    {
        ErrorText = "";
        if (!int.TryParse(IntervalText.Trim(), out int interval) || interval < 1 || interval > 365)
        {
            ErrorText = "Interval must be a whole number from 1 to 365.";
            return;
        }
        if (!TimeSpan.TryParse(TimeText.Trim(), out _))
        {
            ErrorText = "Time must look like HH:mm (e.g. 09:00).";
            return;
        }
        if (_manifest.RequiresAdmin && !RiskConsented)
        {
            ErrorText = "You must check 'I understand the risks' first.";
            return;
        }
        if (RiskConsented)
            _consents.SetConsent(_manifest.Id, true);

        var entry = new ScheduleEntry
        {
            ScriptId = _manifest.Id,
            Enabled = true,
            Unit = Unit,
            Interval = interval,
            TimeOfDay = TimeText.Trim(),
            CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
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
