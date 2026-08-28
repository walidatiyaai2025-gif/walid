using System.Windows;
using PCCExecutive.App.Presentation;
using PCCExecutive.App.Services;
using PCCExecutive.App.ViewModels;
using PCCExecutive.Infrastructure;

namespace PCCExecutive.App;

public partial class App : System.Windows.Application
{
    private TrayIconService? _tray;
    private PccExecutiveRuntimeHost? _gateway;
    private DispatcherPresentationGateway? _uiGateway;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var smokeTest = e.Args.Any(arg =>
            string.Equals(arg, "--smoke-test", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "--installer-smoke", StringComparison.OrdinalIgnoreCase));

        try
        {
            var applicationVersion = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
            PackagedStartupSchemaSafety.EnsureDefaultCurrentAsync(applicationVersion).GetAwaiter().GetResult();
            _gateway = PccExecutiveRuntimeHost.Create();
            _uiGateway = new DispatcherPresentationGateway(_gateway, Dispatcher);
            var viewModel = new MainViewModel(_uiGateway, new WpfConfirmationService());
            var window = new MainWindow(viewModel);
            MainWindow = window;

            if (smokeTest)
            {
                window.Show();
                window.Dispatcher.Invoke(window.UpdateLayout);
                if (!window.IsLoaded || !window.IsVisible)
                    throw new InvalidOperationException("PCC Executive WPF startup smoke did not create and render the main window.");
                window.AllowCloseAndClose();
                Shutdown(0);
                return;
            }

            _tray = new TrayIconService(window, viewModel);
            window.Show();
        }
        catch (Exception ex)
        {
            if (!smokeTest)
                System.Windows.MessageBox.Show(ex.Message, "PCC Executive startup failed", MessageBoxButton.OK, MessageBoxImage.Error);
            else
                Console.Error.WriteLine(ex);
            Shutdown(2);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _uiGateway?.Dispose();
        if (_gateway is not null)
            _gateway.DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.OnExit(e);
    }
}
