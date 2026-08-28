using PCCExecutive.Application;
using PCCExecutive.Browser;
using PCCExecutive.Domain;
using PCCExecutive.Infrastructure;
using Xunit;

namespace PCCExecutive.Integration;

public sealed class FinalConvergenceIntegrationTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pcc-final-integration", Guid.NewGuid().ToString("N"));

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
    public async Task Clean_database_initializes_and_reopens_with_schema_and_project_run()
    {
        var path = Path.Combine(_root, "clean.db");
        var run = new ProjectRun(ProjectRunId.New(), ProjectId.New(), ProjectRunState.ManagerPlanning, DateTimeOffset.UtcNow, new ManagerEstimate(0), new VerifiedCompletion(0), ProjectCompletionMode.Active);

        await using (var store = new SqliteStateStore(path))
        {
            await store.InitializeAsync();
            Assert.Equal(1, await store.GetSchemaVersionAsync());
            await store.SaveProjectRunAsync(run);
        }

        await using (var reopened = new SqliteStateStore(path))
        {
            await reopened.InitializeAsync();
            var restored = await reopened.LoadProjectRunAsync(run.Id);
            Assert.NotNull(restored);
            Assert.Equal(run.Id, restored!.Id);
            Assert.Equal(ProjectRunState.ManagerPlanning, restored.State);
        }
    }

    [Fact]
    public void Project_singleton_lock_allows_one_owner_and_releases_cleanly()
    {
        var identity = "PCCEXECUTIVE|walidatiyaai2025-gif/walid|" + Guid.NewGuid().ToString("N");
        using var first = ProjectRunLock.TryAcquire(identity);
        using var second = ProjectRunLock.TryAcquire(identity);
        Assert.True(first.IsOwned);
        Assert.False(second.IsOwned);
        first.Dispose();
        using var third = ProjectRunLock.TryAcquire(identity);
        Assert.True(third.IsOwned);
    }

    [Fact]
    public async Task Browser_agent_provider_rejects_wrong_project_agent_task_slot_and_conversation_before_submit()
    {
        var run = ProjectRunId.New();
        var agent = LogicalAgentId.New();
        var task = TaskId.New();
        var conversation = ConversationId.New();
        var runtime = Runtime(run, agent, task, conversation, "1");
        var registry = new InMemoryBrowserRuntimeRegistry();
        await registry.UpsertAsync(runtime);
        var adapter = new CountingAdapter();
        var browserProvider = new BrowserChatProvider(registry, adapter, new InMemoryDispatchLedger(), new WrongChatGuard(), new GlobalBrowserSendGate(), new StaticOwnership(true));
        var provider = new BrowserAgentProviderAdapter(registry, browserProvider);

        var requests = new[]
        {
            Request(ProjectRunId.New(), agent, task, conversation, new WorkerSlotId(1), "wrong-project"),
            Request(run, LogicalAgentId.New(), task, conversation, new WorkerSlotId(1), "wrong-agent"),
            Request(run, agent, TaskId.New(), conversation, new WorkerSlotId(1), "wrong-task"),
            Request(run, agent, task, conversation, new WorkerSlotId(2), "wrong-slot"),
            Request(run, agent, task, ConversationId.New(), new WorkerSlotId(1), "wrong-conversation")
        };

        foreach (var request in requests)
        {
            var result = await provider.SendAsync(request);
            Assert.False(result.Accepted);
        }

        Assert.Equal(0, adapter.SubmitCalls);
    }

    [Fact]
    public async Task Ownership_tamper_blocks_browser_submission_with_zero_submit_calls()
    {
        var run = ProjectRunId.New();
        var agent = LogicalAgentId.New();
        var task = TaskId.New();
        var conversation = ConversationId.New();
        var runtime = Runtime(run, agent, task, conversation, "1");
        var registry = new InMemoryBrowserRuntimeRegistry();
        await registry.UpsertAsync(runtime);
        var adapter = new CountingAdapter();
        var provider = new BrowserChatProvider(registry, adapter, new InMemoryDispatchLedger(), new WrongChatGuard(), new GlobalBrowserSendGate(), new StaticOwnership(false));
        var request = new BrowserDispatchRequest(DispatchId.New().ToString(), run.ToString(), agent.ToString(), task.ToString(), conversation.ToString(), runtime.ProviderConversationIdentity!, "prompt", "hash", "1");

        var result = await provider.SendAsync(runtime.RuntimeId, request);

        Assert.Equal(BrowserDispatchOutcome.NotSent, result.Outcome);
        Assert.Equal("PCC_OWNERSHIP_NOT_PROVEN", result.Reason);
        Assert.Equal(0, adapter.SubmitCalls);
    }

    [Fact]
    public async Task Submitted_unknown_restart_preserves_same_dispatch_identity_and_never_blindly_resends()
    {
        var path = Path.Combine(_root, "submitted-unknown.db");
        await using var store = new SqliteStateStore(path);
        await store.InitializeAsync();
        var run = new ProjectRun(ProjectRunId.New(), ProjectId.New(), ProjectRunState.Dispatching, DateTimeOffset.UtcNow, new ManagerEstimate(25), new VerifiedCompletion(0), ProjectCompletionMode.Active);
        var task = new WorkerTask(TaskId.New(), "uncertain", TaskScope.Create("walidatiyaai2025-gif/walid", ["tests"]), new HashSet<TaskId>(), ["evidence"], TaskState.Dispatched, "fp");
        var wave = new Wave(WaveId.New(), run.Id, 1, WaveState.Dispatching, [task.Id], DateTimeOffset.UtcNow);
        var dispatch = new PCCExecutive.Domain.Dispatch(DispatchId.New(), run.Id, wave.Id, task.Id, LogicalAgentId.New(), ConversationId.New(), "content-hash", DateTimeOffset.UtcNow, PCCExecutive.Domain.DispatchState.PREPARED, null, null, null, null, null);
        var snapshot = new OrchestrationRecoverySnapshot(run, wave, [task], new Dictionary<TaskId, WorkerSlotId> { [task.Id] = new(1) }, [dispatch], null, OrchestrationPhase.Dispatching, DateTimeOffset.UtcNow);
        var orchestration = new SqliteOrchestrationStateStore(store);
        await orchestration.SaveAsync(snapshot);
        await store.SaveDispatchAsync(dispatch);
        await store.ReserveAsync(dispatch.Id.ToString(), dispatch.ContentHash);
        await store.UpdateAsync(dispatch.Id.ToString(), PCCExecutive.Browser.DispatchState.Submitting, "enter-triggered");

        var recovered = await new DurableStartupRecoveryService(store, orchestration).ReconstructAsync(run.Id);
        var restored = Assert.Single(recovered!.Dispatches);
        Assert.Equal(dispatch.Id, restored.Id);
        Assert.Equal(dispatch.ContentHash, restored.ContentHash);
        Assert.Equal(PCCExecutive.Domain.DispatchState.SUBMITTED_UNKNOWN, restored.State);

        var journal = new AutonomousDispatchJournal(store);
        var reconciliation = await journal.ReconcileAsync(restored);
        Assert.True(reconciliation.IsUncertain);
        Assert.False(reconciliation.SafeToSubmit);
        Assert.Equal(dispatch.Id, reconciliation.Dispatch.Id);
    }

    [Fact]
    public async Task Durable_rollover_commits_lineage_with_exactly_one_active_conversation()
    {
        var path = Path.Combine(_root, "rollover.db");
        await using var store = new SqliteStateStore(path);
        await store.InitializeAsync();
        var runId = ProjectRunId.New();
        var agentId = LogicalAgentId.New();
        var oldId = ConversationId.New();
        var newId = ConversationId.New();
        var run = new ProjectRun(runId, ProjectId.New(), ProjectRunState.WaveRunning, DateTimeOffset.UtcNow, new ManagerEstimate(50), new VerifiedCompletion(50), ProjectCompletionMode.Active);
        var orchestration = new SqliteOrchestrationStateStore(store);
        await orchestration.SaveAsync(new OrchestrationRecoverySnapshot(run, null, [], new Dictionary<TaskId, WorkerSlotId>(), [], null, OrchestrationPhase.WaveRunning, DateTimeOffset.UtcNow));
        await store.SaveConversationAsync(new PCCExecutive.Domain.Conversation(oldId, agentId, 1, AgentProviderKind.BrowserChat, "provider-old", "url-old", ConversationState.Active, DateTimeOffset.UtcNow, null, null, null, 1, 1, null, null), runId);
        await store.SaveLogicalAgentAsync(new LogicalAgentSession(agentId, runId, AgentRole.Worker, new WorkerSlotId(1), null, oldId, LogicalSessionState.Active));
        var predecessor = new ConversationRecord { ConversationId = oldId.ToString(), LogicalAgentId = agentId.ToString(), ProjectRunId = runId.ToString(), Sequence = 1, UrlOrProviderIdentity = "url-old", CreatedAt = DateTimeOffset.UtcNow, State = ConversationLifecycleState.Active };
        var successor = new ConversationRecord { ConversationId = newId.ToString(), LogicalAgentId = agentId.ToString(), ProjectRunId = runId.ToString(), Sequence = 2, UrlOrProviderIdentity = "url-new", CreatedAt = DateTimeOffset.UtcNow, PredecessorConversationId = oldId.ToString(), State = ConversationLifecycleState.Candidate };
        var lifecycle = new DurableConversationLifecycleStore(store);
        var checkpoint = CheckpointId.New().ToString();
        await lifecycle.SaveCandidateAsync(successor, checkpoint);
        await lifecycle.CommitRolloverAsync(predecessor with { State = ConversationLifecycleState.Archived, SuccessorConversationId = newId.ToString(), RetiredAt = DateTimeOffset.UtcNow }, successor with { State = ConversationLifecycleState.Active }, checkpoint);

        var recovery = new FullDurabilityRecoveryService(store, orchestration);
        Assert.True(await new ConversationInvariantService(recovery).ExactlyOneActiveAsync(runId, agentId));
        var session = await store.LoadLogicalAgentAsync(agentId);
        Assert.Equal(newId, session!.CurrentConversationId);
    }

    private AgentRequest Request(ProjectRunId run, LogicalAgentId agent, TaskId task, ConversationId conversation, WorkerSlotId slot, string content) =>
        new(run, agent, conversation, DispatchId.New(), content, content + "-hash", slot, task, WaveId.New());

    private BrowserRuntimeRecord Runtime(ProjectRunId run, LogicalAgentId agent, TaskId task, ConversationId conversation, string slot) => new()
    {
        RuntimeId = "runtime-" + Guid.NewGuid().ToString("N"),
        ProjectRunId = run.ToString(),
        LogicalAgentId = agent.ToString(),
        WorkerSlotId = slot,
        TaskId = task.ToString(),
        ProcessId = 1001,
        ProcessStartIdentity = "pid:1001:start:1",
        ContextIdentity = "ctx",
        ProfilePath = Path.Combine(_root, "profile"),
        CreatedByPcc = true,
        AdoptedExplicitly = false,
        ConversationIdentity = conversation.ToString(),
        ProviderConversationIdentity = "https://chatgpt.com/c/provider",
        Visibility = BrowserVisibility.Hidden,
        State = BrowserSessionState.Hidden,
        LastHeartbeatAt = DateTimeOffset.UtcNow,
        LastActivityAt = DateTimeOffset.UtcNow,
        OwnershipNonce = "nonce"
    };

    private sealed class StaticOwnership(bool proven) : IOwnershipProofService
    {
        public Task<OwnershipProof> ProveAsync(BrowserRuntimeRecord runtime, CancellationToken cancellationToken = default) =>
            Task.FromResult(proven ? OwnershipProof.Proven(runtime.RuntimeId) : OwnershipProof.Denied(runtime.RuntimeId, "TEST_TAMPER"));
    }

    private sealed class CountingAdapter : IChatGptBrowserAdapter
    {
        public string AdapterVersion => "integration";
        public int SubmitCalls { get; private set; }
        public Task<ChatGptSemanticSnapshot> InspectAsync(BrowserRuntimeRecord runtime, BrowserDispatchExpectation expectation, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatGptSemanticSnapshot(
                SemanticDetection<InputState>.Create(InputState.Ready, .99, AdapterVersion, "ready"),
                SemanticDetection<GenerationState>.Create(GenerationState.Idle, .99, AdapterVersion, "idle"),
                SemanticDetection<AuthState>.Create(AuthState.Authenticated, .99, AdapterVersion, "auth"),
                SemanticDetection<ConversationMatch>.Create(ConversationMatch.Match, .99, AdapterVersion, "match"),
                SemanticDetection<PageHealth>.Create(PageHealth.Healthy, .99, AdapterVersion, "healthy"),
                ResponseCompleteness.None, 0, null, DateTimeOffset.UtcNow, AdapterVersion));
        public Task<AdapterSubmissionResult> SubmitAsync(BrowserRuntimeRecord runtime, BrowserDispatchExpectation expectation, string prompt, CancellationToken cancellationToken = default)
        {
            SubmitCalls++;
            return Task.FromResult(new AdapterSubmissionResult(true, true, false, "submitted", ["submitted"]));
        }
    }
}
