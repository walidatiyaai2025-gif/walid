using PCCExecutive.Application;
using PCCExecutive.Browser;
using PCCExecutive.Domain;
using PCCExecutive.Infrastructure;
using Xunit;

namespace PCCExecutive.Integration;

public sealed class FinalPreSubmitFenceTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pcc-pre-submit-fence", Guid.NewGuid().ToString("N"));

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_root, true); } catch { }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Ownership_tamper_between_preflight_and_final_boundary_has_zero_send_or_durable_advancement()
    {
        var db = Path.Combine(_root, "tamper.db");
        await using var store = new SqliteStateStore(db);
        await store.InitializeAsync();

        var run = ProjectRunId.New();
        var agent = LogicalAgentId.New();
        var task = TaskId.New();
        var wave = WaveId.New();
        var conversation = ConversationId.New();
        var dispatch = DispatchId.New();
        var runtime = new BrowserRuntimeRecord
        {
            RuntimeId = "runtime-final-boundary",
            ProjectRunId = run.ToString(),
            LogicalAgentId = agent.ToString(),
            WorkerSlotId = "1",
            TaskId = task.ToString(),
            ProcessId = 1001,
            ProcessStartIdentity = "pid:1001:start:1",
            ContextIdentity = "ctx",
            ProfilePath = Path.Combine(_root, "profile"),
            CreatedByPcc = true,
            AdoptedExplicitly = false,
            ConversationIdentity = conversation.ToString(),
            ProviderConversationIdentity = "provider-conversation",
            Visibility = BrowserVisibility.Hidden,
            State = BrowserSessionState.Hidden,
            LastHeartbeatAt = DateTimeOffset.UtcNow,
            LastActivityAt = DateTimeOffset.UtcNow,
            OwnershipNonce = "nonce"
        };
        await store.UpsertAsync(runtime);

        var ownership = new SequencedOwnershipProof(true, false);
        var adapter = new CountingHealthyAdapter();
        var browser = new BrowserChatProvider(store, adapter, store, new WrongChatGuard(), new GlobalBrowserSendGate(), ownership);
        var provider = new BrowserAgentProviderAdapter(store, browser, ownership);
        var request = new AgentRequest(run, agent, conversation, dispatch, "prompt", "content-hash", new WorkerSlotId(1), task, wave);

        var result = await provider.SendAsync(request);

        Assert.False(result.Accepted);
        Assert.Equal("PCC_OWNERSHIP_NOT_PROVEN", result.Error);
        Assert.Equal(2, ownership.ProofCalls);
        Assert.Equal(0, adapter.SubmitCalls);
        Assert.Null(await store.GetAsync(dispatch.ToString()));
        Assert.Empty(await new AutonomousDispatchJournal(store).ListAsync(run));
    }

    private sealed class SequencedOwnershipProof(params bool[] outcomes) : IOwnershipProofService
    {
        private int _index;
        public int ProofCalls => _index;

        public Task<OwnershipProof> ProveAsync(BrowserRuntimeRecord runtime, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = Interlocked.Increment(ref _index) - 1;
            var proven = index < outcomes.Length && outcomes[index];
            return Task.FromResult(proven
                ? OwnershipProof.Proven(runtime.RuntimeId)
                : OwnershipProof.Denied(runtime.RuntimeId, "TEST_OWNERSHIP_TAMPER"));
        }
    }

    private sealed class CountingHealthyAdapter : IChatGptBrowserAdapter
    {
        public string AdapterVersion => "final-pre-submit";
        public int SubmitCalls { get; private set; }

        public Task<ChatGptSemanticSnapshot> InspectAsync(BrowserRuntimeRecord runtime, BrowserDispatchExpectation expectation, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatGptSemanticSnapshot(
                SemanticDetection<InputState>.Create(InputState.Ready, .99, AdapterVersion, "ready"),
                SemanticDetection<GenerationState>.Create(GenerationState.Idle, .99, AdapterVersion, "idle"),
                SemanticDetection<AuthState>.Create(AuthState.Authenticated, .99, AdapterVersion, "authenticated"),
                SemanticDetection<ConversationMatch>.Create(ConversationMatch.Match, .99, AdapterVersion, "conversation-match"),
                SemanticDetection<PageHealth>.Create(PageHealth.Healthy, .99, AdapterVersion, "healthy"),
                ResponseCompleteness.None,
                0,
                null,
                DateTimeOffset.UtcNow,
                AdapterVersion));

        public Task<AdapterSubmissionResult> SubmitAsync(BrowserRuntimeRecord runtime, BrowserDispatchExpectation expectation, string prompt, CancellationToken cancellationToken = default)
        {
            SubmitCalls++;
            return Task.FromResult(new AdapterSubmissionResult(true, true, false, "submitted", ["submitted"]));
        }
    }
}