using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ScriptSuite.Models;
using ScriptSuite.Services;
using ScriptSuite.ViewModels;

namespace ScriptSuite.Views;

/// <summary>First-run setup wizard. Shows one step per script that has
/// configurable fields, reusing the schema-driven SettingsForm. Completion is
/// tracked by wizard.json (config presence can't signal first run because
/// configs are seeded on every launch); the marker is written only on finish
/// or explicit skip — closing early shows the wizard again next launch.
///
/// ACCEPTED TRADE-OFF (known, intentional): steps auto-save live config
/// values on every edit; only the completion MARKER is deferred. Closing
/// early does NOT roll edits back. "Use recommended defaults" overwrites all
/// of a step's fields with manifest defaults before advancing.
/// Ported from windows-scripts.</summary>
public partial class FirstRunWizardWindow : Window
{
    private readonly List<ScriptManifest> _steps = new();
    private readonly ScriptConfigService _config = new();
    private int _index;
    private SettingsFormViewModel? _form;

    public FirstRunWizardWindow()
    {
        InitializeComponent();
    }

    public FirstRunWizardWindow(IEnumerable<ScriptManifest> catalog, ScriptConfigService config)
        : this()
    {
        _config = config;
        // Skip scripts with no configurable fields so every step shows
        // at least one editable setting (all 8 Linux scripts have fields).
        _steps = catalog.Where(m => m.Fields.Count > 0).ToList();
        if (_steps.Count > 0)
            ShowStep(0);
    }

    /// <summary>Steps for headless tests (the window itself needs a UI thread).</summary>
    public IReadOnlyList<string> StepIds => _steps.Select(m => m.Id).ToList();

    private void ShowStep(int index)
    {
        _index = index;
        var manifest = _steps[index];
        StepHeaderText.Text = $"Step {index + 1} of {_steps.Count} — {manifest.DisplayName}";
        StepDescriptionText.Text = manifest.Description;
        _form = new SettingsFormViewModel(manifest, _config);
        WizardFormControl.Form = _form;
        BackButton.IsEnabled = index > 0;
        bool last = index == _steps.Count - 1;
        NextButton.Content = last ? "Finish" : "Next";
    }

    private void Advance()
    {
        _form?.FlushPending();
        if (_index == _steps.Count - 1)
            CompleteAndClose();
        else
            ShowStep(_index + 1);
    }

    private void CompleteAndClose()
    {
        WizardStateStore.MarkCompleted();
        Close();
    }

    private void Recommended_Click(object? sender, RoutedEventArgs e)
    {
        _form?.FlushPending();
        _form?.ApplyRecommendedDefaults();
        if (_index == _steps.Count - 1)
            CompleteAndClose();
        else
            ShowStep(_index + 1);
    }

    private void Back_Click(object? sender, RoutedEventArgs e)
    {
        if (_index > 0)
        {
            _form?.FlushPending();
            ShowStep(_index - 1);
        }
    }

    private void Next_Click(object? sender, RoutedEventArgs e) => Advance();

    private void Skip_Click(object? sender, RoutedEventArgs e) => CompleteAndClose();

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Save any in-flight text edit, but do NOT write the marker: closing
        // the wizard before finishing/skipping means it shows again next launch.
        _form?.FlushPending();
        base.OnClosing(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            // Closing early keeps the marker absent, so the wizard reappears
            // next launch (same as the title-bar close).
            Close();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }
}
