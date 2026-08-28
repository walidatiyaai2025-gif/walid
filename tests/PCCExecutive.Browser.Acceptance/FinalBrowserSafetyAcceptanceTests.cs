using PCCExecutive.Browser;
using Xunit;

namespace PCCExecutive.Browser.Acceptance;

public sealed class FinalBrowserSafetyAcceptanceTests
{
    [Fact]
    public async Task Manager_and_worker_slots_keep_exact_runtime_dispatch_correlation()
    {
        var harness = new ControlledBrowserAcceptanceHarness();
        await harness.CreateTopologyAsync(5);

        Assert.Null(harness.Manager.WorkerSlotId);

        for (var slot = 1; slot <= 5; slot++)
        {
            var runtime = harness.Worker(slot);
            var request = harness.Request(runtime, runtime.TaskId!, $"dispatch-correlation-{slot}", $"correlation {slot}") with
            {
                ContentHash = $"HASH-{slot}"
            };
            harness.Adapter.SetSnapshot(runtime.RuntimeId, AcceptanceSnapshots.Healthy());
            harness.Adapter.SetSubmission(runtime.RuntimeId, new(true, true, false, "SUBMISSION_PROVEN", ["correlation:submitted"]));
            var provider = new BrowserChatProvider(harness.Registry, harness.Adapter, harness.Ledger, new WrongChatGuard(), harness.GlobalGate, harness.Ownership);

            var result = await provider.SendAsync(runtime.RuntimeId, request);
            var dispatch = await harness.Ledger.GetAsync(request.DispatchId);

            Assert.Equal(slot.ToString(System.Globalization.CultureInfo.InvariantCulture), runtime.WorkerSlotId);
            Assert.Equal(runtime.ProjectRunId, request.ProjectRunId);
            Assert.Equal(runtime.LogicalAgentId, request.LogicalAgentId);
            Assert.Equal(runtime.WorkerSlotId, request.WorkerSlotId);
            Assert.Equal(runtime.TaskId, request.TaskId);
            Assert.Equal(runtime.ConversationIdentity, request.ConversationIdentity);
            Assert.Equal(runtime.ProviderConversationIdentity, request.ProviderConversationIdentity);
            Assert.Equal(request.ContentHash, dispatch!.ContentHash);
            Assert.Equal(BrowserDispatchOutcome.Submitted, result.Outcome);
        }
    }

