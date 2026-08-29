using PCCExecutive.Browser;
using Xunit;

namespace PCCExecutive.Integration;

public sealed class FinalPreSubmitFenceTests
{
    [Fact]
    public async Task Tamper_after_fill_blocks_press_and_leaves_dispatch_prepared()
    {
        var registry = new InMemoryBrowserRuntimeRegistry();
        var ledger = new InMemoryDispatchLedger();
        var ownership = new AlwaysOwnershipProof();
        var runtime = Runtime("1");
        await registry.UpsertAsync(runtime);
        var adapter = new BoundaryAdapter(registry, runtime.RuntimeId, tamperWorkerSlotAfterFill: true);
        var provider = new BrowserChatProvider(registry, adapter, ledger, new WrongChatGuard(), new GlobalBrowserSendGate(), ownership);
        var request = Request(runtime, "1");

        var result = await provider.SendAsync(runtime.RuntimeId, request);
        var durable = await ledger.GetAsync(request.DispatchId);

        Assert.Equal(BrowserDispatchOutcome.NotSent, result.Outcome);
        Assert.Equal("PRE_ENTER_AUTHORIZATION_DENIED", result.Reason);
        Assert.Equal(1, adapter.FillCalls);
        Assert.Equal(0, adapter.PressCalls);
        Assert.Equal(new[] { "Fill", "Authorize" }, adapter.Steps);
        Assert.NotNull(durable);
        Assert.Equal(DispatchState.Prepared, durable!.State);
    }

    [Fact]
    public async Task Fresh_boundary_proof_occurs_after_fill_immediately_before_single_press()
    {
        var registry = new InMemoryBrowserRuntimeRegistry();
        var ledger = new InMemoryDispatchLedger();
        var runtime = Runtime("1");
        await registry.UpsertAsync(runtime);
        var adapter = new BoundaryAdapter(registry, runtime.RuntimeId, tamperWorkerSlotAfterFill: false);
        var provider = new BrowserChatProvider(registry, adapter, ledger, new WrongChatGuard(), new GlobalBrowserSendGate(), new AlwaysOwnershipProof());

        var result = await provider.SendAsync(runtime.RuntimeId, Request(runtime, "1"));

        Assert.Equal(BrowserDispatchOutcome.Submitted, result.Outcome);
        Assert.Equal(1, adapter.FillCalls);
        Assert.Equal(1, adapter.PressCalls);
        Assert.Equal(new[] { "Fill", "Authorize", "Press" }, adapter.Steps);
    }

    [Fact]
    public async Task Playwright_direct_submit_has_no_unguarded_enter_path()
    {
        var runtime = Runtime("1");
        var adapter = new PlaywrightChatGptBrowserAdapter(new NullPageProvider());
        var expected = new BrowserDispatchExpectation(runtime.ProjectRunId, runtime.LogicalAgentId, runtime.TaskId!, runtime.ConversationIdentity!, runtime.ProviderConversationIdentity!, runtime.WorkerSlotId);

        var result = await adapter.SubmitAsync(runtime, expected, "prompt");

        Assert.False(result.Triggered);
        Assert.Equal("PRE_ENTER_AUTHORIZATION_REQUIRED", result.Reason);
    }

    private static BrowserRuntimeRecord Runtime(string slot) => new()
    {
        RuntimeId = "runtime-final-boundary",
        ProjectRunId = "project-run",
        LogicalAgentId = "worker-agent",
        WorkerSlotId = slot,
        TaskId = "task",
        ProfilePath = "profile",
        CreatedByPcc = true,
        ConversationIdentity = "conversation",
        ProviderConversationIdentity = "provider-conversation",
        State = BrowserSessionState.Ready,
        LastHeartbeatAt = DateTimeOffset.UtcNow,
        LastActivityAt = DateTimeOffset.UtcNow,
        OwnershipNonce = "nonce"
    };

    private static BrowserDispatchRequest Request(BrowserRuntimeRecord runtime, string slot) =>
        new("dispatch-final-boundary", runtime.ProjectRunId, runtime.LogicalAgentId, runtime.TaskId!, runtime.ConversationIdentity!, runtime.ProviderConversationIdentity!, "prompt", null, slot);

    private sealed class AlwaysOwnershipProof : IOwnershipProofService
    {
        public Task<OwnershipProof> ProveAsync(BrowserRuntimeRecord runtime, CancellationToken cancellationToken = default) =>
            Task.FromResult(OwnershipProof.Proven(runtime.RuntimeId));
    }

    private sealed class NullPageProvider : IPlaywrightPageProvider
    {
        public Task<Microsoft.Playwright.IPage?> GetPageAsync(string runtimeId, CancellationToken cancellationToken = default) => Task.FromResult<Microsoft.Playwright.IPage?>(null);
    }

    private sealed class BoundaryAdapter(InMemoryBrowserRuntimeRegistry registry, string runtimeId, bool tamperWorkerSlotAfterFill) : IPhysicalSubmitAuthorizationAdapter
    {
        public string AdapterVersion => "boundary-test";
        public int FillCalls { get; private set; }
        public int PressCalls { get; private set; }
        public List<string> Steps { get; } = [];

        public Task<ChatGptSemanticSnapshot> InspectAsync(BrowserRuntimeRecord runtime, BrowserDispatchExpectation expectation, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatGptSemanticSnapshot(
                SemanticDetection<InputState>.Create(InputState.Ready, .99, AdapterVersion, "ready"),
                SemanticDetection<GenerationState>.Create(GenerationState.Idle, .99, AdapterVersion, "idle"),
                SemanticDetection<AuthState>.Create(AuthState.Authenticated, .99, AdapterVersion, "authenticated"),
                SemanticDetection<ConversationMatch>.Create(ConversationMatch.Match, .99, AdapterVersion, "conversation-match"),
                SemanticDetection<PageHealth>.Create(PageHealth.Healthy, .99, AdapterVersion, "healthy"),
                ResponseCompleteness.None, 0, null, DateTimeOffset.UtcNow, AdapterVersion));

        public Task<AdapterSubmissionResult> SubmitAsync(BrowserRuntimeRecord runtime, BrowserDispatchExpectation expectation, string prompt, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AdapterSubmissionResult(false, false, false, "PRE_ENTER_AUTHORIZATION_REQUIRED", ["physical-enter:fence-required"]));

        public async Task<AdapterSubmissionResult> SubmitAuthorizedAsync(BrowserRuntimeRecord runtime, BrowserDispatchExpectation expectation, string prompt, Func<CancellationToken, Task<PreEnterAuthorizationDecision>> authorizeBeforeEnter, CancellationToken cancellationToken = default)
        {
            FillCalls++;
            Steps.Add("Fill");
            if (tamperWorkerSlotAfterFill)
            {
                var current = await registry.GetAsync(runtimeId, cancellationToken) ?? throw new InvalidOperationException("runtime missing");
                await registry.UpsertAsync(current with { WorkerSlotId = "2" }, cancellationToken);
            }
            Steps.Add("Authorize");
            var authorization = await authorizeBeforeEnter(cancellationToken);
            if (!authorization.Authorized)
                return new AdapterSubmissionResult(false, false, false, "PRE_ENTER_AUTHORIZATION_DENIED", authorization.Evidence.Prepend(authorization.Reason).ToArray());
            Steps.Add("Press");
            PressCalls++;
            return new AdapterSubmissionResult(true, true, false, "SUBMISSION_PROVEN", ["press:triggered"]);
        }
    }
}