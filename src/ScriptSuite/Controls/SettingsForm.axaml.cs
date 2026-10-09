using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ScriptSuite.Services;
using ScriptSuite.ViewModels;

namespace ScriptSuite.Controls;

/// <summary>Selects the per-type control template for a settings field. Keeps
/// the field rendering schema-driven: adding a field type means adding a
/// template and a case here, nothing else. Avalonia port of the WPF
/// DataTemplateSelector (IDataTemplate instead).</summary>
public sealed class FieldTemplateSelector : IDataTemplate
{
    public IDataTemplate? IntTemplate { get; set; }
    public IDataTemplate? StringTemplate { get; set; }
    public IDataTemplate? PathTemplate { get; set; }
    public IDataTemplate? BoolTemplate { get; set; }
    public IDataTemplate? ListTemplate { get; set; }
    public IDataTemplate? PathListTemplate { get; set; }
    public IDataTemplate? PatternListTemplate { get; set; }

    public Control? Build(object? param)
    {
        IDataTemplate? template = param is SettingsFieldViewModel vm ? vm.Field.Type switch
        {
            "int" => IntTemplate,
            "bool" => BoolTemplate,
            "path" => PathTemplate,
            "stringList" => ListTemplate,
            "pathList" => PathListTemplate,
            "patternList" => PatternListTemplate,
            _ => StringTemplate,
        } : null;
        return template?.Build(param);
    }

    public bool Match(object? data) => data is SettingsFieldViewModel;
}

/// <summary>Reusable schema-driven settings form: renders one control per
/// manifest field (int stepper, string textbox, path + browse, bool checkbox,
/// list chips), auto-saves every change, and provides a session Undo.
/// Hosted by SettingsWindow and by the first-run wizard.
/// File/folder Browse uses the Avalonia StorageProvider (Windows used the
/// Win32 dialogs); start dir falls back to ~/Documents like MyDocuments did.</summary>
public partial class SettingsForm : UserControl
{
    public SettingsForm()
    {
        InitializeComponent();
    }

    public SettingsFormViewModel? Form
    {
        get => DataContext as SettingsFormViewModel;
        set => DataContext = value;
    }

    private void Text_LostFocus(object? sender, RoutedEventArgs e) => CommitPending(sender);

    private void Text_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            CommitPending(sender);
    }

    private void AddItem_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (sender as Control)?.DataContext is SettingsFieldViewModel vm
            && vm.AddNewItemCommand.CanExecute(null))
            vm.AddNewItemCommand.Execute(null);
    }

    private static void CommitPending(object? sender)
    {
        if ((sender as Control)?.DataContext is SettingsFieldViewModel vm)
            vm.CommitPending();
    }

    private async void Browse_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not SettingsFieldViewModel vm)
            return;
        var sp = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (sp is null) return;

        string expanded = PathEnv.ExpandUserPath(vm.TextValue ?? "");
        var start = await TryGetFolder(sp,
            Path.IsPathRooted(expanded)
                ? (Directory.Exists(expanded) ? expanded : Path.GetDirectoryName(expanded))
                : null)
            ?? await TryGetFolder(sp, PathEnv.DocumentsDir());

        if (vm.IsFilePath)
        {
            var opts = new FilePickerSaveOptions
            {
                Title = "Choose file for " + vm.Label,
                SuggestedStartLocation = start,
            };
            if (Path.IsPathRooted(expanded))
                opts.SuggestedFileName = Path.GetFileName(expanded);
            var file = await sp.SaveFilePickerAsync(opts);
            if (file is not null)
                vm.SetPath(file.Path.LocalPath);
        }
        else
        {
            var folders = await sp.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Choose folder for " + vm.Label,
                SuggestedStartLocation = start,
            });
            if (folders.Count > 0)
                vm.SetPath(folders[0].Path.LocalPath);
        }
    }

    /// <summary>Browse for a pathList entry (e.g. EmptyFolderCleanup IgnoreFolders):
    /// fills the add-row text box; the user still presses Add to commit it.</summary>
    private async void BrowseNewItem_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not SettingsFieldViewModel vm)
            return;
        var sp = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (sp is null) return;

        string expanded = PathEnv.ExpandUserPath(vm.NewItemText ?? "");
        var start = await TryGetFolder(sp,
            Path.IsPathRooted(expanded) && Directory.Exists(expanded) ? expanded : null)
            ?? await TryGetFolder(sp, PathEnv.DocumentsDir());

        var folders = await sp.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose folder for " + vm.Label,
            SuggestedStartLocation = start,
        });
        if (folders.Count > 0)
            vm.NewItemText = folders[0].Path.LocalPath;
    }

    private static async Task<IStorageFolder?> TryGetFolder(IStorageProvider sp, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            return await sp.TryGetFolderFromPathAsync(new Uri(path));
        }
        catch
        {
            return null;
        }
    }
}
