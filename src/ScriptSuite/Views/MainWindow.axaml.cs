using Avalonia.Controls;
using ScriptSuite.ViewModels;

namespace ScriptSuite.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm && vm.ShowScheduleDialog is null)
            {
                vm.ShowScheduleDialog = async card =>
                {
                    var dlg = new ScheduleDialog
                    {
                        DataContext = new ScheduleDialogViewModel(
                            card.Manifest, vm.Schedules, vm.Consents),
                    };
                    return await dlg.ShowDialog<bool?>(this) == true;
                };
            }
        };
    }
}
