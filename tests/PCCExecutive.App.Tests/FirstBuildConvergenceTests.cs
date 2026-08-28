using System.Diagnostics;
using PCCExecutive.App.Presentation;
using PCCExecutive.App.ViewModels;
using Xunit;

namespace PCCExecutive.App.Tests;

public sealed class FirstBuildConvergenceTests
{
    [Fact]
    public void Real_project_resolution_advances_first_run_to_dashboard()
    {
        var gateway = new ProjectSelectionGateway(resolve: true);
        var vm = new MainViewModel(gateway);

        Assert.Equal(ScreenId.ProjectSelection, vm.SelectedScreen);

        vm.SelectProjectCommand.Execute("PCCEXECUTIVE");

        Assert.Equal(ScreenId.Dashboard, vm.SelectedScreen);
        Assert.True(vm.Snapshot.HasActiveRun);
        Assert.Equal((UiAction.SelectProject, "PCCEXECUTIVE"), gateway.LastExecution);
        Assert.False(vm.HasUiError);
    }

    [Fact]
    public void Failed_project_resolution_remains_on_project_selection_with_honest_error()
    {
        var gateway = new ProjectSelectionGateway(resolve: false);
        var vm = new MainViewModel(gateway);

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

        public bool CanExecute(UiAction action, string? targetId = null) => action switch
        {
            UiAction.SelectProject or UiAction.Refresh or UiAction.SaveSettings or UiAction.RunVerification => true,
            UiAction.OpenSession or UiAction.BringSessionToFront or UiAction.HideSession or UiAction.RestartSession or UiAction.KillSession or UiAction.KillAllPccSessions => allowOwnedSessionActions,
            _ => false
        };

        public Task ExecuteAsync(UiAction action, string? targetId = null, CancellationToken cancellationToken = default)
        {
            LastExecution = (action, targetId);
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
            return Task.CompletedTask;
        }
    }
}
