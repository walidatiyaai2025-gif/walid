using System.Windows;
using PCCExecutive.App.Presentation;
using PCCExecutive.App.Services;
using PCCExecutive.App.ViewModels;

namespace PCCExecutive.App;

public partial class App : Application
{
    private TrayIconService? _tray;
    private IntegratedPresentationGateway? _gateway;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            _gateway = IntegratedPresentationGateway.Create();

            if (e.Args.Any(arg => string.Equals(arg, "--smoke-test", StringComparison.OrdinalIgnoreCase)))
            {
                if (!_gateway.Snapshot.GatewayBound || !_gateway.Snapshot.HasActiveRun)
                    throw new InvalidOperationException("Integrated runtime gateway did not initialize an active durable project run.");
                Shutdown(0);
                return;
            }

            var viewModel = new MainViewModel(_gateway, new WpfConfirmationService());
            var window = new MainWindow(viewModel);
            MainWindow = window;

            _tray = new TrayIconService(window, viewModel);
            window.Show();
        }
        catch (Exception ex)
        {
            if (!e.Args.Any(arg => string.Equals(arg, "--smoke-test", StringComparison.OrdinalIgnoreCase)))
                MessageBox.Show(ex.Message, "PCC Executive startup failed", MessageBoxButton.OK, MessageBoxImage.Error);
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
