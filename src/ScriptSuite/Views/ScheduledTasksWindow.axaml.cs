using Avalonia.Controls;
using ScriptSuite.ViewModels;

namespace ScriptSuite.Views;

public partial class ScheduledTasksWindow : Window
{
    public ScheduledTasksWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ScheduledTasksViewModel vm)
                vm.RequestClose += Close;
        };
    }
}
