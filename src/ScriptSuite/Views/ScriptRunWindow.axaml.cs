using Avalonia.Controls;
using Avalonia.Input;
using ScriptSuite.ViewModels;

namespace ScriptSuite.Views;

public partial class ScriptRunWindow : Window
{
    public ScriptRunWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ScriptRunViewModel vm)
                vm.RequestClose += Close;
        };
        Opened += async (_, _) =>
        {
            if (DataContext is ScriptRunViewModel vm)
                await vm.LoadAsync();
        };
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Escape closes only when no run is in flight (Windows parity);
        // window-chrome close mid-run is refused outright (safer than Windows).
        if (e.Key == Key.Escape && DataContext is ScriptRunViewModel vm && !vm.IsRunning)
            Close();
        else
            base.OnKeyDown(e);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (DataContext is ScriptRunViewModel vm && vm.IsRunning)
            e.Cancel = true;
        base.OnClosing(e);
    }
}
