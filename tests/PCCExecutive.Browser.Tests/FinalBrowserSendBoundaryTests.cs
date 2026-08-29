using PCCExecutive.Browser;
using Xunit;

namespace PCCExecutive.Browser.Tests;

public sealed class FinalBrowserSendBoundaryTests
{
    [Fact]
    public async Task Worker_slot_tamper_after_fill_blocks_enter_and_keeps_dispatch_prepared()
    {
        var runtime = Runtime("worker-1-runtime", "worker-1-agent", "1", "task-1", "W1-C01");
        var registry = new InMemoryBrowserRuntimeRegistry();
        await registry.UpsertAsync(runtime);
        var ledger = new InMemoryDispatchLedger();
        var adapter = new BoundaryPhysicalAdapter(Healthy());
        adapter.AfterFill = () => registry.UpsertAsync(runtime with { WorkerSlotId = "2" });
        var provider = Provider(registry, adapter, ledger);
        var request = Request(runtime, "dispatch-slot-tamper", "slot tamper proof");

        var result = await provider.SendAsync(runtime.RuntimeId, request);
        var persisted = await ledger.GetAsync(request.DispatchId);

        Assert.Equal(BrowserDispatchOutcome.NotSent, result.Outcome);
        Assert.Equal(DispatchState.Prepared, result.State);
        Assert.Equal(DispatchState.Prepared, persisted!.State);
        Assert.Equal(1, adapter.FillCount);
        Assert.Equal(0, adapter.EnterCount);
        Assert.Equal(0, adapter.SubmitAsyncCount);
        Assert.Contains(result.Evidence, item => item.Contains("FINAL_WORKER_SLOT_MISMATCH", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Provider_conversation_drift_after_fill_is_reinspected_and_blocks_enter()
    {
        var runtime = Runtime("worker-2-runtime", "worker-2-agent", "2", "task-2", "W2-C01");
        var registry = new InMemoryBrowserRuntimeRegistry();
        await registry.UpsertAsync(runtime);
        var ledger = new InMemoryDispatchLedger();
        var adapter = new BoundaryPhysicalAdapter(Healthy());
        adapter.AfterFill = () =>
        {
            adapter.Snapshot = Healthy(ConversationMatch.Mismatch);
            return Task.CompletedTask;
        };
        var provider = Provider(registry, adapter, ledger);
        var request = Request(runtime, "dispatch-chat-drift", "wrong-chat race proof");

        var result = await provider.SendAsync(runtime.RuntimeId, request);
        var persisted = await ledger.GetAsync(request.DispatchId);

        Assert.Equal(BrowserDispatchOutcome.NotSent, result.Outcome);
        Assert.Equal(DispatchState.Prepared, persisted!.State);
        Assert.Equal(0, adapter.EnterCount);
        Assert.Equal(0, adapter.SubmitAsyncCount);
        Assert.Contains(result.Evidence, item => item.Contains("FINAL_PROVIDER_CONVERSATION_MISMATCH", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Proven_physical_submission_is_exactly_once_for_same_dispatch()
    {
        var runtime = Runtime("worker-3-runtime", "worker-3-agent", "3", "task-3", "W3-C01");
        var registry = new InMemoryBrowserRuntimeRegistry();
        await registry.UpsertAsync(runtime);
        var ledger = new InMemoryDispatchLedger();
        var adapter = new BoundaryPhysicalAdapter(Healthy());
        var provider = Provider(registry, adapter, ledger);
        var request = Request(runtime, "dispatch-dedupe", "exactly once proof");

        var first = await provider.SendAsync(runtime.RuntimeId, request);
        var duplicate = await provider.SendAsync(runtime.RuntimeId, request);

        Assert.Equal(BrowserDispatchOutcome.Submitted, first.Outcome);
        Assert.Equal(BrowserDispatchOutcome.DuplicateBlocked, duplicate.Outcome);
        Assert.Equal(1, adapter.FillCount);
        Assert.Equal(1, adapter.EnterCount);
        Assert.Equal(0, adapter.SubmitAsyncCount);
    }

    [Fact]
    public async Task Missing_or_null_uncertain_semantic_evidence_never_authorizes_retry()
    {
        var dispatch = new DispatchLedgerEntry("dispatch-unknown", "HASH", DispatchState.SubmittedUnknown, DateTimeOffset.UtcNow);
        var missingEvidence = new UncertainSendReconciler(new FixedProbe(new ConversationDispatchEvidence(null, false, false, .50, null!)));
        var nullProbeResult = new UncertainSendReconciler(new NullProbe());

        var ambiguous = await missingEvidence.ReconcileAsync("worker-runtime", dispatch);
        var missing = await nullProbeResult.ReconcileAsync("worker-runtime", dispatch);

        Assert.Equal(SendReconciliationState.CannotDetermine, ambiguous.State);
        Assert.Equal(RetrySafety.NotSafe, ambiguous.RetrySafety);
        Assert.Empty(ambiguous.Evidence);
        Assert.Equal(SendReconciliationState.CannotDetermine, missing.State);
        Assert.Equal(RetrySafety.NotSafe, missing.RetrySafety);
        Assert.Equal("PROBE_RETURNED_NULL_NO_AUTOMATIC_RESEND", missing.Reason);
    }

    private static BrowserChatProvider Provider(IBrowserRuntimeRegistry registry, IChatGptBrowserAdapter adapter, IDispatchLedger ledger) =>
        new(registry, adapter, ledger, new WrongChatGuard(), new GlobalBrowserSendGate(), new AlwaysOwned());

    private static BrowserDispatchRequest Request(BrowserRuntimeRecord runtime, string dispatchId, string prompt) =>
        new(dispatchId, runtime.ProjectRunId, runtime.LogicalAgentId, runtime.TaskId!, runtime.ConversationIdentity!, runtime.ProviderConversationIdentity!, prompt, null, runtime.WorkerSlotId);

    private static BrowserRuntimeRecord Runtime(string runtimeId, string logicalAgentId, string workerSlotId, string taskId, string conversationId) =>
        new()
        {
            RuntimeId = runtimeId,
            ProjectRunId = "project-run",
            LogicalAgentId = logicalAgentId,
            WorkerSlotId = workerSlotId,
            TaskId = taskId,
            ProcessId = 41001,
            ProcessStartIdentity = "pid:41001:start:final-boundary",
            ContextIdentity = $"ctx-{runtimeId}",
            ProfilePath = Path.Combine(Path.GetTempPath(), "pcc-final-boundary", runtimeId),
            CreatedByPcc = true,
            AdoptedExplicitly = false,
            ConversationIdentity = conversationId,
            ProviderConversationIdentity = $"https://chatgpt.com/c/{conversationId}",
            Visibility = BrowserVisibility.Hidden,
            State = BrowserSessionState.Hidden,
            LastHeartbeatAt = DateTimeOffset.UtcNow,
            LastActivityAt = DateTimeOffset.UtcNow,
            OwnershipNonce = $"nonce-{runtimeId}"
        };

    private static ChatGptSemanticSnapshot Healthy(ConversationMatch conversation = ConversationMatch.Match) =>
        new(
            SemanticDetection<InputState>.Create(InputState.Ready, .95, "final-boundary-test", "input:ready"),
            SemanticDetection<GenerationState>.Create(GenerationState.Idle, .95, "final-boundary-test", "generation:idle"),
            SemanticDetection<AuthState>.Create(AuthState.Authenticated, .95, "final-boundary-test", "auth:authenticated"),
            SemanticDetection<ConversationMatch>.Create(conversation, .95, "final-boundary-test", $"conversation:{conversation}"),
            SemanticDetection<PageHealth>.Create(PageHealth.Healthy, .95, "final-boundary-test", "health:healthy"),
            ResponseCompleteness.None,
            0,
            null,
            DateTimeOffset.UtcNow,
            "final-boundary-test");

    private sealed class BoundaryPhysicalAdapter(ChatGptSemanticSnapshot initial) : IPhysicalSubmitAuthorizationAdapter
    {
        public string AdapterVersion => "final-boundary-test";
        public ChatGptSemanticSnapshot Snapshot { get; set; } = initial;
        public Func<Task>? AfterFill { get; set; }
        public int FillCount { get; private set; }
        public int EnterCount { get; private set; }
        public int SubmitAsyncCount { get; private set; }

        public Task<ChatGptSemanticSnapshot> InspectAsync(BrowserRuntimeRecord runtime, BrowserDispatchExpectation expectation, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Snapshot);
        }

        public Task<AdapterSubmissionResult> SubmitAsync(BrowserRuntimeRecord runtime, BrowserDispatchExpectation expectation, string prompt, CancellationToken cancellationToken = default)
        {
            SubmitAsyncCount++;
            return Task.FromResult(new AdapterSubmissionResult(false, false, false, "UNAUTHORIZED_DIRECT_SUBMIT", ["submit-async:must-not-run"]));
        }

        public async Task<AdapterSubmissionResult> SubmitAuthorizedAsync(
            BrowserRuntimeRecord runtime,
            BrowserDispatchExpectation expectation,
            string prompt,
            Func<CancellationToken, Task<PreEnterAuthorizationDecision>> authorizeBeforeEnter,
            CancellationToken cancellationToken = default)
        {
            FillCount++;
            if (AfterFill is not null)
                await AfterFill().ConfigureAwait(false);

            var authorization = await authorizeBeforeEnter(cancellationToken).ConfigureAwait(false);
            if (!authorization.Authorized)
                return new(false, false, false, "PRE_ENTER_AUTHORIZATION_DENIED", authorization.Evidence.Prepend(authorization.Reason).ToArray());

            EnterCount++;
            return new(true, true, false, "SUBMISSION_PROVEN", authorization.Evidence.Append("physical-enter:triggered").ToArray());
        }
    }

    private sealed class AlwaysOwned : IOwnershipProofService
    {
        public Task<OwnershipProof> ProveAsync(BrowserRuntimeRecord runtime, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(OwnershipProof.Proven(runtime.RuntimeId));
        }
    }

    private sealed class FixedProbe(ConversationDispatchEvidence evidence) : IConversationEvidenceProbe
    {
        public Task<ConversationDispatchEvidence> InspectDispatchAsync(string runtimeId, string dispatchId, string contentHash, CancellationToken cancellationToken = default) =>
            Task.FromResult(evidence);
    }

    private sealed class NullProbe : IConversationEvidenceProbe
    {
        public Task<ConversationDispatchEvidence> InspectDispatchAsync(string runtimeId, string dispatchId, string contentHash, CancellationToken cancellationToken = default) =>
            Task.FromResult<ConversationDispatchEvidence>(null!);
    }
}
