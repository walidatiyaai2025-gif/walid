using PCCExecutive.App.Presentation;
using PCCExecutive.App.ViewModels;
using PCCExecutive.Application;
using Xunit;

namespace PCCExecutive.App.Tests;

public sealed class ChromeLiveReadinessRegressionTests
{
    [Fact]
    public void Failed_owned_manager_record_does_not_complete_guided_chrome_step()
    {
        var snapshot = CreateSnapshot(new SessionSummary(
            "manager-runtime",
            "Manager",
            "Manager",
            "FAILEDREQUIRESATTENTION",
            SessionVisibility.Hidden,
            "Not bound",
            DateTimeOffset.UtcNow,
            IsPccOwned: true,
            ProcessId: 1234,
            Health: HealthState.Unknown));

        var vm = new MainViewModel(new SnapshotGateway(snapshot));

        Assert.False(snapshot.ChromeConnectionProven);
        Assert.False(vm.GuidedExecution[GuidedStepId.Chrome].Satisfied);
        Assert.Equal(GuidedStepState.Current, vm.GuidedExecution[GuidedStepId.Chrome].State);
        Assert.Equal("CHROME_NOT_READY", vm.GuidedExecution[GuidedStepId.Chrome].ReasonCode);
    }

    [Fact]
    public void Runtime_inspector_keeps_next_action_on_chrome_when_only_stale_owned_record_exists()
    {
        var snapshot = CreateSnapshot(new SessionSummary(
            "manager-runtime",
            "Manager",
            "Manager",
            "FAILEDREQUIRESATTENTION",
            SessionVisibility.Hidden,
            "Not bound",
            DateTimeOffset.UtcNow,
            IsPccOwned: true,
            ProcessId: 1234,
            Health: HealthState.Unknown));

        var state = new SnapshotRuntimeInspectorStateSource(() => snapshot)
            .CaptureAsync().GetAwaiter().GetResult();

        Assert.Equal(GuidedStepId.Chrome, state.NextAction.Step);
        Assert.False(state.Prerequisites.Single(x => x.Step == GuidedStepId.Chrome).Satisfied);
    }

    [Fact]
    public void Owned_manager_without_process_identity_is_not_connection_proof()
    {
        var snapshot = CreateSnapshot(new SessionSummary(
            "manager-runtime",
            "Manager",
            "Manager",
            "READY",
            SessionVisibility.Hidden,
            "Not bound",
            DateTimeOffset.UtcNow,
            IsPccOwned: true,
            ProcessId: null,
            Health: HealthState.Unknown));

        Assert.False(snapshot.ChromeConnectionProven);
    }

    private static RuntimeSnapshot CreateSnapshot(SessionSummary session) => new(
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
        LatestManagerHandoff: "",
        CurrentExecutionFlow: "",
        ApiConfigured: false,
        ProviderMode: ProviderMode.BrowserWeb,
        DispatchSettings: DispatchSettingsSummary.ProductDefaults,
        Update: new UpdateSummary("0.1.0", null, "", "", "", "", false),
        Projects: Array.Empty<ProjectSummary>(),
        Sessions: [session],
        Workers: Array.Empty<WorkerSummary>(),
        Tasks: Array.Empty<TaskSummary>(),
        EvidenceGates: Array.Empty<EvidenceGateSummary>(),
        AttentionItems: Array.Empty<AttentionSummary>(),
        RecoveryEvents: Array.Empty<RecoveryEventSummary>());

    private sealed class SnapshotGateway(RuntimeSnapshot snapshot) : IPccExecutivePresentationGateway
    {
        public RuntimeSnapshot Snapshot { get; } = snapshot;
        public event EventHandler<RuntimeSnapshot>? SnapshotChanged;
        public bool CanExecute(UiAction action, string? targetId = null) => true;
        public Task ExecuteAsync(UiAction action, string? targetId = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
