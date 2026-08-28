using System.Windows;
using PCCExecutive.App.Presentation;
using PCCExecutive.App.Services;
using PCCExecutive.App.ViewModels;

namespace PCCExecutive.App;

public partial class App : Application
{
    private TrayIconService? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // The Integration Lead replaces this honest unbound gateway with the adapter
        // backed by the canonical Application/Browser/Infrastructure contracts.
        var gateway = new UnavailablePresentationGateway();
        var viewModel = new MainViewModel(gateway, new WpfConfirmationService());
        var window = new MainWindow(viewModel);
        MainWindow = window;

        _tray = new TrayIconService(window, viewModel);
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        base.OnExit(e);
    }
}
