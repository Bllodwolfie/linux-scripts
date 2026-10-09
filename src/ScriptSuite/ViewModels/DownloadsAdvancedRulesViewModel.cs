using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using ScriptSuite.Services;

namespace ScriptSuite.ViewModels;

/// <summary>Per-extension advanced rules for DownloadsCleanup only. Additive to simple mode:
/// explicit rule overrides DeleteExts/Categories for that extension. Missing key = no change.
/// Overlap decision: AdvancedRules always wins; simple-mode tag list is not auto-synced,
/// UI shows a one-line warning so the user is not surprised.
/// Ported verbatim from windows-scripts (CommunityToolkit base).</summary>
public sealed class AdvancedRuleRowViewModel : ViewModelBase
{
    private string _extension = "";
    private string _action = "Ignore";
    private string _destination = "";
    private string _overlapWarning = "";

    public AdvancedRuleRowViewModel(string extension, string action, string destination)
    {
        _extension = extension;
        _action = action;
        _destination = destination;
    }

    public string Extension
    {
        get => _extension;
        set => SetProperty(ref _extension, value);
    }

    public string Action
    {
        get => _action;
        set
        {
            if (SetProperty(ref _action, value))
            {
                OnPropertyChanged(nameof(IsMoveTo));
                OnPropertyChanged(nameof(HasValidationError));
                OnPropertyChanged(nameof(ValidationMessage));
            }
        }
    }

    public bool IsMoveTo => string.Equals(Action, "MoveTo", StringComparison.OrdinalIgnoreCase);

    public string Destination
    {
        get => _destination;
        set
        {
            if (SetProperty(ref _destination, value))
            {
                OnPropertyChanged(nameof(HasValidationError));
                OnPropertyChanged(nameof(ValidationMessage));
            }
        }
    }

    // Blocking validation — deliberate departure from ValidatePath() warn-but-save.
    // A missing Destination for MoveTo is not recoverable (cannot do anything sensible),
    // so the UI blocks saving instead of persisting a broken rule. Script fallback
    // remains as defense-in-depth for hand-edited JSON (Skip + loud warning, dry-run
    // also surfaces Skip [advanced rule]).
    public bool HasValidationError => IsMoveTo && string.IsNullOrWhiteSpace(Destination);
    public string ValidationMessage => HasValidationError ? "Destination required for MoveTo" : "";

    public string OverlapWarning
    {
        get => _overlapWarning;
        set
        {
            if (SetProperty(ref _overlapWarning, value))
                OnPropertyChanged(nameof(HasOverlapWarning));
        }
    }

    public bool HasOverlapWarning => !string.IsNullOrEmpty(OverlapWarning);
}

public sealed class DownloadsAdvancedRulesViewModel : ViewModelBase
{
    private readonly ScriptConfigService _config;
    private string _newExtension = "";
    private string _newAction = "Ignore";
    private string _newDestination = "";
    private string _newOverlapHint = "";

    public DownloadsAdvancedRulesViewModel(ScriptConfigService config)
    {
        _config = config;
        Rules = new ObservableCollection<AdvancedRuleRowViewModel>();
        ActionOptions = new List<string> { "Ignore", "Delete", "MoveTo" };
        Load();
    }

    public ObservableCollection<AdvancedRuleRowViewModel> Rules { get; }
    public List<string> ActionOptions { get; }

    public string NewExtension
    {
        get => _newExtension;
        set
        {
            if (SetProperty(ref _newExtension, value))
            {
                OnPropertyChanged(nameof(CanAdd));
                UpdateNewOverlapHint();
            }
        }
    }

    public string NewAction
    {
        get => _newAction;
        set
        {
            if (SetProperty(ref _newAction, value))
            {
                OnPropertyChanged(nameof(IsNewMoveTo));
                OnPropertyChanged(nameof(HasNewValidationError));
                OnPropertyChanged(nameof(NewValidationMessage));
                OnPropertyChanged(nameof(CanAdd));
                UpdateNewOverlapHint();
            }
        }
    }

    public bool IsNewMoveTo => string.Equals(NewAction, "MoveTo", StringComparison.OrdinalIgnoreCase);

    public string NewDestination
    {
        get => _newDestination;
        set
        {
            if (SetProperty(ref _newDestination, value))
            {
                OnPropertyChanged(nameof(HasNewValidationError));
                OnPropertyChanged(nameof(NewValidationMessage));
                OnPropertyChanged(nameof(CanAdd));
            }
        }
    }

    // Blocking validation for the add-row — MoveTo requires a destination.
    public bool HasNewValidationError => IsNewMoveTo && string.IsNullOrWhiteSpace(NewDestination);
    public string NewValidationMessage => HasNewValidationError ? "Destination required for MoveTo" : "";

    public string NewOverlapHint
    {
        get => _newOverlapHint;
        private set
        {
            if (SetProperty(ref _newOverlapHint, value))
                OnPropertyChanged(nameof(HasNewOverlapHint));
        }
    }

    public bool HasNewOverlapHint => !string.IsNullOrEmpty(NewOverlapHint);

    private HashSet<string> _deleteExts = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _extMap = new(StringComparer.OrdinalIgnoreCase);

