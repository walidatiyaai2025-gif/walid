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
    public void Project_resolution_state_is_rendered_from_runtime_snapshot()
    {
        var snapshot = TestSnapshots.Healthy with { ProjectResolutionState = "ROUTING_NOT_READY", ProjectResolutionMessage = "Variant boundary is not routable." };
        var vm = new MainViewModel(new FakeGateway(snapshot));
        Assert.Equal("ROUTING_NOT_READY", vm.Snapshot.ProjectResolutionState);
        Assert.Contains("not routable", vm.Snapshot.ProjectResolutionMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Dashboard_maps_real_state_without_inventing_percentages()
    {
        var snapshot = TestSnapshots.Healthy with { Repository = "walidatiyaai2025-gif/walid", HeadSha = "abc123", PccSourceSha = "pcc456", VerifiedCompletion = null, ManagerEstimate = null };
        Assert.Equal("—", snapshot.VerifiedCompletionText);
        Assert.Equal("—", snapshot.ManagerEstimateText);
        Assert.Equal("abc123", snapshot.HeadSha);
        Assert.Equal("pcc456", snapshot.PccSourceSha);
    }

    [Fact]
    public void Provider_default_is_browser_web_and_api_requires_explicit_configuration()
    {
        var vm = new MainViewModel(new FakeGateway(TestSnapshots.Healthy with { ApiConfigured = false }));
        Assert.Equal(ProviderMode.BrowserWeb, vm.SelectedProviderMode);
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
    public void Closure_mode_is_explicit_at_99_percent_and_not_verified_complete()
    {
        var snapshot = TestSnapshots.Healthy with { VerifiedCompletion = 99, CompletionMode = CompletionMode.ClosureMode };
        Assert.True(snapshot.IsClosureMode);
        Assert.False(snapshot.IsVerifiedComplete);
    }

    [Fact]
    public void Only_100_percent_with_verified_mode_is_verified_complete()
    {
        var snapshot = TestSnapshots.Healthy with { VerifiedCompletion = 100, CompletionMode = CompletionMode.Verified };
        Assert.True(snapshot.IsVerifiedComplete);
    }

    [Fact]
    public void Worker_slot_selection_maps_logical_worker_to_runtime_identity()
    {
        var w1 = new WorkerSummary("logical-1", "Worker 1", "Backend", "Running", 50, "T1", HealthState.Ready, null);
        var w2 = new WorkerSummary("logical-2", "Worker 2", "QA", "Running", 25, "T2", HealthState.Ready, null);
        var s1 = new SessionSummary("runtime-1", "Worker 1", "Worker", "Active", SessionVisibility.Hidden, "T1", DateTimeOffset.UtcNow, true, 1, HealthState.Ready);
        var s2 = new SessionSummary("runtime-2", "Worker 2", "Worker", "Active", SessionVisibility.Hidden, "T2", DateTimeOffset.UtcNow, true, 2, HealthState.Ready);
        var vm = new MainViewModel(new FakeGateway(TestSnapshots.Healthy with { Workers = [w1, w2], Sessions = [s1, s2] }));
        vm.SelectWorkerCommand.Execute(w2);
        Assert.Equal("logical-2", vm.SelectedWorker?.Id);
        Assert.Equal("runtime-2", vm.SelectedWorkerSession?.RuntimeId);
    }

    [Fact]
    public void Session_kill_is_disabled_when_ownership_is_unknown()
    {
        var gateway = new FakeGateway(TestSnapshots.Healthy, (action, target) => action != UiAction.KillSession, (action, _) => action == UiAction.KillSession ? "positive PCC ownership is not proven" : null);
        var vm = new MainViewModel(gateway);
        Assert.False(vm.KillSessionCommand.CanExecute("runtime-unknown"));
    }

    [Fact]
    public void Global_pcc_kill_only_enabled_when_gateway_has_owned_session_evidence()
    {
        var denied = new MainViewModel(new FakeGateway(TestSnapshots.Healthy, (action, _) => action != UiAction.KillAllPccSessions));
        Assert.False(denied.KillAllPccSessionsCommand.CanExecute(null));
        var allowed = new MainViewModel(new FakeGateway(TestSnapshots.Healthy, (action, _) => action == UiAction.KillAllPccSessions));
        Assert.True(allowed.KillAllPccSessionsCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(HealthState.RateLimited, "RATE LIMIT DETECTED")]
    [InlineData(HealthState.PartialResponse, "PARTIAL RESPONSE")]
    [InlineData(HealthState.AdapterUncertain, "ADAPTER STATE UNCERTAIN")]
    [InlineData(HealthState.ContextLimitDetected, "CONTEXT LIMIT DETECTED")]
    public void Health_state_mapping_is_semantic_and_honest(HealthState health, string expected)
    {
        var snapshot = TestSnapshots.Healthy with { GlobalHealth = health };
        Assert.Contains(expected, snapshot.OperatorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Login_required_is_not_no_action_required_and_attention_can_be_explicit()
    {
        var attention = new AttentionSummary("login-1", "Login required", "Credentials require the operator", "Open Login", "runtime:r1", "USER_ACTION_REQUIRED");
        var snapshot = TestSnapshots.Healthy with { GlobalHealth = HealthState.LoginRequired, AttentionItems = [attention] };
        Assert.False(snapshot.NoActionRequired);
        Assert.Equal(1, snapshot.AttentionCount);
    }

    [Fact]
    public void Loop_guard_auto_stopped_state_is_preserved_verbatim()
    {
        var snapshot = TestSnapshots.Healthy with { LoopGuardState = "AUTO STOPPED · repeated task fingerprint" };
        Assert.Contains("AUTO STOPPED", snapshot.LoopGuardState);
    }

    [Fact]
    public void Settings_editing_is_disabled_when_durable_persistence_is_not_bound()
    {
        var gateway = new FakeGateway(TestSnapshots.Healthy, (action, _) => action != UiAction.SaveSettings, (action, _) => action == UiAction.SaveSettings ? "Worker 2 durable settings persistence is not integrated." : null);
        var vm = new MainViewModel(gateway);
        Assert.False(vm.CanEditSettings);
        Assert.Contains("Worker 2", vm.SaveSettingsDisabledReason);
    }

    [Fact]
    public void Update_state_does_not_fabricate_available_or_install_ready()
    {
        var update = new UpdateSummary("0.1.0", null, "Not checked", "Not started", "Not started", "Not available", false) { State = "SOURCE_NOT_CONFIGURED", DisabledReason = "No verified update source is configured." };
        var snapshot = TestSnapshots.Healthy with { Update = update };
        Assert.Null(snapshot.Update.NewVersion);
        Assert.False(snapshot.Update.InstallReady);
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
    public void Unverified_worker_done_claim_is_not_presented_as_verified_done()
    {
        var unverified = new TaskSummary("t1", "Claimed done", "Done", "P1", "Worker 1", false);
        var verified = new TaskSummary("t2", "Verified done", "Done", "P1", "Worker 2", true);
        var vm = new MainViewModel(new FakeGateway(TestSnapshots.Healthy with { Tasks = [unverified, verified] }));
        Assert.DoesNotContain(unverified, vm.DoneTasks);
        Assert.Contains(unverified, vm.TestingTasks);
        Assert.Contains(verified, vm.DoneTasks);
    }

    [Fact]
    public void Task_filters_are_local_read_side_filters_only()
    {
        var a = new TaskSummary("a", "A", "Running", "P0", "Worker 1", false) { Wave = "W1", Blocker = "none" };
        var b = new TaskSummary("b", "B", "Running", "P1", "Worker 2", false) { Wave = "W2", Blocker = "external" };
        var vm = new MainViewModel(new FakeGateway(TestSnapshots.Healthy with { Tasks = [a, b] }));
        vm.TaskWorkerFilter = "Worker 2";
        Assert.Single(vm.InProgressTasks);
        Assert.Equal("b", vm.InProgressTasks.Single().Id);
    }

    [Fact]
    public void Unsupported_control_is_disabled_with_reason()
    {
        var binding = new UnavailableRuntimeBinding("dependency unavailable");
        Assert.False(binding.CanExecute(UiAction.StartDispatch));
        Assert.Contains("dependency unavailable", binding.DisabledReason(UiAction.StartDispatch));
    }

    [Fact]
    public void Control_census_has_zero_unclassified_p0_controls()
    {
        Assert.Empty(ControlCensus.UnresolvedP0);
        Assert.All(ControlCensus.All.Where(x => x.P0), x => Assert.True(x.CurrentStatus is ControlClassification.WiredReal or ControlClassification.DisabledWithReason));
    }

    [Fact]
    public async Task Operational_gateway_reconciles_snapshot_after_real_command()
    {
        var binding = new FakeRuntimeBinding(TestSnapshots.Healthy);
        var gateway = new OperationalPresentationGateway(binding);
        await gateway.ExecuteAsync(UiAction.Refresh);
        Assert.Equal(1, binding.RefreshCount);
        Assert.Equal("refresh-1", gateway.Snapshot.RuntimeStatus);
    }

    [Fact]
    public async Task Operational_gateway_blocks_duplicate_invocation()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var binding = new FakeRuntimeBinding(TestSnapshots.Healthy) { BlockExecute = gate.Task };
        var gateway = new OperationalPresentationGateway(binding);
        var first = gateway.ExecuteAsync(UiAction.RetryHealth);
        await binding.CommandEntered.Task;
        Assert.False(gateway.CanExecute(UiAction.RetryHealth));
        await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.ExecuteAsync(UiAction.RetryHealth));
        gate.SetResult();
        await first;
    }

    [Fact]
    public void Async_command_blocks_double_click_while_running()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var command = new AsyncRelayCommand(async (_, _) => await tcs.Task);
        command.Execute(null);
        Assert.True(command.IsRunning);
        Assert.False(command.CanExecute(null));
        command.Execute(null);
        tcs.SetResult();
    }
}

internal sealed class FakeGateway : IPccExecutivePresentationGateway
{
    private readonly Func<UiAction, string?, bool> _canExecute;
    private readonly Func<UiAction, string?, string?> _disabledReason;
    public FakeGateway(RuntimeSnapshot snapshot, Func<UiAction, string?, bool>? canExecute = null, Func<UiAction, string?, string?>? disabledReason = null)
    {
        Snapshot = snapshot;
        _canExecute = canExecute ?? ((_, _) => true);
        _disabledReason = disabledReason ?? ((_, _) => null);
    }
    public RuntimeSnapshot Snapshot { get; private set; }
    public event EventHandler<RuntimeSnapshot>? SnapshotChanged;
    public bool CanExecute(UiAction action, string? targetId = null) => _canExecute(action, targetId);
    public string? DisabledReason(UiAction action, string? targetId = null) => _disabledReason(action, targetId);
    public Task ExecuteAsync(UiAction action, string? targetId = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public void Publish(RuntimeSnapshot snapshot) { Snapshot = snapshot; SnapshotChanged?.Invoke(this, snapshot); }
}

internal sealed class FakeRuntimeBinding : IRuntimeBinding
{
    private RuntimeSnapshot _snapshot;
    public FakeRuntimeBinding(RuntimeSnapshot snapshot) => _snapshot = snapshot;
    public int RefreshCount { get; private set; }
    public Task? BlockExecute { get; init; }
    public TaskCompletionSource CommandEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public RuntimeSnapshot Current => _snapshot;
    public Task<RuntimeSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        RefreshCount++;
        _snapshot = _snapshot with { RuntimeStatus = $"refresh-{RefreshCount}" };
        return Task.FromResult(_snapshot);
    }
    public bool CanExecute(UiAction action, string? targetId = null) => true;
    public string? DisabledReason(UiAction action, string? targetId = null) => null;
    public async Task ExecuteAsync(UiAction action, string? targetId = null, CancellationToken cancellationToken = default)
    {
        CommandEntered.TrySetResult();
        if (BlockExecute is not null) await BlockExecute;
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
