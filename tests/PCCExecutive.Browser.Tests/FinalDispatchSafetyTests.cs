using PCCExecutive.Browser;
using Xunit;

namespace PCCExecutive.Browser.Tests;

public sealed class FinalDispatchSafetyTests
{
    [Fact]
    public async Task Ownership_tamper_between_fill_and_enter_blocks_enter_and_state_advance()
    {
        var runtime = Runtime();
        var registry = new InMemoryBrowserRuntimeRegistry();
        await registry.UpsertAsync(runtime);
        var ledger = new InMemoryDispatchLedger();
        var adapter = new FinalBoundaryAdapter();
        var provider = new BrowserChatProvider(registry, adapter, ledger, new WrongChatGuard(), new GlobalBrowserSendGate(), new SequencedOwnershipProof(true, false));

        var result = await provider.SendAsync(runtime.RuntimeId, Request(runtime, "dispatch-ownership"));
        var persisted = await ledger.GetAsync("dispatch-ownership");

        Assert.Equal(BrowserDispatchOutcome.NotSent, result.Outcome);
        Assert.Equal("FINAL_ENTER_AUTHORIZATION_FAILED", result.Reason);
        Assert.Equal(1, adapter.FillCount);
        Assert.Equal(0, adapter.EnterCount);
        Assert.Equal(0, adapter.SubmitCount);
        Assert.Equal(DispatchState.Prepared, persisted!.State);
    }

    [Theory]
    [InlineData("slot")]
    [InlineData("task")]
    [InlineData("conversation")]
    [InlineData("provider")]
    [InlineData("project")]
    [InlineData("agent")]
    public async Task Binding_tamper_between_fill_and_enter_blocks_enter_and_state_advance(string field)
    {
        var runtime = Runtime();
        var registry = new InMemoryBrowserRuntimeRegistry();
        await registry.UpsertAsync(runtime);
        var ledger = new InMemoryDispatchLedger();
        var adapter = new FinalBoundaryAdapter(async () =>
        {
            var current = (await registry.GetAsync(runtime.RuntimeId))!;
            var changed = field switch
            {
                "slot" => current with { WorkerSlotId = "2" },
                "task" => current with { TaskId = "different-task" },
                "conversation" => current with { ConversationIdentity = "different-conversation" },
                "provider" => current with { ProviderConversationIdentity = "different-provider" },
                "project" => current with { ProjectRunId = "different-run" },
                "agent" => current with { LogicalAgentId = "different-agent" },
                _ => current
            };
            await registry.UpsertAsync(changed);
        });
        var provider = new BrowserChatProvider(registry, adapter, ledger, new WrongChatGuard(), new GlobalBrowserSendGate(), new AlwaysOwnershipProof());

        var result = await provider.SendAsync(runtime.RuntimeId, Request(runtime, $"dispatch-{field}"));
        var persisted = await ledger.GetAsync($"dispatch-{field}");

        Assert.Equal(BrowserDispatchOutcome.NotSent, result.Outcome);
        Assert.Equal("FINAL_ENTER_AUTHORIZATION_FAILED", result.Reason);
        Assert.Equal(1, adapter.FillCount);
        Assert.Equal(0, adapter.EnterCount);
        Assert.Equal(0, adapter.SubmitCount);
        Assert.Equal(DispatchState.Prepared, persisted!.State);
    }

    [Fact]
    public async Task Content_hash_conflict_is_blocked_without_duplicate_adapter_submit()
    {
        var runtime = Runtime();
        var registry = new InMemoryBrowserRuntimeRegistry();
        await registry.UpsertAsync(runtime);
        var ledger = new InMemoryDispatchLedger();
        var adapter = new FinalBoundaryAdapter();
        var provider = new BrowserChatProvider(registry, adapter, ledger, new WrongChatGuard(), new GlobalBrowserSendGate(), new AlwaysOwnershipProof());

        var first = await provider.SendAsync(runtime.RuntimeId, Request(runtime, "dispatch-conflict", "hash-a"));
        var submitsAfterFirst = adapter.SubmitCount;
        var second = await provider.SendAsync(runtime.RuntimeId, Request(runtime, "dispatch-conflict", "hash-b"));

        Assert.Equal(BrowserDispatchOutcome.Submitted, first.Outcome);
        Assert.Equal(BrowserDispatchOutcome.DuplicateBlocked, second.Outcome);
        Assert.Equal("DISPATCH_ID_CONTENT_HASH_CONFLICT", second.Reason);
        Assert.Equal(1, submitsAfterFirst);
        Assert.Equal(submitsAfterFirst, adapter.SubmitCount);
    }

    [Fact]
    public async Task Direct_playwright_submit_cannot_bypass_final_authorization()
    {
        var adapter = new PlaywrightChatGptBrowserAdapter(new NullPageProvider());
        var runtime = Runtime();
        var result = await adapter.SubmitAsync(runtime, Expectation(runtime), "prompt");

        Assert.False(result.Triggered);
        Assert.Equal("FINAL_ENTER_AUTHORIZATION_REQUIRED", result.Reason);
    }

