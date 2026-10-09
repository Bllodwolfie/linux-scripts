using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ScriptSuite.Services;
using ScriptSuite.ViewModels;

namespace ScriptSuite.Controls;

public partial class DownloadsAdvancedRulesControl : UserControl
{
    public DownloadsAdvancedRulesControl()
    {
        InitializeComponent();
    }

    private DownloadsAdvancedRulesViewModel? ViewModel => DataContext as DownloadsAdvancedRulesViewModel;

    private void RemoveRule_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control c && c.Tag is AdvancedRuleRowViewModel row)
            ViewModel?.Remove(row);
        else if ((sender as Control)?.DataContext is AdvancedRuleRowViewModel row2)
            ViewModel?.Remove(row2);
    }

    private void AddRule_Click(object? sender, RoutedEventArgs e) => ViewModel?.AddNew();

    private void Rule_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if ((sender as Control)?.DataContext is AdvancedRuleRowViewModel row)
            ViewModel?.NotifyRowChanged(row);
    }

    private void Rule_LostFocus(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is AdvancedRuleRowViewModel row)
            ViewModel?.NotifyRowChanged(row);
    }

    private async void BrowseDestination_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is not AdvancedRuleRowViewModel row) return;
        var sp = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (sp is null) return;
        var start = await TryGetFolder(sp, row.Destination)
            ?? await TryGetFolder(sp, PathEnv.DocumentsDir());
        var folders = await sp.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose folder for " + row.Extension,
            SuggestedStartLocation = start,
        });
        if (folders.Count > 0)
        {
            row.Destination = folders[0].Path.LocalPath;
            ViewModel?.NotifyRowChanged(row);
        }
    }

    private async void BrowseNewDestination_Click(object? sender, RoutedEventArgs e)
    {
        var vm = ViewModel;
        if (vm is null) return;
        var sp = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (sp is null) return;
        var start = await TryGetFolder(sp, vm.NewDestination)
            ?? await TryGetFolder(sp, PathEnv.DocumentsDir());
        var folders = await sp.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose folder for " + (string.IsNullOrWhiteSpace(vm.NewExtension) ? "new rule" : vm.NewExtension),
            SuggestedStartLocation = start,
        });
        if (folders.Count > 0)
            vm.SetNewDestination(folders[0].Path.LocalPath);
    }

    private static async Task<IStorageFolder?> TryGetFolder(IStorageProvider sp, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            string expanded = PathEnv.ExpandUserPath(path);
            if (!Path.IsPathRooted(expanded)) return null;
            string dir = Directory.Exists(expanded) ? expanded : Path.GetDirectoryName(expanded) ?? "";
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
            return await sp.TryGetFolderFromPathAsync(new Uri(dir));
        }
        catch
        {
            return null;
        }
    }
}
