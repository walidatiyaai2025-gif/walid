using PCCExecutive.App.Presentation;
using PCCExecutive.App.ViewModels;

namespace PCCExecutive.App.Tests;

public sealed class MainViewModelTests
{
    [Fact]
    public void Navigation_changes_real_current_screen()
    {
        var vm = new MainViewModel(new FakeGateway(TestSnapshots.Healthy));
        vm.Navigate(ScreenId.SessionMonitor);
        Assert.Equal(ScreenId.SessionMonitor, vm.SelectedScreen);
        Assert.IsType<SessionMonitorViewModel>(vm.CurrentScreen);
    }

    [Fact]
    public void Provider_default_is_browser_web_and_api_requires_explicit_configuration()
    {
        var vm = new MainViewModel(new FakeGateway(TestSnapshots.Healthy with { ApiConfigured = false }));
        Assert.Equal(ProviderMode.BrowserWeb, vm.SelectedProviderMode);
        vm.SelectedProviderMode = ProviderMode.OpenAiApi;
        Assert.Equal(ProviderMode.BrowserWeb, vm.SelectedProviderMode);
        Assert.True(vm.HasUiError);
    }

    [Fact]
    public void Manager_estimate_and_verified_completion_remain_distinct()
    {
        var snapshot = TestSnapshots.Healthy with { VerifiedCompletion = 72, ManagerEstimate = 91 };
        Assert.Equal("72%", snapshot.VerifiedCompletionText);
        Assert.Equal("91%", snapshot.ManagerEstimateText);
        Assert.NotEqual(snapshot.VerifiedCompletionText, snapshot.ManagerEstimateText);
    }

    [Fact]
    public void Closure_mode_is_explicit_at_99_percent()
    {
        var snapshot = TestSnapshots.Healthy with { VerifiedCompletion = 99, CompletionMode = CompletionMode.ClosureMode };
        Assert.Equal(99, snapshot.VerifiedCompletion);
        Assert.Equal(CompletionMode.ClosureMode, snapshot.CompletionMode);
        Assert.NotEqual(CompletionMode.Verified, snapshot.CompletionMode);
    }

    [Fact]
    public void Attention_center_zero_state_is_no_action_required_when_runtime_is_healthy()
    {
        var snapshot = TestSnapshots.Healthy with { AttentionItems = Array.Empty<AttentionSummary>() };
        Assert.True(snapshot.NoActionRequired);
        Assert.Contains("NO ACTION REQUIRED", snapshot.OperatorMessage);
    }


    [Fact]
    public void Project_selection_targets_the_project_identity_and_is_not_a_dead_control()
    {
        var gateway = new FakeGateway(TestSnapshots.Healthy, (action, target) =>
            action == UiAction.SelectProject && target == "project-42");
        var vm = new MainViewModel(gateway);
        Assert.True(vm.SelectProjectCommand.CanExecute("project-42"));
        Assert.False(vm.SelectProjectCommand.CanExecute("other-project"));
    }

    [Fact]
    public void Attention_action_state_is_bound_to_the_exact_attention_identity()
    {
        var gateway = new FakeGateway(TestSnapshots.Healthy, (action, target) =>
            action == UiAction.OpenAttentionLocation && target == "attention-login");
        var vm = new MainViewModel(gateway);
        Assert.True(vm.OpenAttentionLocationCommand.CanExecute("attention-login"));
        Assert.False(vm.OpenAttentionLocationCommand.CanExecute("other"));
    }

    [Fact]
    public void Unbound_runtime_does_not_claim_zero_attention_or_zero_active_workers()
    {
        var snapshot = RuntimeSnapshot.Unbound;
        Assert.Equal("—", snapshot.AttentionCountText);
        Assert.Equal("—", snapshot.ActiveWorkersText);
        Assert.Contains("unavailable", snapshot.AttentionHeadline, StringComparison.OrdinalIgnoreCase);
    }


    [Fact]
    public void Unverified_worker_done_claim_is_not_presented_in_verified_done_column()
    {
        var unverified = new TaskSummary("t1", "Claimed done", "Done", "P1", "Worker 1", false);
        var verified = new TaskSummary("t2", "Verified done", "Done", "P1", "Worker 2", true);
        var vm = new MainViewModel(new FakeGateway(TestSnapshots.Healthy with { Tasks = new[] { unverified, verified } }));

        Assert.DoesNotContain(unverified, vm.DoneTasks);
        Assert.Contains(unverified, vm.TestingTasks);
        Assert.Contains(verified, vm.DoneTasks);
    }

    [Fact]
    public void Worker_chat_uses_the_owned_session_runtime_identity_not_the_worker_domain_id()
    {
        var worker = new WorkerSummary("logical-worker-domain-id", "Worker 1", "Backend", "Running", 50, "Task", HealthState.Healthy, null);
        var session = new SessionSummary("runtime-owned-9", "Worker 1", "Backend", "Active", SessionVisibility.Hidden, "Task", DateTimeOffset.UtcNow, true, 1234, HealthState.Healthy);
        var snapshot = TestSnapshots.Healthy with { Workers = new[] { worker }, Sessions = new[] { session } };
        var vm = new MainViewModel(new FakeGateway(snapshot));
        Assert.Equal("runtime-owned-9", vm.SelectedWorkerSession?.RuntimeId);
        Assert.NotEqual(worker.Id, vm.SelectedWorkerSession?.RuntimeId);
    }

    [Fact]
    public void Session_actions_are_disabled_when_gateway_cannot_prove_ownership()
    {
        var gateway = new FakeGateway(TestSnapshots.Healthy, (_, _) => false);
        var vm = new MainViewModel(gateway);
        Assert.False(vm.KillSessionCommand.CanExecute("runtime-1"));
        Assert.False(vm.KillAllPccSessionsCommand.CanExecute(null));
    }

    [Fact]
    public void Session_actions_bind_runtime_identity_when_gateway_allows_them()
    {
        var gateway = new FakeGateway(TestSnapshots.Healthy, (action, target) =>
            action == UiAction.OpenSession && target == "runtime-owned-1");
        var vm = new MainViewModel(gateway);
        Assert.True(vm.OpenSessionCommand.CanExecute("runtime-owned-1"));
        Assert.False(vm.OpenSessionCommand.CanExecute("unknown"));
    }
}

internal sealed class FakeGateway : IPccExecutivePresentationGateway
{
    private readonly Func<UiAction, string?, bool> _canExecute;
    public FakeGateway(RuntimeSnapshot snapshot, Func<UiAction, string?, bool>? canExecute = null)
    {
        Snapshot = snapshot;
        _canExecute = canExecute ?? ((_, _) => true);
    }

    public RuntimeSnapshot Snapshot { get; private set; }
    public event EventHandler<RuntimeSnapshot>? SnapshotChanged;
    public bool CanExecute(UiAction action, string? targetId = null) => _canExecute(action, targetId);
    public Task ExecuteAsync(UiAction action, string? targetId = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public void Publish(RuntimeSnapshot snapshot) { Snapshot = snapshot; SnapshotChanged?.Invoke(this, snapshot); }
}

internal static class TestSnapshots
{
    public static RuntimeSnapshot Healthy => RuntimeSnapshot.Unbound with
    {
        GatewayBound = true,
        RuntimeStatus = "Bound",
        GlobalHealth = HealthState.Healthy,
        AutopilotState = "AUTOPILOT",
        CompletionMode = CompletionMode.Running,
        ProviderMode = ProviderMode.BrowserWeb,
        AttentionItems = Array.Empty<AttentionSummary>()
    };
}
