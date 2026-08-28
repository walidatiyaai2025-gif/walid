using System.Windows;
using PCCExecutive.App.Presentation;
using PCCExecutive.App.Services;
using PCCExecutive.App.ViewModels;

namespace PCCExecutive.App;

public partial class App : System.Windows.Application
{
    private TrayIconService? _tray;
    private TerminalPresentationGateway? _gateway;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            _gateway = TerminalPresentationGateway.Create();

            if (e.Args.Any(arg => string.Equals(arg, "--smoke-test", StringComparison.OrdinalIgnoreCase)))
            {
                if (!_gateway.Snapshot.GatewayBound)
                    throw new InvalidOperationException("Integrated runtime gateway did not initialize.");
                Shutdown(0);
                return;
            }

            var viewModel = new MainViewModel(_gateway, new WpfConfirmationService());
            viewModel.Navigate(_gateway.Snapshot.HasActiveRun ? ScreenId.Dashboard : ScreenId.ProjectSelection);
            _gateway.SnapshotChanged += (_, snapshot) =>
            {
                if (snapshot.HasActiveRun && viewModel.SelectedScreen == ScreenId.ProjectSelection)
                    Dispatcher.Invoke(() => viewModel.Navigate(ScreenId.Dashboard));
            };

            var window = new MainWindow(viewModel);
            MainWindow = window;
            _tray = new TrayIconService(window, viewModel);
            window.Show();
        }
        catch (Exception ex)
        {
            if (!e.Args.Any(arg => string.Equals(arg, "--smoke-test", StringComparison.OrdinalIgnoreCase)))
                System.Windows.MessageBox.Show(ex.Message, "PCC Executive startup failed", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(2);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        if (_gateway is not null)
            _gateway.DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.OnExit(e);
    }
}
