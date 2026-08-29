using PCCExecutive.App.Presentation;
using PCCExecutive.App.ViewModels;
using Xunit;

namespace PCCExecutive.App.Tests;

public sealed class ManagerPlanningProjectionTests
{
    [Fact]
    public void Manager_step_stays_current_while_pcc_is_reading_and_validating_chatgpt_response()
    {
        var gateway = new FakeGateway(Snapshot("PLANNING", "Manager request submitted. Waiting for a complete structured response."));
        var vm = new MainViewModel(gateway);

        var manager = vm.Navigation.Single(x => x.Id == ScreenId.ManagerWorkspace);
        Assert.Equal(GuidedStepState.Current, manager.State);
        Assert.NotEqual(GuidedStepState.Completed, manager.State);
    }

    [Fact]
    public void Manager_step_completes_only_after_structured_plan_validation()
    {
        var gateway = new FakeGateway(Snapshot("PLANNING", "Reading Manager response."));
        var vm = new MainViewModel(gateway);

        gateway.Push(Snapshot("READY_TO_DISPATCH", "Validated Wave: structured Manager plan accepted."));

        var manager = vm.Navigation.Single(x => x.Id == ScreenId.ManagerWorkspace);
        Assert.Equal(GuidedStepState.Completed, manager.State);
    }

    private static RuntimeSnapshot Snapshot(string autopilot, string handoff) => new(
        GatewayBound: true,
        HasActiveRun: true,
        RuntimeStatus: "Integrated runtime",
        GlobalHealth: HealthState.Unknown,
        AutopilotState: autopilot,
        CurrentWave: "Manager planning",
        VerifiedCompletion: 0,
        ManagerEstimate: 0,
        CompletionMode: CompletionMode.Running,
        ActiveWorkers: 0,
        P0Count: 0,
        P1Count: 0,
        BlockerCount: 0,
        LoopGuardState: "NORMAL",
        LatestManagerHandoff: handoff,
        CurrentExecutionFlow: "Project → Manager plan → validate → staged Workers",
        ApiConfigured: false,
        ProviderMode: ProviderMode.BrowserWeb,
        DispatchSettings: DispatchSettingsSummary.ProductDefaults,
        Update: new UpdateSummary("0.1.0", null, "ready", "ready", "ready", "ready", false),
        Projects: [],
        Sessions:
        [
            new SessionSummary("manager-runtime", "Manager", "Manager", "VISIBLE", SessionVisibility.Visible,
                "manager-conversation", DateTimeOffset.UtcNow, true, 1234, HealthState.Unknown)
        ],
        Workers: [],
        Tasks: [],
        EvidenceGates: [],
        AttentionItems: [],
        RecoveryEvents: []);

    private sealed class FakeGateway : IPccExecutivePresentationGateway
    {
        private RuntimeSnapshot _snapshot;
        public FakeGateway(RuntimeSnapshot snapshot) => _snapshot = snapshot;
        public RuntimeSnapshot Snapshot => _snapshot;
        public event EventHandler<RuntimeSnapshot>? SnapshotChanged;
        public bool CanExecute(UiAction action, string? targetId = null) => true;
        public Task ExecuteAsync(UiAction action, string? targetId = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Push(RuntimeSnapshot snapshot)
        {
            _snapshot = snapshot;
            SnapshotChanged?.Invoke(this, snapshot);
        }
    }
}
