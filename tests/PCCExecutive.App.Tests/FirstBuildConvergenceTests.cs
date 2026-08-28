using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using PCCExecutive.App.Presentation;
using PCCExecutive.App.ViewModels;
using Xunit;

namespace PCCExecutive.App.Tests;

public sealed class FirstBuildConvergenceTests
{
    [Fact]
    public void Real_project_resolution_auto_connects_chrome_and_advances_to_manager()
    {
        var gateway = new ProjectSelectionGateway(resolve: true);
        var vm = new MainViewModel(gateway);

        Assert.Equal(ScreenId.ChromeConnection, vm.SelectedScreen);
        vm.Navigate(ScreenId.ProjectSelection);

        vm.SelectProjectCommand.Execute("PCCEXECUTIVE");

        Assert.Equal(ScreenId.ManagerWorkspace, vm.SelectedScreen);
        Assert.True(vm.Snapshot.HasActiveRun);
        Assert.Contains(gateway.Executions, x => x == (UiAction.SelectProject, "PCCEXECUTIVE"));
        Assert.Contains(gateway.Executions, x => x.Action == UiAction.ConnectChrome);
        Assert.Contains(vm.Snapshot.Sessions, x => x.IsPccOwned && string.Equals(x.Role, "Manager", StringComparison.OrdinalIgnoreCase));
        Assert.False(vm.HasUiError);
    }

    [Fact]
    public void Failed_project_resolution_remains_on_project_selection_with_honest_error()
    {
        var gateway = new ProjectSelectionGateway(resolve: false);
        var vm = new MainViewModel(gateway);
        vm.Navigate(ScreenId.ProjectSelection);

        vm.SelectProjectCommand.Execute("missing-project");

        Assert.Equal(ScreenId.ProjectSelection, vm.SelectedScreen);
        Assert.False(vm.Snapshot.HasActiveRun);
        Assert.True(vm.HasUiError);
        Assert.Contains("did not resolve", vm.LastUiError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Session_mutations_remain_disabled_when_runtime_ownership_is_not_proven()
    {
        var gateway = new ProjectSelectionGateway(resolve: true, allowOwnedSessionActions: false);
        var vm = new MainViewModel(gateway);

        Assert.False(vm.OpenSessionCommand.CanExecute("runtime-unknown"));
        Assert.False(vm.BringSessionToFrontCommand.CanExecute("runtime-unknown"));
        Assert.False(vm.HideSessionCommand.CanExecute("runtime-unknown"));
        Assert.False(vm.RestartSessionCommand.CanExecute("runtime-unknown"));
        Assert.False(vm.KillSessionCommand.CanExecute("runtime-unknown"));
        Assert.False(vm.KillAllPccSessionsCommand.CanExecute(null));
    }

    [Fact]
    public void Save_settings_forwards_every_supported_operator_selection()
    {
        var gateway = new ProjectSelectionGateway(resolve: true);
        var vm = new MainViewModel(gateway)
        {
            SelectedDispatchMode = DispatchMode.Assisted,
            SelectedBaseIntervalSeconds = 17,
            SelectedMaxWorkers = 3,
            SelectedAdaptivePacing = false,
            SelectedAutoResume = false
        };

        vm.SaveSettingsCommand.Execute(null);

        Assert.Equal(UiAction.SaveSettings, gateway.LastExecution?.Action);
        Assert.Equal("provider=BrowserWeb;dispatch=Assisted;interval=17;maxWorkers=3;adaptive=False;autoResume=False", gateway.LastExecution?.Target);
    }

    [Fact]
    public void Exact_built_app_completes_integrated_wpf_startup_smoke()
    {
        var repoRoot = FindRepositoryRoot();
        var appExe = Path.Combine(repoRoot, "src", "PCCExecutive.App", "bin", "Release", "net10.0-windows", "PCCExecutive.exe");
        Assert.True(File.Exists(appExe), $"Expected exact-head WPF executable was not found: {appExe}");

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = appExe,
            Arguments = "--smoke-test",
            UseShellExecute = false,
            CreateNoWindow = false,
            WorkingDirectory = repoRoot
        });

        Assert.NotNull(process);
        if (!process!.WaitForExit(20_000))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("PCC Executive startup smoke did not exit within 20 seconds.");
        }

        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public void Premium_shell_survives_1920x1080_dpi_equivalent_viewports()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var application = new PCCExecutive.App.App();
                application.InitializeComponent();
                application.ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;

