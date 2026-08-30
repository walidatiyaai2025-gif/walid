using System.Windows;
using PCCExecutive.App.Presentation;
using PCCExecutive.App.Services;
using PCCExecutive.App.ViewModels;
using PCCExecutive.Infrastructure;
using PCCExecutive.Application;

namespace PCCExecutive.App;

public partial class App : System.Windows.Application
{
    private TrayIconService? _tray;
    private PccExecutiveRuntimeHost? _gateway;
    private DispatcherPresentationGateway? _uiGateway;
    private IRuntimeDiagnosticCollector? _diagnostics;

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
            var diagnosticPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCC Executive", "runtime-diagnostics.db");
            var diagnosticStore = new SqliteRuntimeDiagnosticStore(diagnosticPath);
            diagnosticStore.InitializeAsync().GetAwaiter().GetResult();
            _diagnostics = new RuntimeDiagnosticCollector(diagnosticStore, diagnosticStore);
            MainViewModel? viewModel = null;
            var stateSource = new SnapshotRuntimeInspectorStateSource(() => viewModel?.Snapshot ?? _uiGateway.Snapshot);
            var snapshotService = new RuntimeDiagnosticSnapshotService(diagnosticStore, stateSource);
            var inspector = new RuntimeInspectorServices(_diagnostics, stateSource, snapshotService.CreateJsonAsync);
            viewModel = new MainViewModel(_uiGateway, new WpfConfirmationService(), inspector);
            var startupCorrelation = _diagnostics.BeginCorrelation();
            _diagnostics.RecordAsync(_diagnostics.Create(RuntimeDiagnosticKind.StateTransition, "APPLICATION_STARTED", "Application startup and diagnostic persistence initialized.", startupCorrelation, afterState: "READY")).GetAwaiter().GetResult();
            var window = new MainWindow(viewModel);
            MainWindow = window;

            if (smokeTest)
            {
                window.Show();
                window.Dispatcher.Invoke(window.UpdateLayout);
                if (!window.IsLoaded || !window.IsVisible)
                    throw new InvalidOperationException("PCC Executive WPF startup smoke did not create and render the main window.");

                // Give the external package smoke probe a deterministic window in which
                // MainWindowHandle is observable, then close through the normal WPF path.
                System.Threading.Thread.Sleep(1000);
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
        if (_diagnostics is not null)
        {
            var correlation = _diagnostics.BeginCorrelation();
            _diagnostics.RecordAsync(_diagnostics.Create(RuntimeDiagnosticKind.StateTransition, "APPLICATION_EXITING", "Application shutdown started.", correlation, beforeState: "READY", afterState: "STOPPING")).GetAwaiter().GetResult();
        }
        _tray?.Dispose();
        _uiGateway?.Dispose();
        if (_gateway is not null)
            _gateway.DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.OnExit(e);
    }
}
