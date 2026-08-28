using PCCExecutive.Browser;
using Xunit;

namespace PCCExecutive.Browser.Tests;

public sealed class ResponseRecoveryTests
{
    [Fact]
    public async Task Partial_response_capture_preserves_text_and_dispatch_identity()
    {
        var port = new CapturePort();
        var coordinator = new PartialResponseRecoveryCoordinator(port);
        var request = new BrowserDispatchRequest("dispatch-9", "run", "agent", "task-9", "conversation-9", "provider-9", "prompt");
        var snapshot = Snapshot(ResponseCompleteness.Partial, "captured visible response");
        var result = await coordinator.CaptureAsync(request, snapshot);

        Assert.True(result.Captured);
        Assert.False(result.MayReportDone);
        Assert.NotNull(port.Saved);
        Assert.Equal("dispatch-9", port.Saved!.DispatchId);
        Assert.Equal("task-9", port.Saved.TaskId);
        Assert.Equal("captured visible response", port.Saved.CapturedText);
        Assert.Contains("DISPATCH_ID=dispatch-9", result.ContinuationInstruction);
    }

    [Fact]
    public async Task Dispatch_reconciliation_marks_safe_retry_only_after_proven_absence()
    {
        var ledger = new InMemoryDispatchLedger();
        await ledger.ReserveAsync("d-safe", "hash");
        await ledger.UpdateAsync("d-safe", DispatchState.SubmittedUnknown);
        var existing = await ledger.GetAsync("d-safe");
        var reconciler = new UncertainSendReconciler(new Probe(new ConversationDispatchEvidence(false, false, false, .99, new[] { "message-absent" })));
        var coordinator = new DispatchReconciliationCoordinator(reconciler, ledger);
        var result = await coordinator.ReconcileAsync("runtime", existing!);
        var updated = await ledger.GetAsync("d-safe");
        Assert.Equal(RetrySafety.SafeRetry, result.RetrySafety);
        Assert.Equal(DispatchState.SafeRetry, updated!.State);
    }

    [Fact]
    public async Task Dispatch_reconciliation_keeps_cannot_determine_in_submitted_unknown()
    {
        var ledger = new InMemoryDispatchLedger();
        await ledger.ReserveAsync("d-unknown", "hash");
        await ledger.UpdateAsync("d-unknown", DispatchState.SubmittedUnknown);
        var existing = await ledger.GetAsync("d-unknown");
        var reconciler = new UncertainSendReconciler(new Probe(new ConversationDispatchEvidence(null, false, false, .40, new[] { "ambiguous" })));
        var coordinator = new DispatchReconciliationCoordinator(reconciler, ledger);
        await coordinator.ReconcileAsync("runtime", existing!);
        var updated = await ledger.GetAsync("d-unknown");
        Assert.Equal(DispatchState.SubmittedUnknown, updated!.State);
    }

    [Fact]
    public async Task Recovery_ladder_coordinator_records_reason_and_evidence()
    {
        var port = new RecoveryEvidencePort();
        var coordinator = new RecoveryLadderCoordinator(port);
        var step = await coordinator.DecideAndRecordAsync("runtime", new RecoveryAttemptContext(3, RuntimeResilienceState.Stuck, false, true, true, true), new[] { "stuck:no-progress" });
        Assert.Equal(RecoveryAction.RestoreConversation, step.Action);
        Assert.NotNull(port.Saved);
        Assert.Equal(3, port.Saved!.Level);
        Assert.Contains("stuck:no-progress", port.Saved.Evidence);
    }

    [Fact]
    public void Global_resume_waits_for_cooldown_and_restores_gradually()
    {
        var gate = new GlobalBrowserSendGate();
        var now = DateTimeOffset.UtcNow;
        gate.Apply(new ResilienceDecision(ChatGptResilienceState.RateLimited, FaultScope.Global, true, false, "RATE_LIMITED"), now, TimeSpan.FromSeconds(30));
        var coordinator = new GlobalRateLimitRecoveryCoordinator(gate);
        var healthy = new[] { new RuntimeTransitionDecision(RuntimeResilienceState.Recovering, RuntimeResilienceState.Ready, FaultScope.None, false, false, false, RecoveryAction.None, null, "READY", Array.Empty<string>()) };

        var early = coordinator.Reevaluate(now.AddSeconds(10), healthy, new AdaptivePacingState(TimeSpan.FromSeconds(40)));
        Assert.False(early.MayResumeNewSends);
        var resumed = coordinator.Reevaluate(now.AddSeconds(31), healthy, new AdaptivePacingState(TimeSpan.FromSeconds(40)));
        Assert.True(resumed.MayResumeNewSends);
        Assert.True(resumed.GateResumed);
        Assert.True(resumed.SuggestedInterval >= TimeSpan.FromSeconds(10));
    }

    private static ChatGptSemanticSnapshot Snapshot(ResponseCompleteness completeness, string? response) => new(
        SemanticDetection<InputState>.Create(InputState.Ready, .9, "test", "input"),
        SemanticDetection<GenerationState>.Create(GenerationState.Complete, .9, "test", "generation"),
        SemanticDetection<AuthState>.Create(AuthState.Authenticated, .9, "test", "auth"),
        SemanticDetection<ConversationMatch>.Create(ConversationMatch.Match, .9, "test", "conversation"),
        SemanticDetection<PageHealth>.Create(PageHealth.Healthy, .9, "test", "health"),
        completeness, 1, response, DateTimeOffset.UtcNow, "test");

    private sealed class CapturePort : IPartialResponseCapturePort
    {
        public PartialResponseCapture? Saved { get; private set; }
        public Task SaveAsync(PartialResponseCapture capture, CancellationToken cancellationToken = default) { Saved = capture; return Task.CompletedTask; }
    }

    private sealed class Probe(ConversationDispatchEvidence evidence) : IConversationEvidenceProbe
    {
        public Task<ConversationDispatchEvidence> InspectDispatchAsync(string runtimeId, string dispatchId, string contentHash, CancellationToken cancellationToken = default) => Task.FromResult(evidence);
    }

    private sealed class RecoveryEvidencePort : IRecoveryEvidencePort
    {
        public RecoveryEvidence? Saved { get; private set; }
        public Task RecordAsync(RecoveryEvidence evidence, CancellationToken cancellationToken = default) { Saved = evidence; return Task.CompletedTask; }
    }
}
