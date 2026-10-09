using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScriptSuite.Models;
using ScriptSuite.Services;

namespace ScriptSuite.ViewModels;

/// <summary>State for one script's settings form: one field VM per manifest
/// field, plus the session-scoped Undo stack. Auto-save writes every committed
/// change straight to the script's config JSON; Undo pops the most recent
/// change, restores the field, and re-saves the reverted value. The stack
/// lives for the lifetime of this instance (one visit to a settings screen)
/// and is discarded when the form is closed.
/// Ported verbatim from windows-scripts (CommunityToolkit base).</summary>
public partial class SettingsFormViewModel : ViewModelBase
{
    private readonly ScriptConfigService _config;
    private readonly ScriptManifest _manifest;
    private readonly Stack<(string Name, JsonNode? Prior)> _undoStack = new();

    public SettingsFormViewModel(ScriptManifest manifest, ScriptConfigService config)
    {
        _manifest = manifest;
        _config = config;
        var doc = config.Load(manifest.Id);
        foreach (var field in manifest.Fields)
            Fields.Add(new SettingsFieldViewModel(field, LoadValue(doc, field), Commit));
    }

    public ObservableCollection<SettingsFieldViewModel> Fields { get; } = new();

    public bool CanUndo => _undoStack.Count > 0;

    /// <summary>Commits any in-flight text edit first (so the box's current
    /// text is what Undo reverts), then pops the most recent change, restores
    /// the field, and re-saves the reverted value without pushing again.</summary>
    [RelayCommand]
    public void Undo()
    {
        FlushPending();
        if (_undoStack.Count == 0)
            return;
        var (name, prior) = _undoStack.Pop();
        Fields.FirstOrDefault(x => x.Field.Name == name)?.Restore(prior);
        _config.SetField(_manifest.Id, name, prior);
        OnPropertyChanged(nameof(CanUndo));
    }

    /// <summary>Commits every field's deferred text edit. Called on focus loss
    /// and on window close so no typed value is ever lost.</summary>
    public void FlushPending()
    {
        foreach (var field in Fields)
            field.CommitPending();
    }

    /// <summary>Accepts the manifest's default value for every field on this
    /// step: writes them all to the config and re-syncs the field controls.
    /// Used by the wizard's "Use recommended defaults" and the settings
    /// window's reset path.</summary>
    public void ApplyRecommendedDefaults()
    {
        foreach (var field in _manifest.Fields)
        {
            JsonNode? def = field.HasDefault ? JsonNode.Parse(field.Default.GetRawText()) : null;
            _config.SetField(_manifest.Id, field.Name, def);
            Fields.FirstOrDefault(x => x.Field.Name == field.Name)?.Restore(def);
        }
        _undoStack.Clear();
        OnPropertyChanged(nameof(CanUndo));
    }

    private void Commit(SettingsFieldViewModel field, JsonNode? prior, JsonNode? next)
    {
        _config.SetField(_manifest.Id, field.Field.Name, next);
        _undoStack.Push((field.Field.Name, prior));
        OnPropertyChanged(nameof(CanUndo));
    }

    private static JsonNode? LoadValue(JsonObject doc, ScriptField field)
    {
        if (doc.TryGetPropertyValue(field.Name, out var v) && v is not null && v.GetValueKind() != JsonValueKind.Null)
            return v.DeepClone();
        return null; // SettingsFieldViewModel falls back to the manifest default
    }
}
