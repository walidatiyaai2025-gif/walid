using PCCExecutive.App.Presentation;
using PCCExecutive.App.ViewModels;
using Xunit;
using PCCExecutive.Application;

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
    public async Task Every_navigation_attempt_is_visible_in_runtime_inspector()
    {
        var memory = new InMemoryRuntimeDiagnosticStore();
        var collector = new RuntimeDiagnosticCollector(memory, memory);
        var state = new TestInspectorStateSource();
        var services = new RuntimeInspectorServices(collector, state, (_, _, _, _) => Task.FromResult("{}"));
        var vm = new MainViewModel(new FakeGateway(TestSnapshots.Healthy), runtimeInspector: services);

        vm.Navigate(ScreenId.RuntimeInspector);

        var events = await collector.ReadRecentAsync(10);
        var navigation = Assert.Single(events, x => x.Event.Kind == RuntimeDiagnosticKind.Navigation);
        Assert.Equal("RuntimeInspector", navigation.Event.Target);
        Assert.True(navigation.Event.Allowed);
        Assert.IsType<RuntimeInspectorViewModel>(vm.CurrentScreen);
    }

    [Fact]
    public void Destructive_session_action_requires_confirmation()
    {
        var gateway = new FakeGateway(TestSnapshots.Healthy);
        var confirmations = new RecordingConfirmationService(false);
        var vm = new MainViewModel(gateway, confirmations);

        vm.KillSessionCommand.Execute("session-1");

        Assert.Equal(1, confirmations.CallCount);
        Assert.Empty(gateway.Executions);
    }

    [Fact]
    public void Provider_defaults_to_browser_web()
    {
        var vm = new MainViewModel(new FakeGateway(TestSnapshots.Healthy));
        Assert.Equal(ProviderMode.BrowserWeb, vm.SelectedProviderMode);
        Assert.True(vm.IsBrowserProviderSelected);
    }

    [Fact]
    public void Api_provider_cannot_be_selected_until_configured()
    {
        var vm = new MainViewModel(new FakeGateway(TestSnapshots.Healthy with { ApiConfigured = false }));
        vm.SelectedProviderMode = ProviderMode.OpenAiApi;
        Assert.Equal(ProviderMode.BrowserWeb, vm.SelectedProviderMode);
        Assert.True(vm.HasUiError);
    }

    [Fact]
    public void Chrome_connection_is_initial_screen_without_active_run()
    {
        var vm = new MainViewModel(new FakeGateway(TestSnapshots.Healthy with { HasActiveRun = false }));
        Assert.Equal(ScreenId.ChromeConnection, vm.SelectedScreen);
        Assert.Equal(ScreenId.ChromeConnection, vm.Navigation[0].Id);
        Assert.Equal("01  Chrome", vm.Navigation[0].Label);
    }

    [Fact]
    public void Active_run_with_unproven_live_Chrome_reconciles_to_Chrome_instead_of_inventing_completion()
    {
        var vm = new MainViewModel(new FakeGateway(TestSnapshots.Healthy with { HasActiveRun = true }));
        Assert.Equal(ScreenId.ChromeConnection, vm.SelectedScreen);
        Assert.NotEqual(GuidedStepState.Completed, vm.Navigation[0].State);
    }

    [Fact]
    public void Todo_task_partition_uses_runtime_state()
    {
        var snapshot = TestSnapshots.Healthy with
        {
            Tasks =
            [
                new TaskSummary("1", "todo", "To Do", "P0", null, false),
                new TaskSummary("2", "running", "Running", "P0", "Worker 1", false),
                new TaskSummary("3", "done", "Done", "P1", "Worker 2", true)
            ]
        };
        var vm = new MainViewModel(new FakeGateway(snapshot));
        Assert.Single(vm.TodoTasks);
        Assert.Single(vm.InProgressTasks);
        Assert.Single(vm.DoneTasks);
    }

    [Fact]
    public void Unverified_done_task_remains_testing_not_done()
    {
        var snapshot = TestSnapshots.Healthy with
        {
            Tasks = [new TaskSummary("1", "candidate", "Done", "P0", "Worker 1", false)]
        };
        var vm = new MainViewModel(new FakeGateway(snapshot));
        Assert.Single(vm.TestingTasks);
        Assert.Empty(vm.DoneTasks);
    }

    [Fact]
    public void Pcc_owned_session_count_excludes_unproven_sessions()
    {
        var snapshot = TestSnapshots.Healthy with
        {
            Sessions =
            [
                new SessionSummary("1", "Manager", "Manager", "READY", SessionVisibility.Hidden, "conv", DateTimeOffset.UtcNow, true, 10, HealthState.Unknown),
                new SessionSummary("2", "Worker 1", "Worker", "READY", SessionVisibility.Hidden, "conv", DateTimeOffset.UtcNow, false, 11, HealthState.Unknown)
            ]
        };
        var vm = new MainViewModel(new FakeGateway(snapshot));
        Assert.Equal(1, vm.PccOwnedSessionCount);
    }

    [Fact]
    public void Snapshot_change_refreshes_runtime_projection()
    {
        var gateway = new FakeGateway(TestSnapshots.Healthy);
        var vm = new MainViewModel(gateway);
        var changed = TestSnapshots.Healthy with { AutopilotState = "PAUSED", ActiveWorkers = 3 };

        gateway.Push(changed);

        Assert.Equal("PAUSED", vm.Snapshot.AutopilotState);
        Assert.Equal(3, vm.Snapshot.ActiveWorkers);
    }

    [Fact]
    public void Unbound_snapshot_does_not_report_zero_attention_as_healthy()
    {
        var vm = new MainViewModel(new FakeGateway(RuntimeSnapshot.Unbound));
        Assert.False(vm.Snapshot.NoActionRequired);
        Assert.Equal("— unavailable", vm.Snapshot.AttentionSummaryText);
    }

    [Fact]
    public void Browser_provider_selection_is_preserved_from_runtime_snapshot()
    {
        var gateway = new FakeGateway(TestSnapshots.Healthy with { ProviderMode = ProviderMode.BrowserWeb });
        var vm = new MainViewModel(gateway);
        Assert.Equal(ProviderMode.BrowserWeb, vm.SelectedProviderMode);
    }

    [Fact]
    public void Wrong_path_Manager_navigation_is_blocked_with_exact_redirect()
    {
        var vm = new MainViewModel(new FakeGateway(TestSnapshots.Healthy with { HasActiveRun = false, GlobalHealth = HealthState.Healthy }));
        var before = vm.SelectedScreen;

        vm.Navigate(ScreenId.ManagerWorkspace);

        Assert.Equal(before, vm.SelectedScreen);
        Assert.True(vm.HasBlockedNavigation);
        Assert.Contains("04 Manager", vm.BlockedActionTitle);
        Assert.Contains("02 Project", vm.BlockedActionDetail);
        Assert.Contains("Open Project", vm.BlockedActionDetail);
    }

    [Fact]
    public void Go_to_required_step_navigates_to_safe_exact_screen()
    {
        var vm = new MainViewModel(new FakeGateway(TestSnapshots.Healthy with { HasActiveRun = true, GlobalHealth = HealthState.Healthy, Sessions = [] }));
        vm.Navigate(ScreenId.WorkersDispatch);

        vm.GoToRequiredStepCommand.Execute(null);

        Assert.Equal(ScreenId.ManagerWorkspace, vm.SelectedScreen);
        Assert.False(vm.HasBlockedNavigation);
    }

    [Fact]
    public void Command_guard_uses_same_Manager_prerequisite_as_navigation()
    {
        var vm = new MainViewModel(new FakeGateway(TestSnapshots.Healthy with { HasActiveRun = true, GlobalHealth = HealthState.Healthy, Sessions = [] }));

        Assert.True(vm.StartManagerCommand.CanExecute(null));
        Assert.False(vm.StartDispatchCommand.CanExecute(null));
    }

    [Fact]
    public void Navigation_projection_has_non_color_semantic_cues_and_canonical_banner()
    {
        var vm = new MainViewModel(new FakeGateway(TestSnapshots.Healthy with { HasActiveRun = false, GlobalHealth = HealthState.Healthy }));
        var chrome = vm.Navigation.Single(x => x.Id == ScreenId.ChromeConnection);
        var project = vm.Navigation.Single(x => x.Id == ScreenId.ProjectSelection);

        Assert.Equal("COMPLETED", chrome.StatusText);
        Assert.Equal("✓", chrome.StatusGlyph);
        Assert.Equal("CURRENT", project.StatusText);
        Assert.Equal("▶", project.StatusGlyph);
        Assert.Contains("02 Project", vm.NextAction.Instruction);
    }

    [Fact]
    public void Recovery_atomically_updates_step_and_NextAction_without_contradiction()
    {
        var gateway = new FakeGateway(TestSnapshots.Healthy with { GlobalHealth = HealthState.Healthy });
        var vm = new MainViewModel(gateway);

        gateway.Push(TestSnapshots.Healthy with { GlobalHealth = HealthState.Recovering });

        var chrome = vm.Navigation.Single(x => x.Id == ScreenId.ChromeConnection);
        Assert.Equal(GuidedStepState.Recovering, chrome.State);
        Assert.Equal(GuidedActionKind.Automatic, vm.NextAction.Kind);
        Assert.Equal(GuidedStepId.Chrome, vm.NextAction.Step);
    }

    [Fact]
    public void Read_only_diagnostic_screens_remain_reachable_when_execution_is_blocked()
    {
        var vm = new MainViewModel(new FakeGateway(RuntimeSnapshot.Unbound));
        vm.Navigate(ScreenId.SessionMonitor);
        Assert.Equal(ScreenId.SessionMonitor, vm.SelectedScreen);
        vm.Navigate(ScreenId.AttentionCenter);
        Assert.Equal(ScreenId.AttentionCenter, vm.SelectedScreen);
    }

    [Fact]
    public void Blocked_navigation_emits_structured_diagnostic_event()
    {
        var vm = new MainViewModel(new FakeGateway(TestSnapshots.Healthy with { HasActiveRun = false, GlobalHealth = HealthState.Healthy }));
        RuntimeDiagnosticEvent? emitted = null;
        vm.RuntimeDiagnosticEmitted += (_, e) => emitted = e;

        vm.Navigate(ScreenId.ManagerWorkspace);

        Assert.NotNull(emitted);
        Assert.Equal(RuntimeDiagnosticKind.GuardDecision, emitted.Kind);
        Assert.False(emitted.Allowed);
        Assert.Equal("ManagerWorkspace", emitted.Target);
    }

    private sealed class RecordingConfirmationService(bool result) : Services.IConfirmationService
    {
        public int CallCount { get; private set; }
        public bool Confirm(string title, string message, string confirmLabel)
        {
            CallCount++;
            return result;
        }
    }

    private sealed class FakeGateway : IPccExecutivePresentationGateway
    {
        private RuntimeSnapshot _snapshot;
        public FakeGateway(RuntimeSnapshot snapshot) => _snapshot = snapshot;
        public RuntimeSnapshot Snapshot => _snapshot;
        public event EventHandler<RuntimeSnapshot>? SnapshotChanged;
        public List<(UiAction Action, string? Target)> Executions { get; } = [];
        public bool CanExecute(UiAction action, string? targetId = null) => true;
        public Task ExecuteAsync(UiAction action, string? targetId = null, CancellationToken cancellationToken = default)
        {
            Executions.Add((action, targetId));
            return Task.CompletedTask;
        }
        public void Push(RuntimeSnapshot snapshot)
        {
            _snapshot = snapshot;
            SnapshotChanged?.Invoke(this, snapshot);
        }
    }

    private sealed class TestInspectorStateSource : IRuntimeInspectorStateSource
    {
        public Task<RuntimeInspectorState> CaptureAsync(CancellationToken cancellationToken = default) => Task.FromResult(new RuntimeInspectorState(
            null, "BrowserWeb", "Unknown", "Not ready", "0 active", 0, "READY", null, [], []));
    }

    private static class TestSnapshots
    {
        public static RuntimeSnapshot Healthy => new(
            GatewayBound: true,
            HasActiveRun: true,
            RuntimeStatus: "Integrated runtime",
            GlobalHealth: HealthState.Unknown,
            AutopilotState: "READY",
            CurrentWave: "Manager planning",
            VerifiedCompletion: 0,
            ManagerEstimate: 0,
            CompletionMode: CompletionMode.Running,
            ActiveWorkers: 0,
            P0Count: 0,
            P1Count: 0,
            BlockerCount: 0,
            LoopGuardState: "NORMAL",
            LatestManagerHandoff: "Awaiting",
            CurrentExecutionFlow: "Project → Manager",
            ApiConfigured: false,
            ProviderMode: ProviderMode.BrowserWeb,
            DispatchSettings: DispatchSettingsSummary.ProductDefaults,
            Update: new UpdateSummary("0.1.0", null, "ready", "ready", "ready", "ready", false),
            Projects: [],
            Sessions: [],
            Workers: [],
            Tasks: [],
            EvidenceGates: [],
            AttentionItems: [],
            RecoveryEvents: []);
    }
}
