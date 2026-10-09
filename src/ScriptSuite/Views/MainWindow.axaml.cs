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
                vm.ShowRunWindow = async runVm =>
                {
                    var win = new ScriptRunWindow { DataContext = runVm };
                    await win.ShowDialog<bool?>(this);
                    vm.Refresh();
                };
                vm.ShowHistory = async historyVm =>
                {
                    var win = new HistoryWindow { DataContext = historyVm };
                    await win.ShowDialog<bool?>(this);
                };
                vm.ShowScheduledHistory = async scheduledVm =>
                {
                    var win = new ScheduledHistoryWindow { DataContext = scheduledVm };
                    await win.ShowDialog<bool?>(this);
                };
                vm.ShowRunAll = async runAllVm =>
                {
                    var win = new RunAllPreviewWindow { DataContext = runAllVm };
                    await win.ShowDialog<bool?>(this);
                };
                vm.ShowSettings = async (manifest, config) =>
                {
                    var win = new SettingsWindow(manifest, config);
                    await win.ShowDialog<bool?>(this);
                };
            }
        };
    }
}
