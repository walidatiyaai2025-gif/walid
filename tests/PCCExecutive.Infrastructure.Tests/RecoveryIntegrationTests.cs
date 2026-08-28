using PCCExecutive.Application;
using PCCExecutive.Browser;
using PCCExecutive.Domain;
using PCCExecutive.Infrastructure;
using Xunit;

namespace PCCExecutive.Infrastructure.Tests;

public sealed class RecoveryIntegrationTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pcc-recovery-integration", Guid.NewGuid().ToString("N"));
    public Task InitializeAsync() { Directory.CreateDirectory(_root); return Task.CompletedTask; }
    public Task DisposeAsync() { try { Directory.Delete(_root, true); } catch { } return Task.CompletedTask; }

    [Fact]
    public async Task Orchestration_snapshot_round_trips_assignments_and_dispatches()
    {
        await using var store = await NewStoreAsync("orchestration.db");
        var adapter = new SqliteOrchestrationStateStore(store);
        var run = Run(ProjectRunState.WaveRunning);
        var task = WorkerTaskOf("one");
        var wave = new Wave(WaveId.New(), run.Id, 1, WaveState.Running, [task.Id], DateTimeOffset.UtcNow);
        var dispatch = Dispatch(run.Id, wave.Id, task.Id, PCCExecutive.Domain.DispatchState.GENERATING);
        var snapshot = new OrchestrationRecoverySnapshot(run, wave, [task], new Dictionary<TaskId, WorkerSlotId> { [task.Id] = new(1) }, [dispatch], null, OrchestrationPhase.WaveRunning, DateTimeOffset.UtcNow);
        await adapter.SaveAsync(snapshot);
        var restored = await adapter.LoadAsync(run.Id);
        Assert.NotNull(restored);
        Assert.Equal(task.Id, restored!.Tasks.Single().Id);
        Assert.Equal(1, restored.Assignments[task.Id].Value);
        Assert.Equal(PCCExecutive.Domain.DispatchState.GENERATING, restored.Dispatches.Single().State);
    }

    [Fact]
    public async Task Crash_after_browser_submission_fence_promotes_prepared_to_submitted_unknown()
    {
        await using var store = await NewStoreAsync("uncertain.db");
        var adapter = new SqliteOrchestrationStateStore(store);
        var run = Run(ProjectRunState.Dispatching);
        var task = WorkerTaskOf("uncertain");
        var wave = new Wave(WaveId.New(), run.Id, 1, WaveState.Dispatching, [task.Id], DateTimeOffset.UtcNow);
        var dispatch = Dispatch(run.Id, wave.Id, task.Id, PCCExecutive.Domain.DispatchState.PREPARED);
        await adapter.SaveAsync(new OrchestrationRecoverySnapshot(run, wave, [task], new Dictionary<TaskId, WorkerSlotId> { [task.Id] = new(1) }, [dispatch], null, OrchestrationPhase.Dispatching, DateTimeOffset.UtcNow));
        await store.ReserveAsync(dispatch.Id.ToString(), dispatch.ContentHash);
        await store.UpdateAsync(dispatch.Id.ToString(), PCCExecutive.Browser.DispatchState.Submitting, "adapter-entered");
        var recovery = new DurableStartupRecoveryService(store, adapter);
        var restored = await recovery.ReconstructAsync(run.Id);
        Assert.NotNull(restored);
        Assert.Equal(PCCExecutive.Domain.DispatchState.SUBMITTED_UNKNOWN, restored!.Dispatches.Single().State);
        Assert.Contains("recovered-browser-ledger:Submitting", restored.Dispatches.Single().ReconciliationEvidence);
    }

    [Fact]
    public async Task Clean_restart_is_distinguished_from_interrupted_restart()
    {
        await using var store = await NewStoreAsync("shutdown.db");
        var adapter = new SqliteOrchestrationStateStore(store);
        var run = Run(ProjectRunState.ManagerPlanning);
        await adapter.SaveAsync(new OrchestrationRecoverySnapshot(run, null, [], new Dictionary<TaskId, WorkerSlotId>(), [], null, OrchestrationPhase.ManagerPlanning, DateTimeOffset.UtcNow));
        var recovery = new DurableStartupRecoveryService(store, adapter);
        await recovery.MarkCleanShutdownAsync(run.Id);
        Assert.Equal(RecoveryStartupKind.CLEAN_SHUTDOWN, await recovery.BeginStartupAsync(run.Id));
        Assert.Equal(RecoveryStartupKind.INTERRUPTED_IDLE, await recovery.BeginStartupAsync(run.Id));
    }

    [Fact]
    public async Task Compact_checkpoint_verifies_hash_and_does_not_embed_transcript()
    {
        await using var store = await NewStoreAsync("checkpoint.db");
        var service = new RecoveryCheckpointService(store);
        var run = Run(ProjectRunState.ManagerReview);
        var checkpoint = await service.CreateAsync(run.Id, null, null, null, null, null, null, "branch", "head", "#1", "MANAGER_REVIEW", ["work"], ["blocker"], ["decision"], "next", "0.1.0", "MANAGER_CONTINUATION");
        var restored = await service.LoadAsync(checkpoint.CheckpointId);
        Assert.NotNull(restored);
        Assert.Equal("head", restored!.Head);
        var durable = await store.LoadCheckpointAsync(checkpoint.CheckpointId.ToString());
        Assert.DoesNotContain("chat transcript", durable!.Payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Successful_rollover_switches_domain_session_and_archives_predecessor()
    {
        await using var store = await NewStoreAsync("rollover.db");
        var run = Run(ProjectRunState.WaveRunning);
        var agentId = LogicalAgentId.New();
        var predecessorId = ConversationId.New();
        var predecessor = new PCCExecutive.Domain.Conversation(predecessorId, agentId, 1, AgentProviderKind.BrowserChat, "provider-1", "url-1", ConversationState.Active, DateTimeOffset.UtcNow, null, null, null, 1, 1, null, null);
        var session = new LogicalAgentSession(agentId, run.Id, AgentRole.Worker, new(1), null, predecessorId, LogicalSessionState.Active);
        await store.SaveConversationAsync(predecessor, run.Id);
        await store.SaveLogicalAgentAsync(session);
        var successorId = ConversationId.New();
        var browserPredecessor = Record(predecessorId, agentId, run.Id, 1, "url-1", ConversationLifecycleState.Active);
        var browserSuccessor = Record(successorId, agentId, run.Id, 2, "url-2", ConversationLifecycleState.Candidate) with { PredecessorConversationId = predecessorId.ToString() };
        var checkpoint = CheckpointId.New().ToString();
        var lifecycle = new DurableConversationLifecycleStore(store);
        await lifecycle.SaveCandidateAsync(browserSuccessor, checkpoint);
        await lifecycle.CommitRolloverAsync(browserPredecessor with { State = ConversationLifecycleState.Archived, SuccessorConversationId = successorId.ToString(), RetiredAt = DateTimeOffset.UtcNow }, browserSuccessor with { State = ConversationLifecycleState.Active }, checkpoint);
        var restoredSession = await store.LoadLogicalAgentAsync(agentId);
        Assert.Equal(successorId, restoredSession!.CurrentConversationId);
        Assert.Equal(ConversationState.Archived, (await store.LoadConversationAsync(predecessorId))!.State);
        Assert.Equal(ConversationState.Active, (await store.LoadConversationAsync(successorId))!.State);
    }

    [Fact]
    public async Task Failed_rollover_keeps_predecessor_active()
    {
        await using var store = await NewStoreAsync("failed-rollover.db");
        var run = Run(ProjectRunState.WaveRunning);
        var agentId = LogicalAgentId.New();
        var predecessorId = ConversationId.New();
        var predecessor = new PCCExecutive.Domain.Conversation(predecessorId, agentId, 1, AgentProviderKind.BrowserChat, "provider", "url", ConversationState.Active, DateTimeOffset.UtcNow, null, null, null, 1, 1, null, null);
        await store.SaveConversationAsync(predecessor, run.Id);
        var lifecycle = new DurableConversationLifecycleStore(store);
        await lifecycle.RecordFailedRolloverAsync(Record(predecessorId, agentId, run.Id, 1, "url", ConversationLifecycleState.Active), null, "validation failed");
        Assert.Equal(ConversationState.Active, (await store.LoadConversationAsync(predecessorId))!.State);
    }

    [Fact]
    public void Unknown_browser_is_not_adopted()
    {
        var run = Run(ProjectRunState.WaveRunning);
        var session = new LogicalAgentSession(LogicalAgentId.New(), run.Id, AgentRole.Worker, new(1), null, null, LogicalSessionState.Active);
        var runtime = new BrowserRuntimeRecord { RuntimeId = "foreign", ProjectRunId = run.Id.ToString(), LogicalAgentId = session.Id.ToString(), ProfilePath = "x", CreatedByPcc = false, AdoptedExplicitly = false, OwnershipNonce = "n", LastHeartbeatAt = DateTimeOffset.UtcNow, LastActivityAt = DateTimeOffset.UtcNow };
        var result = new BrowserSessionReconciliationService().Reconcile(session, runtime);
        Assert.Equal(BrowserReconciliationKind.UNKNOWN, result.Outcome);
    }

    [Fact]
    public void Browser_reconciliation_requires_canonical_conversation_uuid_format()
    {
        var run = Run(ProjectRunState.WaveRunning);
        var agent = LogicalAgentId.New();
        var conversation = ConversationId.New();
        var session = new LogicalAgentSession(agent, run.Id, AgentRole.Manager, null, null, conversation, LogicalSessionState.Active);
        var runtime = new BrowserRuntimeRecord
        {
            RuntimeId = "manager-runtime",
            ProjectRunId = run.Id.ToString(),
            LogicalAgentId = agent.ToString(),
            ConversationIdentity = conversation.Value.ToString("D"),
            ProfilePath = "owned",
            CreatedByPcc = true,
            OwnershipNonce = "nonce",
            LastHeartbeatAt = DateTimeOffset.UtcNow,
            LastActivityAt = DateTimeOffset.UtcNow
        };
        var reconciliation = new BrowserSessionReconciliationService();

        var nonCanonical = reconciliation.Reconcile(session, runtime);
        var canonical = reconciliation.Reconcile(session, runtime with { ConversationIdentity = conversation.ToString() });

        Assert.Equal(BrowserReconciliationKind.IDENTITY_MISMATCH, nonCanonical.Outcome);
        Assert.Equal(BrowserReconciliationKind.MATCHED, canonical.Outcome);
    }

    [Fact]
    public void Browser_reconciliation_keeps_a_genuine_conversation_uuid_mismatch_blocking()
    {
        var run = Run(ProjectRunState.WaveRunning);
        var agent = LogicalAgentId.New();
        var durableConversation = ConversationId.New();
        var session = new LogicalAgentSession(agent, run.Id, AgentRole.Manager, null, null, durableConversation, LogicalSessionState.Active);
        var runtime = new BrowserRuntimeRecord
        {
            RuntimeId = "manager-runtime",
            ProjectRunId = run.Id.ToString(),
            LogicalAgentId = agent.ToString(),
            ConversationIdentity = ConversationId.New().Value.ToString("D"),
            ProfilePath = "owned",
            CreatedByPcc = true,
            OwnershipNonce = "nonce",
            LastHeartbeatAt = DateTimeOffset.UtcNow,
            LastActivityAt = DateTimeOffset.UtcNow
        };

        var result = new BrowserSessionReconciliationService().Reconcile(session, runtime);

        Assert.Equal(BrowserReconciliationKind.IDENTITY_MISMATCH, result.Outcome);
    }

    [Fact]
    public async Task Pre_update_checkpoint_requires_verified_backup()
    {
        await using var store = await NewStoreAsync("update.db");
        var orchestration = new SqliteOrchestrationStateStore(store);
        var run = Run(ProjectRunState.ManagerPlanning);
        var snapshot = new OrchestrationRecoverySnapshot(run, null, [], new Dictionary<TaskId, WorkerSlotId>(), [], null, OrchestrationPhase.ManagerPlanning, DateTimeOffset.UtcNow);
        var gate = new FakeSendGate();
        var coordinator = new PreUpdateRecoveryCoordinator(gate, orchestration, store, new SqliteBackupService());
        var result = await coordinator.PrepareAsync(snapshot, Path.Combine(_root, "backup"), "0.1.0");
        Assert.True(result.SafeToUpdate);
        Assert.True(File.Exists(result.Backup.BackupPath));
        Assert.True(gate.Paused);
    }

    private async Task<SqliteStateStore> NewStoreAsync(string name)
    {
        var store = new SqliteStateStore(Path.Combine(_root, name));
        await store.InitializeAsync();
        return store;
    }

    private static ProjectRun Run(ProjectRunState state) => new(ProjectRunId.New(), ProjectId.New(), state, DateTimeOffset.UtcNow, new ManagerEstimate(50), new VerifiedCompletion(25), ProjectCompletionMode.Active);
    private static WorkerTask WorkerTaskOf(string suffix) => new(TaskId.New(), $"task-{suffix}", TaskScope.Create("owner/repo"), new HashSet<TaskId>(), ["done"], TaskState.Ready, $"fp-{suffix}");
    private static Dispatch Dispatch(ProjectRunId runId, WaveId waveId, TaskId taskId, PCCExecutive.Domain.DispatchState state)
    {
        var agent = LogicalAgentId.New();
        var conversation = ConversationId.New();
        return new Dispatch(DispatchId.New(), runId, waveId, taskId, agent, conversation, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, state, null, null, null, null, null);
    }
    private static ConversationRecord Record(ConversationId id, LogicalAgentId agentId, ProjectRunId runId, int sequence, string url, ConversationLifecycleState state) => new()
    {
        ConversationId = id.ToString(), LogicalAgentId = agentId.ToString(), ProjectRunId = runId.ToString(), Sequence = sequence,
        UrlOrProviderIdentity = url, CreatedAt = DateTimeOffset.UtcNow, State = state
    };

    private sealed class FakeSendGate : INewSendPausePort
    {
        public bool Paused { get; private set; }
        public Task PauseNewSendsAsync(string reason, CancellationToken cancellationToken = default) { Paused = true; return Task.CompletedTask; }
        public Task ResumeNewSendsAsync(string reason, CancellationToken cancellationToken = default) { Paused = false; return Task.CompletedTask; }
    }
}