    [Fact]
    public async Task Submitted_unknown_restart_reconciles_same_dispatch_before_any_retry()
    {
        var harness = new ControlledBrowserAcceptanceHarness();
        await harness.CreateTopologyAsync(1);
        var worker = await harness.BindTaskAsync(new AcceptanceTask("task-restart-unknown", 1, "restart unknown", "scope"));
        harness.Adapter.SetSnapshot(worker.RuntimeId, AcceptanceSnapshots.Healthy());
        harness.Adapter.SetSubmission(worker.RuntimeId, new(true, false, true, "SUBMITTED_UNKNOWN", ["enter-triggered", "ack-unproven"]));
        var firstProvider = new BrowserChatProvider(harness.Registry, harness.Adapter, harness.Ledger, new WrongChatGuard(), harness.GlobalGate, harness.Ownership);
        var request = harness.Request(worker, "task-restart-unknown", "dispatch-restart-unknown", "restart unknown");

        var first = await firstProvider.SendAsync(worker.RuntimeId, request);
        var original = await harness.Ledger.GetAsync(request.DispatchId);
        Assert.Equal(BrowserDispatchOutcome.SubmittedUnknown, first.Outcome);
        Assert.NotNull(original);

        var restoredLedger = new InMemoryDispatchLedger();
        await restoredLedger.ReserveAsync(original!.DispatchId, original.ContentHash);
        await restoredLedger.UpdateAsync(original.DispatchId, DispatchState.SubmittedUnknown, original.ReconciliationEvidence);
        var restartAdapter = new DeterministicChatGptAdapter();
        restartAdapter.SetSnapshot(worker.RuntimeId, AcceptanceSnapshots.Healthy());
        restartAdapter.SetSubmission(worker.RuntimeId, new(true, true, false, "SHOULD_NOT_RUN_BEFORE_RECONCILIATION", ["duplicate"]));
        var restartProvider = new BrowserChatProvider(harness.Registry, restartAdapter, restoredLedger, new WrongChatGuard(), harness.GlobalGate, harness.Ownership);
        var probe = new MutableProbe(new ConversationDispatchEvidence(null, false, false, .50, ["ambiguous-after-restart"]));
        var coordinator = new DispatchReconciliationCoordinator(new UncertainSendReconciler(probe), restoredLedger);

        var unresolvedEntry = await restoredLedger.GetAsync(request.DispatchId);
        var unresolved = await coordinator.ReconcileAsync(worker.RuntimeId, unresolvedEntry!);
        var duplicate = await restartProvider.SendAsync(worker.RuntimeId, request);

        Assert.Equal(RetrySafety.NotSafe, unresolved.RetrySafety);
        Assert.Equal(DispatchState.SubmittedUnknown, (await restoredLedger.GetAsync(request.DispatchId))!.State);
        Assert.Equal(BrowserDispatchOutcome.DuplicateBlocked, duplicate.Outcome);
        Assert.False(restartAdapter.SubmitCounts.ContainsKey(worker.RuntimeId));

        probe.Evidence = new ConversationDispatchEvidence(false, false, false, .99, ["message-absence-positively-proven"]);
        var safeEntry = await restoredLedger.GetAsync(request.DispatchId);
        var safe = await coordinator.ReconcileAsync(worker.RuntimeId, safeEntry!);
        restartAdapter.SetSubmission(worker.RuntimeId, new(true, true, false, "SUBMISSION_PROVEN", ["safe-retry-after-absence-proof"]));
        var retry = await restartProvider.SendAsync(worker.RuntimeId, request);

        Assert.Equal(RetrySafety.SafeRetry, safe.RetrySafety);
        Assert.Equal(BrowserDispatchOutcome.Submitted, retry.Outcome);
        Assert.Equal(1, restartAdapter.SubmitCounts[worker.RuntimeId]);
    }

    [Fact]
    public async Task Worker_one_slot_expectation_against_worker_two_slot_fails_closed_without_submit()
    {
        var harness = new ControlledBrowserAcceptanceHarness();
        await harness.CreateTopologyAsync(2);
        var worker2 = await harness.BindTaskAsync(new AcceptanceTask("task-slot-cross", 2, "cross slot", "scope"));
        harness.Adapter.SetSnapshot(worker2.RuntimeId, AcceptanceSnapshots.Healthy());
        harness.Adapter.SetSubmission(worker2.RuntimeId, new(true, true, false, "SHOULD_NOT_RUN", ["cross-slot"]));
        var request = harness.Request(worker2, "task-slot-cross", "dispatch-slot-cross", "cross slot") with { WorkerSlotId = "1" };
        var provider = new BrowserChatProvider(harness.Registry, harness.Adapter, harness.Ledger, new WrongChatGuard(), harness.GlobalGate, harness.Ownership);

        var result = await provider.SendAsync(worker2.RuntimeId, request);

        Assert.Equal(BrowserDispatchOutcome.NotSent, result.Outcome);
        Assert.Equal("WORKER_SLOT_MISMATCH", result.Reason);
        Assert.False(harness.Adapter.SubmitCounts.ContainsKey(worker2.RuntimeId));
    }

    private sealed class MutableProbe(ConversationDispatchEvidence evidence) : IConversationEvidenceProbe
    {
        public ConversationDispatchEvidence Evidence { get; set; } = evidence;

        public Task<ConversationDispatchEvidence> InspectDispatchAsync(string runtimeId, string dispatchId, string contentHash, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Evidence);
        }
    }
}
