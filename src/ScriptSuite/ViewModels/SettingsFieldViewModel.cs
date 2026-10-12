using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScriptSuite.Models;
using ScriptSuite.Services;

namespace ScriptSuite.ViewModels;

/// <summary>One editable field on a script's settings form. The rendered
/// control is driven by the manifest field's Type; every edit auto-saves
/// through the owning form's commit callback, and the form records an undo
/// snapshot per committed change.
///
/// Text entry (string/path/int text box) updates the in-memory value on every
/// keystroke but defers the actual save until CommitPending (a short pause
/// after typing, focus loss, Enter, window close, or Undo), so a half-typed
/// value is never written to disk.
/// Discrete controls (stepper, checkbox, chip add/remove, browse) commit
/// immediately.
/// Ported from windows-scripts. Linux adaptations: Avalonia DispatcherTimer
/// instead of the WPF one, PathEnv expansion ($HOME/~) instead of
/// %VAR% expansion, no trailing space/dot rule (not a Linux concern), and
/// absolute-path wording uses /home/… examples. The MinAgeDays >= 0 blocking
/// rule is unchanged.</summary>
public partial class SettingsFieldViewModel : ViewModelBase
{
    private readonly Action<SettingsFieldViewModel, JsonNode?, JsonNode?> _commit;
    private string _textValue;
    private int _intValue;
    private string _intText;
    private bool _boolValue;
    private bool _savedShown;
    private bool _textPending;
    private string? _pendingTextPrior;
    private bool _intPending;
    private int? _pendingIntPrior;
    private string _newItemText = "";
    private string _pathWarning = "";
    private string _validationMessage = "";
    private DispatcherTimer? _savedTimer;
    private DispatcherTimer? _debounce;

    public SettingsFieldViewModel(ScriptField field, JsonNode? initial,
        Action<SettingsFieldViewModel, JsonNode?, JsonNode?> commit)
    {
        Field = field;
        _commit = commit;
        var (text, intVal, boolVal, items) = FromNode(field, initial);
        _textValue = text;
        _intValue = intVal;
        _intText = intVal.ToString(CultureInfo.InvariantCulture);
        _boolValue = boolVal;
        foreach (var item in items)
            Items.Add(item);
        RefreshListPathWarning();
    }

    public ScriptField Field { get; }
    public string Label => Field.Label;
    public string HelpText => Field.HelpText ?? "";
    public string HelpDetail => Field.HelpDetail ?? "";
    public bool HasHelpDetail => !string.IsNullOrWhiteSpace(Field.HelpDetail);

    [ObservableProperty] private bool _isHelpExpanded;

    [RelayCommand]
    public void ToggleHelp() => IsHelpExpanded = !IsHelpExpanded;

    public string Unit => Field.Unit ?? "";
    public bool ShowUnit => !string.IsNullOrEmpty(Field.Unit);
    public bool IsFilePath => Field.IsFilePath;

    public ObservableCollection<string> Items { get; } = new();

    /// <summary>Text for string/path fields. Value updates on every keystroke;
    /// the save is deferred to CommitPending.</summary>
    public string TextValue
    {
        get => _textValue;
        set
        {
            if (_textValue == value) return;
            _pendingTextPrior ??= _textValue;
            _textValue = value;
            _textPending = true;
            OnPropertyChanged();
            RefreshVacuumNote();
            ArmDebounce();
        }
    }