                foreach (var viewport in new[]
                {
                    (Width: 1920d, Height: 1080d, Scale: "100%"),
                    (Width: 1536d, Height: 864d, Scale: "125%"),
                    (Width: 1280d, Height: 720d, Scale: "150%")
                })
                {
                    var vm = new MainViewModel(new ProjectSelectionGateway(resolve: false));
                    var window = new MainWindow(vm)
                    {
                        Width = viewport.Width,
                        Height = viewport.Height,
                        WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
                        Left = 0,
                        Top = 0
                    };
                    window.Show();
                    window.Dispatcher.Invoke(window.UpdateLayout);

                    Assert.True(window.IsLoaded, $"{viewport.Scale} viewport did not load MainWindow.");
                    Assert.True(window.IsVisible, $"{viewport.Scale} viewport did not show MainWindow.");
                    Assert.True(window.ActualWidth >= window.MinWidth, $"{viewport.Scale} viewport collapsed below minimum width.");
                    Assert.True(window.ActualHeight >= window.MinHeight, $"{viewport.Scale} viewport collapsed below minimum height.");
                    Assert.Equal(ScreenId.ChromeConnection, vm.SelectedScreen);
                    Assert.Equal(ScreenId.ChromeConnection, vm.Navigation[0].Id);
                    Assert.Equal("01  Chrome", vm.Navigation[0].Label);
                    Assert.Equal(ScreenId.ProjectSelection, vm.Navigation[1].Id);
                    Assert.Equal("02  Projects", vm.Navigation[1].Label);
                    Assert.Equal(ScreenId.Dashboard, vm.Navigation[2].Id);
                    Assert.Equal(17, vm.Navigation.Count);
                    Assert.True(vm.RefreshCommand.CanExecute(null));
                    Assert.NotNull(vm.CurrentScreen);

                    window.AllowCloseAndClose();
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(30_000), "DPI-equivalent viewport layout smoke timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "VERSION")) &&
                File.Exists(Path.Combine(directory.FullName, "src", "PCCExecutive.App", "PCCExecutive.App.csproj")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate PCC Executive repository root from the test output directory.");
    }

    private sealed class ProjectSelectionGateway(bool resolve, bool allowOwnedSessionActions = true) : IPccExecutivePresentationGateway
    {
        private RuntimeSnapshot _snapshot = RuntimeSnapshot.Unbound with
        {
            GatewayBound = true,
            HasActiveRun = false,
            RuntimeStatus = "Select a project to begin",
            ProviderMode = ProviderMode.BrowserWeb
        };

        public RuntimeSnapshot Snapshot => _snapshot;
        public event EventHandler<RuntimeSnapshot>? SnapshotChanged;
        public (UiAction Action, string? Target)? LastExecution { get; private set; }
        public List<(UiAction Action, string? Target)> Executions { get; } = [];

        public bool CanExecute(UiAction action, string? targetId = null) => action switch
        {
            UiAction.SelectProject or UiAction.Refresh or UiAction.SaveSettings or UiAction.RunVerification or UiAction.StartManager => true,
            UiAction.ConnectChrome => _snapshot.HasActiveRun,
            UiAction.OpenSession or UiAction.BringSessionToFront or UiAction.HideSession or UiAction.RestartSession or UiAction.KillSession or UiAction.KillAllPccSessions => allowOwnedSessionActions,
            _ => false
        };

        public Task ExecuteAsync(UiAction action, string? targetId = null, CancellationToken cancellationToken = default)
        {
            LastExecution = (action, targetId);
            Executions.Add((action, targetId));
            if (action == UiAction.SelectProject && resolve)
            {
                _snapshot = _snapshot with
                {
                    HasActiveRun = true,
                    RuntimeStatus = "Integrated runtime",
                    CurrentWave = "Manager planning"
                };
                SnapshotChanged?.Invoke(this, _snapshot);
            }
            else if (action == UiAction.ConnectChrome && _snapshot.HasActiveRun)
            {
                _snapshot = _snapshot with
                {
                    Sessions =
                    [
                        new SessionSummary(
                            "manager-runtime",
                            "Manager",
                            "Manager",
                            "READY",
                            SessionVisibility.Hidden,
                            "New conversation",
                            DateTimeOffset.UtcNow,
                            true,
                            4242,
                            HealthState.Healthy)
                    ]
                };
                SnapshotChanged?.Invoke(this, _snapshot);
            }
            return Task.CompletedTask;
        }
    }
}