    private void Load()
    {
        Rules.Clear();
        var doc = _config.Load("DownloadsCleanup");
        // Build overlap maps from current config
        _deleteExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _extMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (doc.TryGetPropertyValue("DeleteExts", out var del) && del is JsonArray delArr)
        {
            foreach (var el in delArr)
                if (el is JsonValue jv && jv.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
                    _deleteExts.Add(s.ToLowerInvariant());
        }
        if (doc.TryGetPropertyValue("Categories", out var cat) && cat is JsonObject catObj)
        {
            foreach (var kv in catObj)
            {
                string dest = kv.Key;
                if (kv.Value is JsonArray arr)
                {
                    foreach (var el in arr)
                        if (el is JsonValue jv && jv.TryGetValue<string>(out var s))
                            _extMap[s.ToLowerInvariant()] = dest;
                }
            }
        }

        if (doc.TryGetPropertyValue("AdvancedRules", out var adv) && adv is JsonObject advObj)
        {
            foreach (var kv in advObj)
            {
                string ext = kv.Key;
                if (!ext.StartsWith(".")) ext = "." + ext;
                ext = ext.ToLowerInvariant();
                string action = "Ignore";
                string dest = "";
                if (kv.Value is JsonObject jo)
                {
                    if (jo.TryGetPropertyValue("Action", out var a) && a is JsonValue jv && jv.TryGetValue<string>(out var s))
                        action = s;
                    if (jo.TryGetPropertyValue("Destination", out var d) && d is JsonValue jdv && jdv.TryGetValue<string>(out var ds))
                        dest = ds;
                }
                var row = new AdvancedRuleRowViewModel(ext, action, dest);
                row.OverlapWarning = BuildOverlapWarning(ext);
                Rules.Add(row);
            }
        }
        UpdateNewOverlapHint();
    }

    private string BuildOverlapWarning(string ext)
    {
        ext = ext.ToLowerInvariant();
        if (!ext.StartsWith(".")) ext = "." + ext;
        var parts = new List<string>();
        if (_deleteExts.Contains(ext))
            parts.Add("Also in “Extensions to delete” — advanced rule will override");
        if (_extMap.TryGetValue(ext, out var dest))
            parts.Add($"Also mapped to {Path.GetFileName(dest.TrimEnd('\\', '/'))} via Categories — advanced rule will override");
        return string.Join(" · ", parts);
    }

    private void UpdateNewOverlapHint()
    {
        var ext = NewExtension?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(ext))
        {
            NewOverlapHint = "";
            return;
        }
        if (!ext.StartsWith(".")) ext = "." + ext;
        NewOverlapHint = BuildOverlapWarning(ext);
    }

    private void Save()
    {
        var obj = new JsonObject();
        foreach (var r in Rules)
        {
            var key = r.Extension.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(key)) continue;
            if (!key.StartsWith(".")) key = "." + key;
            var jo = new JsonObject
            {
                ["Action"] = r.Action
            };
            if (string.Equals(r.Action, "MoveTo", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(r.Destination))
                jo["Destination"] = r.Destination.Trim();
            obj[key] = jo;
        }
        _config.SetField("DownloadsCleanup", "AdvancedRules", obj);
        // refresh overlap warnings for all rows (DeleteExts/Categories may have changed via other UI)
        // Re-load maps to reflect latest DeleteExts edits that happened outside this VM
        var doc = _config.Load("DownloadsCleanup");
        _deleteExts.Clear();
        if (doc.TryGetPropertyValue("DeleteExts", out var del) && del is JsonArray delArr)
            foreach (var el in delArr)
                if (el is JsonValue jv && jv.TryGetValue<string>(out var s))
                    _deleteExts.Add(s.ToLowerInvariant());
        _extMap.Clear();
        if (doc.TryGetPropertyValue("Categories", out var cat) && cat is JsonObject catObj)
            foreach (var kv in catObj)
                if (kv.Value is JsonArray arr)
                    foreach (var el in arr)
                        if (el is JsonValue jv && jv.TryGetValue<string>(out var s))
                            _extMap[s.ToLowerInvariant()] = kv.Key;
        foreach (var r in Rules)
            r.OverlapWarning = BuildOverlapWarning(r.Extension);
        UpdateNewOverlapHint();
    }

    public bool CanAdd => !string.IsNullOrWhiteSpace(NewExtension) && !HasNewValidationError;

    public void AddNew()
    {
        var ext = NewExtension.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(ext)) return;
        if (!ext.StartsWith(".")) ext = "." + ext;
        if (Rules.Any(r => string.Equals(r.Extension, ext, StringComparison.OrdinalIgnoreCase)))
            return; // already exists
        // Block: MoveTo requires destination — this is the deliberate blocking validation,
        // distinct from path warn-but-save. Script fallback remains for hand-edited JSON.
        if (IsNewMoveTo && string.IsNullOrWhiteSpace(NewDestination))
            return;
        var row = new AdvancedRuleRowViewModel(ext, NewAction, IsNewMoveTo ? NewDestination.Trim() : "");
        row.OverlapWarning = BuildOverlapWarning(ext);
        Rules.Add(row);
        NewExtension = "";
        NewDestination = "";
        // keep action as chosen
        Save();
        OnPropertyChanged(nameof(Rules));
        OnPropertyChanged(nameof(CanAdd));
        OnPropertyChanged(nameof(HasNewValidationError));
        OnPropertyChanged(nameof(NewValidationMessage));
    }

    public void Remove(AdvancedRuleRowViewModel row)
    {
        Rules.Remove(row);
        Save();
    }

    public void NotifyRowChanged(AdvancedRuleRowViewModel row)
    {
        // Normalize extension lower case with dot
        var ext = row.Extension.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(ext) && !ext.StartsWith(".")) ext = "." + ext;
        row.Extension = ext;
        row.OverlapWarning = BuildOverlapWarning(ext);
        // Block persisting an invalid MoveTo with empty destination — user must fill it.
        if (row.HasValidationError)
            return;
        Save();
    }

    public void SetNewDestination(string path)
    {
        NewDestination = path;
        OnPropertyChanged(nameof(NewDestination));
    }
}
