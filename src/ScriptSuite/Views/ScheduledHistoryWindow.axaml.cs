using Avalonia.Controls;
using ScriptSuite.ViewModels;

namespace ScriptSuite.Views;

public partial class ScheduledHistoryWindow : Window
{
    public ScheduledHistoryWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ScheduledHistoryViewModel vm)
                vm.RequestClose += Close;
        };
    }
}
