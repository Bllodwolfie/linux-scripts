using Avalonia.Controls;
using ScriptSuite.ViewModels;

namespace ScriptSuite.Views;

public partial class ScheduleDialog : Window
{
    public ScheduleDialog()
    {
        InitializeComponent();
        if (DataContext is ScheduleDialogViewModel vm)
            vm.RequestClose += r => Close(r);
        else
            DataContextChanged += (_, _) =>
            {
                if (DataContext is ScheduleDialogViewModel v)
                    v.RequestClose += r => Close(r);
            };
    }
}
