using Avalonia.Controls;
using Avalonia.Interactivity;
using ScriptSuite.ViewModels;

namespace ScriptSuite.Views;

public partial class RunAllPreviewWindow : Window
{
    public RunAllPreviewWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is RunAllViewModel vm)
                vm.RequestClose += Close;
        };
        Opened += async (_, _) =>
        {
            if (DataContext is RunAllViewModel vm)
                await vm.LoadAsync();
        };
    }

    private void TogglePreview_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is RunScriptPreviewItem item)
            item.TogglePreview();
    }

    private void ToggleLogs_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is RunScriptPreviewItem item)
            item.ToggleLogs();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (DataContext is RunAllViewModel vm && vm.IsRunning)
            e.Cancel = true;
        base.OnClosing(e);
    }
}