    /// <summary>Text box backing the int field. Keeps whatever the user typed
    /// (even if not yet a valid number) so the box can be cleared while typing;
    /// only a valid parse stages a deferred commit. Normalized back to the
    /// current int on CommitPending.</summary>
    public string IntText
    {
        get => _intText;
        set
        {
            if (_intText == value) return;
            var priorValue = _intValue;
            _intText = value;
            OnPropertyChanged();
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed != priorValue)
            {
                // Blocking validation for MinAgeDays — deliberate departure from warn-but-save.
                // Negative age is not recoverable (cannot do anything sensible), so block.
                if (Field.Name == "MinAgeDays" && parsed < 0)
                {
                    ValidationMessage = "Must be >= 0";
                    _pendingIntPrior = null;
                    _intPending = false;
                    _debounce?.Stop();
                    return;
                }
                ValidationMessage = "";
                _pendingIntPrior ??= priorValue;
                _intValue = parsed;
                _intPending = true;
                ArmDebounce();
            }
            else if (value.Trim() == "-")
            {
                // Intermediate typing of negative sign — show validation but don't block yet
                if (Field.Name == "MinAgeDays")
                    ValidationMessage = "Must be >= 0";
            }
        }
    }

    public bool BoolValue
    {
        get => _boolValue;
        set
        {
            if (_boolValue == value) return;
            var prior = _boolValue;
            _boolValue = value;
            OnPropertyChanged();
            Commit(JsonValue.Create(prior), JsonValue.Create(_boolValue));
        }
    }

    public string NewItemText
    {
        get => _newItemText;
        set => SetProperty(ref _newItemText, value);
    }

    /// <summary>Warn-but-save note for pathList fields (e.g. EmptyFolderCleanup
    /// IgnoreFolders): counts entries that are not absolute (never match) or
    /// do not exist yet (kept if created later). The value is always saved —
    /// same pattern as ValidatePath for single path fields.</summary>
    public string ListPathWarning { get; private set; } = "";

    public bool HasListPathWarning => !string.IsNullOrEmpty(ListPathWarning);

    private void RefreshListPathWarning()
    {
        string next = "";
        if (Field.Type == "pathList" && Items.Count > 0)
        {
            int bad = 0, missing = 0;
            foreach (var item in Items)
            {
                var v = (item ?? "").Trim();
                if (v.Length == 0) { bad++; continue; }
                string expanded;
                try { expanded = PathEnv.ExpandUserPath(v); }
                catch { bad++; continue; }
                if (!Path.IsPathRooted(expanded)) { bad++; continue; }
                if (!Directory.Exists(expanded)) missing++;
            }
            var parts = new List<string>();
            if (bad > 0) parts.Add($"{bad} {(bad == 1 ? "entry is" : "entries are")} not absolute and will never match");
            if (missing > 0) parts.Add($"{missing} {(missing == 1 ? "folder does" : "folders do")} not exist yet — kept if created");
            if (parts.Count > 0) next = "Note: " + string.Join("; ", parts) + ".";
        }
        if (ListPathWarning == next) return;
        ListPathWarning = next;
        OnPropertyChanged(nameof(ListPathWarning));
        OnPropertyChanged(nameof(HasListPathWarning));
    }

    /// <summary>Inline warning shown under a path field after a save attempt.
    /// Warn-but-save: the value is still persisted, the warning just flags that
    /// the resolved path doesn't currently exist (or isn't absolute).</summary>
    public string PathWarning
    {
        get => _pathWarning;
        private set
        {
            if (_pathWarning == value) return;
            _pathWarning = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasPathWarning));
        }
    }

    public bool HasPathWarning => !string.IsNullOrEmpty(_pathWarning);

    /// <summary>Blocking validation for int fields where negative is not recoverable.
    /// Unlike PathWarning (warn-but-save), this blocks the commit entirely.</summary>
    public string ValidationMessage
    {
        get => _validationMessage;
        private set
        {
            if (_validationMessage == value) return;
            _validationMessage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasValidationError));
        }
    }

    public bool HasValidationError => !string.IsNullOrEmpty(_validationMessage);

    public bool IsSavedShown
    {
        get => _savedShown;
        private set => SetProperty(ref _savedShown, value);
    }

    /// <summary>Debounce timer factory hook: the Avalonia dispatcher timer in
    /// production; headless contexts (no Dispatcher) get no timer and must
    /// call CommitPending explicitly. Tests may swap in a stand-in.</summary>
    public static Func<TimeSpan, DispatcherTimer?>? TimerFactory { get; set; }

    private static DispatcherTimer? NewTimer(TimeSpan interval)
    {
        if (TimerFactory is not null) return TimerFactory(interval);
        try
        {
            return new DispatcherTimer { Interval = interval };
        }
        catch
        {
            return null; // headless: no Dispatcher — caller degrades gracefully
        }
    }

    [RelayCommand]
    public void StepUp() => Step(1);

    [RelayCommand]
    public void StepDown() => Step(-1);

    public void Step(int delta)
    {
        if (delta == 0) return;
        _debounce?.Stop();
        var prior = _intValue;
        var next = _intValue + delta;
        // Block negative for MinAgeDays — clamp and show validation instead of saving
        if (Field.Name == "MinAgeDays" && next < 0)
        {
            ValidationMessage = "Must be >= 0";
            return;
        }
        ValidationMessage = "";
        _intValue = next;
        _intPending = false;
        _pendingIntPrior = null;
        _intText = _intValue.ToString(CultureInfo.InvariantCulture);
        OnPropertyChanged(nameof(IntText));
        Commit(JsonValue.Create(prior), JsonValue.Create(_intValue));
    }

    [RelayCommand]
    public void AddNewItem()
    {
        var v = _newItemText.Trim();
        if (v.Length == 0 || Items.Contains(v))
        {
            _newItemText = "";
            OnPropertyChanged(nameof(NewItemText));
            return;
        }
        var prior = ToArrayNode(Items.ToList());
        Items.Add(v);
        _newItemText = "";
        OnPropertyChanged(nameof(NewItemText));
        RefreshListPathWarning();
        Commit(prior, ToArrayNode(Items.ToList()));
    }

    [RelayCommand]
    public void RemoveItem(string? value)
    {
        if (value is null || !Items.Contains(value)) return;
        var prior = ToArrayNode(Items.ToList());
        Items.Remove(value);
        RefreshListPathWarning();
        Commit(prior, ToArrayNode(Items.ToList()));
    }

    /// <summary>Sets a path chosen from the Browse dialog (commits immediately).</summary>
    public void SetPath(string value)
    {
        if (_textValue == value) return;
        _debounce?.Stop();
        var prior = _textValue;
        _textValue = value;
        _textPending = false;
        _pendingTextPrior = null;
        OnPropertyChanged(nameof(TextValue));
        Commit(JsonValue.Create(prior), JsonValue.Create(_textValue));
    }

    /// <summary>Commits any deferred text edit (called on focus loss, Enter,
    /// window close, and before Undo). Also normalizes the int text box back
    /// to its committed value.</summary>
    public void CommitPending()
    {
        _debounce?.Stop();
        if (_textPending)
        {
            _textPending = false;
            // Blocking validation for VacuumTime (mirrors MinAgeDays): values
            // the script would reject never reach disk.
            if (Field.Name == "VacuumTime" && !VacuumTimeFormat.TryGetDays(_textValue, out _))
            {
                ValidationMessage = "Use a number with a unit, e.g. 7d, 2 weeks, 365 days.";
                _pendingTextPrior = null;
                return;
            }
            ValidationMessage = "";
            var prior = _pendingTextPrior ?? _textValue;
            _pendingTextPrior = null;
            Commit(JsonValue.Create(prior), JsonValue.Create(_textValue));
        }
        else if (_intPending)
        {
            // Re-validate before commit — block negative MinAgeDays
            if (Field.Name == "MinAgeDays" && _intValue < 0)
            {
                ValidationMessage = "Must be >= 0";
                _intPending = false;
                _pendingIntPrior = null;
                _intText = _intValue.ToString(CultureInfo.InvariantCulture);
                OnPropertyChanged(nameof(IntText));
                return;
            }
            ValidationMessage = "";
            _intPending = false;
            var prior = _pendingIntPrior ?? _intValue;
            _pendingIntPrior = null;
            Commit(JsonValue.Create(prior), JsonValue.Create(_intValue));
        }
        _intText = _intValue.ToString(CultureInfo.InvariantCulture);
        OnPropertyChanged(nameof(IntText));
    }

    /// <summary>Reverts this field to a prior value without committing (Undo).</summary>
    public void Restore(JsonNode? prior)
    {
        _debounce?.Stop();
        var (text, intVal, boolVal, items) = FromNode(Field, prior);
        _textValue = text;
        _intValue = intVal;
        _intText = intVal.ToString(CultureInfo.InvariantCulture);
        _boolValue = boolVal;
        _textPending = false;
        _pendingTextPrior = null;
        _intPending = false;
        _pendingIntPrior = null;
        ValidationMessage = "";
        Items.Clear();
        foreach (var item in items)
            Items.Add(item);
        RefreshListPathWarning();
        OnPropertyChanged(nameof(TextValue));
        OnPropertyChanged(nameof(IntText));
        OnPropertyChanged(nameof(BoolValue));
        PathWarning = ValidatePath();
        RefreshVacuumNote();
    }

    private void Commit(JsonNode? prior, JsonNode? next)
    {
        PathWarning = ValidatePath();
        RefreshVacuumNote();
        _commit(this, prior, next);
        ShowSaved();
    }

    /// <summary>Inline note for a bare-number VacuumTime ("365" is read as
    /// 365 days — journalctl alone would read seconds). Non-blocking, shown
    /// in the PathWarning slot (free for non-path fields); cleared whenever
    /// the value stops being a bare number.</summary>
    private void RefreshVacuumNote()
    {
        if (Field.Name != "VacuumTime" || Field.Type != "string") return;
        PathWarning = VacuumTimeFormat.IsBareNumber(_textValue)
            && VacuumTimeFormat.TryGetDays(_textValue, out _)
            ? $"'{_textValue}' has no unit — will be read as {_textValue} days."
            : "";
    }

    /// <summary>Warn-but-save check for path fields: expands the value and
    /// flags non-absolute or non-existent targets. Empty for non-path fields.
    /// Folder paths warn when the folder is absent (it may be created at run
    /// time); file paths warn only when their parent folder is absent.</summary>
    private string ValidatePath()
    {
        if (Field.Type != "path") return "";
        var value = _textValue;
        if (string.IsNullOrWhiteSpace(value)) return "Enter a path.";
        var expanded = PathEnv.ExpandUserPath(value.Trim());
        if (!Path.IsPathRooted(expanded)) return "Path is not absolute (use e.g. /home/you/Downloads).";
        if (Field.IsFilePath)
        {
            if (File.Exists(expanded)) return "";
            var dir = Path.GetDirectoryName(expanded);
            if (string.IsNullOrEmpty(dir) || Directory.Exists(dir)) return "";
            return "Folder for this file does not exist yet — it may be created on the next run.";
        }
        return Directory.Exists(expanded) ? "" : "Folder does not exist yet — it may be created on the next run.";
    }

    private void ShowSaved()
    {
        IsSavedShown = true;
        _savedTimer ??= NewTimer(TimeSpan.FromMilliseconds(1500));
        if (_savedTimer is null) return; // headless: stays shown, no auto-hide
        _savedTimer.Stop();
        _savedTimer.Tick -= OnSavedTimerTick;
        _savedTimer.Tick += OnSavedTimerTick;
        _savedTimer.Start();
    }

    private void OnSavedTimerTick(object? sender, EventArgs e)
    {
        _savedTimer?.Stop();
        IsSavedShown = false;
    }

    /// <summary>Restarts the auto-save debounce. Any typed text edit is committed
    /// shortly after the user stops typing, so a value is saved even if focus
    /// never leaves the box (e.g. clicking blank space in a single-field window).</summary>
    private void ArmDebounce()
    {
        _debounce ??= NewTimer(TimeSpan.FromMilliseconds(600));
        if (_debounce is null) return; // headless: caller must CommitPending
        _debounce.Stop();
        _debounce.Tick -= OnDebounceTick;
        _debounce.Tick += OnDebounceTick;
        _debounce.Start();
    }

    private void OnDebounceTick(object? sender, EventArgs e)
    {
        _debounce?.Stop();
        CommitPending();
    }

    private static JsonArray ToArrayNode(List<string> items)
    {
        var arr = new JsonArray();
        foreach (var item in items)
            arr.Add(JsonValue.Create(item));
        return arr;
    }

    private static (string Text, int Int, bool Bool, List<string> Items) FromNode(ScriptField field, JsonNode? node)
    {
        switch (field.Type)
        {
            case "int":
            {
                int v = 0;
                if (node is JsonValue jv && jv.TryGetValue<int>(out var i)) v = i;
                else if (field.HasDefault && field.Default.ValueKind == JsonValueKind.Number && field.Default.TryGetInt32(out var d)) v = d;
                return ("", v, false, new List<string>());
            }
            case "bool":
            {
                bool v = false;
                if (node is JsonValue jv && jv.TryGetValue<bool>(out var b)) v = b;
                else if (field.HasDefault && field.Default.ValueKind is JsonValueKind.True or JsonValueKind.False) v = field.Default.GetBoolean();
                return ("", 0, v, new List<string>());
            }
            case "stringList":
            case "pathList":
            case "patternList":
            {
                var items = new List<string>();
                if (node is JsonArray arr)
                {
                    foreach (var element in arr)
                        if (element is JsonValue jv && jv.TryGetValue<string>(out var s)) items.Add(s);
                }
                else if (field.HasDefault && field.Default.ValueKind == JsonValueKind.Array)
                {
                    foreach (var element in field.Default.EnumerateArray())
                        if (element.ValueKind == JsonValueKind.String) items.Add(element.GetString()!);
                }
                return ("", 0, false, items);
            }
            default:
            {
                string v = "";
                if (node is JsonValue jv && jv.TryGetValue<string>(out var s)) v = s;
                else if (field.HasDefault && field.Default.ValueKind == JsonValueKind.String) v = field.Default.GetString() ?? "";
                return (v, 0, false, new List<string>());
            }
        }
    }
}
