using Avalonia.Controls;
using Avalonia.Interactivity;
using ScriptSuite.ViewModels;

namespace ScriptSuite.Views;

public partial class HistoryWindow : Window
{
    public HistoryWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is HistoryViewModel vm)
                vm.RequestClose += Close;
        };
    }

    private void OpenLog_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is HistoryRowViewModel row
            && DataContext is HistoryViewModel vm && vm.OpenLogCommand.CanExecute(row))
            vm.OpenLogCommand.Execute(row);
    }

    private void OpenFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is HistoryRowViewModel row
            && DataContext is HistoryViewModel vm && vm.OpenFolderCommand.CanExecute(row))
            vm.OpenFolderCommand.Execute(row);
    }
}
