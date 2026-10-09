using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ScriptSuite.Services;
using ScriptSuite.ViewModels;
using ScriptSuite.Views;

namespace ScriptSuite;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = new MainWindow
            {
                DataContext = new MainViewModel(),
            };
            desktop.MainWindow = mainWindow;
            // First-run wizard: modal over the shown dashboard, like
            // windows-scripts (marker file, not config presence, gates it).
            mainWindow.Opened += async (_, _) =>
            {
                if (!WizardStateStore.IsCompleted())
                {
                    var catalog = new ManifestCatalog(AppPaths.ManifestsDir);
                    var wizard = new FirstRunWizardWindow(catalog.All, new ScriptConfigService());
                    await wizard.ShowDialog(mainWindow);
                }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}