    private static BrowserRuntimeRecord Runtime() => new()
    {
        RuntimeId = "runtime",
        ProjectRunId = "project-run",
        LogicalAgentId = "agent-1",
        WorkerSlotId = "1",
        TaskId = "task-1",
        ProcessId = 4001,
        ProcessStartIdentity = "pid:4001:start:1",
        ContextIdentity = "ctx-1",
        ProfilePath = Path.Combine(Path.GetTempPath(), "pcc-final-enter", "runtime"),
        CreatedByPcc = true,
        ConversationIdentity = "conversation-1",
        ProviderConversationIdentity = "provider-conversation-1",
        State = BrowserSessionState.Ready,
        LastHeartbeatAt = DateTimeOffset.UtcNow,
        LastActivityAt = DateTimeOffset.UtcNow,
        OwnershipNonce = "nonce"
    };

    private static BrowserDispatchExpectation Expectation(BrowserRuntimeRecord runtime) =>
        new(runtime.ProjectRunId, runtime.LogicalAgentId, runtime.TaskId!, runtime.ConversationIdentity!, runtime.ProviderConversationIdentity!, runtime.WorkerSlotId);

    private static BrowserDispatchRequest Request(BrowserRuntimeRecord runtime, string dispatchId, string hash = "hash") =>
        new(dispatchId, runtime.ProjectRunId, runtime.LogicalAgentId, runtime.TaskId!, runtime.ConversationIdentity!, runtime.ProviderConversationIdentity!, "prompt", hash, runtime.WorkerSlotId);

    private sealed class FinalBoundaryAdapter(Func<Task>? afterFill = null) : IFinalEnterAuthorizationAdapter
    {
        public string AdapterVersion => "final-boundary-test";
        public int FillCount { get; private set; }
        public int EnterCount { get; private set; }
        public int SubmitCount { get; private set; }

        public Task<ChatGptSemanticSnapshot> InspectAsync(BrowserRuntimeRecord runtime, BrowserDispatchExpectation expectation, CancellationToken cancellationToken = default) =>
            Task.FromResult(Healthy());

        public Task<AdapterSubmissionResult> SubmitAsync(BrowserRuntimeRecord runtime, BrowserDispatchExpectation expectation, string prompt, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The provider must use final-enter authorization.");

        public async Task<AdapterSubmissionResult> SubmitWithFinalAuthorizationAsync(
            BrowserRuntimeRecord runtime,
            BrowserDispatchExpectation expectation,
            string prompt,
            Func<CancellationToken, Task<FinalEnterAuthorizationResult>> finalAuthorization,
            CancellationToken cancellationToken = default)
        {
            FillCount++;
            if (afterFill is not null) await afterFill().ConfigureAwait(false);
            var authorization = await finalAuthorization(cancellationToken).ConfigureAwait(false);
            if (!authorization.IsAuthorized)
                return new(false, false, false, "FINAL_ENTER_AUTHORIZATION_FAILED", authorization.Evidence.Prepend(authorization.Reason).ToArray());
            EnterCount++;
            SubmitCount++;
            return new(true, true, false, "SUBMITTED", authorization.Evidence.Append("test:enter").ToArray());
        }

        private static ChatGptSemanticSnapshot Healthy() => new(
            SemanticDetection<InputState>.Create(InputState.Ready, 1, "test", "input-ready"),
            SemanticDetection<GenerationState>.Create(GenerationState.Idle, 1, "test", "idle"),
            SemanticDetection<AuthState>.Create(AuthState.Authenticated, 1, "test", "auth"),
            SemanticDetection<ConversationMatch>.Create(ConversationMatch.Match, 1, "test", "conversation"),
            SemanticDetection<PageHealth>.Create(PageHealth.Healthy, 1, "test", "healthy"),
            ResponseCompleteness.None,
            0,
            null,
            DateTimeOffset.UtcNow,
            "test");
    }

    private sealed class AlwaysOwnershipProof : IOwnershipProofService
    {
        public Task<OwnershipProof> ProveAsync(BrowserRuntimeRecord runtime, CancellationToken cancellationToken = default) =>
            Task.FromResult(OwnershipProof.Proven(runtime.RuntimeId));
    }

    private sealed class SequencedOwnershipProof(params bool[] results) : IOwnershipProofService
    {
        private int _index;
        public Task<OwnershipProof> ProveAsync(BrowserRuntimeRecord runtime, CancellationToken cancellationToken = default)
        {
            var index = Interlocked.Increment(ref _index) - 1;
            var proven = index < results.Length && results[index];
            return Task.FromResult(proven ? OwnershipProof.Proven(runtime.RuntimeId) : OwnershipProof.Denied(runtime.RuntimeId, "PROCESS_IDENTITY_CHANGED"));
        }
    }

    private sealed class NullPageProvider : IPlaywrightPageProvider
    {
        public Task<Microsoft.Playwright.IPage?> GetPageAsync(string runtimeId, CancellationToken cancellationToken = default) =>
            Task.FromResult<Microsoft.Playwright.IPage?>(null);
    }
}
