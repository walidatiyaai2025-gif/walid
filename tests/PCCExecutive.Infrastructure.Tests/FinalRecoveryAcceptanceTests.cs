using PCCExecutive.Application;
using PCCExecutive.Domain;
using PCCExecutive.Infrastructure;
using Xunit;

namespace PCCExecutive.Infrastructure.Tests;

public sealed class FinalRecoveryAcceptanceTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pcc-final-recovery", Guid.NewGuid().ToString("N"));

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
    public async Task Startup_reconstruction_preserves_project_wave_assignment_agent_task_and_conversation_bindings()
    {
        await using var store = await NewStoreAsync("startup-reconstruct.db");
        var orchestration = new SqliteOrchestrationStateStore(store);
        var run = new ProjectRun(ProjectRunId.New(), ProjectId.New(), ProjectRunState.WaveRunning, DateTimeOffset.UtcNow, new ManagerEstimate(61m), new VerifiedCompletion(47m), ProjectCompletionMode.Active);
        var task = new WorkerTask(TaskId.New(), "durable-worker-task", TaskScope.Create("owner/repo"), new HashSet<TaskId>(), ["accepted"], TaskState.Running, "fp-durable-worker");
        var wave = new Wave(WaveId.New(), run.Id, 4, WaveState.Running, [task.Id], DateTimeOffset.UtcNow);
        var agentId = LogicalAgentId.New();
        var conversationId = ConversationId.New();
        var session = new LogicalAgentSession(agentId, run.Id, AgentRole.Worker, new WorkerSlotId(4), task.Id, conversationId, LogicalSessionState.Active);
        var conversation = new PCCExecutive.Domain.Conversation(conversationId, agentId, 3, AgentProviderKind.BrowserChat, "provider-conversation", "https://chatgpt.com/c/provider-conversation", ConversationState.Active, DateTimeOffset.UtcNow, null, null, null, 1d, 12, null, "same-lineage");
        var snapshot = new OrchestrationRecoverySnapshot(run, wave, [task], new Dictionary<TaskId, WorkerSlotId> { [task.Id] = new WorkerSlotId(4) }, [], null, OrchestrationPhase.WaveRunning, DateTimeOffset.UtcNow);

        await store.SaveProjectRunAsync(run);
        await store.SaveTaskAsync(task, run.Id);
        await store.SaveWaveAsync(wave);
        await store.SaveLogicalAgentAsync(session);
        await store.SaveConversationAsync(conversation, run.Id);
        await orchestration.SaveAsync(snapshot);

        var startup = new DurableStartupRecoveryService(store, orchestration);
        _ = await startup.BeginStartupAsync(run.Id);
        var reconstructed = await startup.ReconstructAsync(run.Id);
        var full = await new FullDurabilityRecoveryService(store, orchestration).ReconstructAsync(run.Id);

        Assert.NotNull(reconstructed);
        Assert.Equal(run.Id, reconstructed!.ProjectRun.Id);
        Assert.Equal(wave.Id, reconstructed.CurrentWave!.Id);
        Assert.Equal(task.Id, reconstructed.Tasks.Single().Id);
        Assert.Equal(4, reconstructed.Assignments[task.Id].Value);
        Assert.NotNull(full);
        var restoredSession = Assert.Single(full!.LogicalSessions);
        Assert.Equal(LogicalSessionState.Active, restoredSession.State);
        Assert.Equal(task.Id, restoredSession.CurrentTaskId);
        Assert.Equal(conversationId, restoredSession.CurrentConversationId);
        Assert.Equal(4, restoredSession.WorkerSlotId!.Value.Value);
        var restoredConversation = Assert.Single(full.Conversations);
        Assert.Equal(ConversationState.Active, restoredConversation.State);
        Assert.Equal("provider-conversation", restoredConversation.ProviderIdentity);
    }

    [Fact]
    public async Task Safe_shutdown_persists_snapshot_flushes_and_distinguishes_clean_from_next_interrupted_start()
    {
        await using var store = await NewStoreAsync("safe-shutdown.db");
        var orchestration = new SqliteOrchestrationStateStore(store);
        var run = new ProjectRun(ProjectRunId.New(), ProjectId.New(), ProjectRunState.ManagerPlanning, DateTimeOffset.UtcNow, new ManagerEstimate(10m), new VerifiedCompletion(5m), ProjectCompletionMode.Active);
        var snapshot = new OrchestrationRecoverySnapshot(run, null, [], new Dictionary<TaskId, WorkerSlotId>(), [], null, OrchestrationPhase.ManagerPlanning, DateTimeOffset.UtcNow);
        var gate = new FakeSendGate();
        var startup = new DurableStartupRecoveryService(store, orchestration);
        var shutdown = new SafeShutdownCoordinator(gate, new RecoveryCheckpointService(store), startup, orchestration, store);

        await shutdown.ShutdownAsync(snapshot, "0.1.0");

        Assert.True(gate.Paused);
        Assert.Equal(RecoveryStartupKind.CLEAN_SHUTDOWN, await startup.BeginStartupAsync(run.Id));
        Assert.Equal(RecoveryStartupKind.INTERRUPTED_IDLE, await startup.BeginStartupAsync(run.Id));
        var restored = await startup.ReconstructAsync(run.Id);
        Assert.NotNull(restored);
        Assert.Equal(ProjectRunState.ManagerPlanning, restored!.ProjectRun.State);
    }

    [Fact]
    public async Task Conversation_invariant_reports_exactly_one_active_after_atomic_rollover_commit()
    {
        await using var store = await NewStoreAsync("exactly-one.db");
        var orchestration = new SqliteOrchestrationStateStore(store);
        var run = new ProjectRun(ProjectRunId.New(), ProjectId.New(), ProjectRunState.WaveRunning, DateTimeOffset.UtcNow, new ManagerEstimate(50m), new VerifiedCompletion(50m), ProjectCompletionMode.Active);
        await orchestration.SaveAsync(new OrchestrationRecoverySnapshot(run, null, [], new Dictionary<TaskId, WorkerSlotId>(), [], null, OrchestrationPhase.WaveRunning, DateTimeOffset.UtcNow));
        var agent = LogicalAgentId.New();
        var predecessor = ConversationId.New();
        var successor = ConversationId.New();
        await store.SaveConversationAsync(new PCCExecutive.Domain.Conversation(predecessor, agent, 1, AgentProviderKind.BrowserChat, "provider-old", "url-old", ConversationState.Archived, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow, null, successor, 1d, 10, null, "rollover"), run.Id);
        await store.SaveConversationAsync(new PCCExecutive.Domain.Conversation(successor, agent, 2, AgentProviderKind.BrowserChat, "provider-new", "url-new", ConversationState.Active, DateTimeOffset.UtcNow, null, predecessor, null, 1d, 1, null, "rollover"), run.Id);

        var invariant = new ConversationInvariantService(new FullDurabilityRecoveryService(store, orchestration));

        Assert.True(await invariant.ExactlyOneActiveAsync(run.Id, agent));
    }

    private async Task<SqliteStateStore> NewStoreAsync(string file)
    {
        var store = new SqliteStateStore(Path.Combine(_root, file));
        await store.InitializeAsync();
        return store;
    }

    private sealed class FakeSendGate : INewSendPausePort
    {
        public bool Paused { get; private set; }
        public Task PauseNewSendsAsync(string reason, CancellationToken cancellationToken = default)
        {
            Paused = true;
            return Task.CompletedTask;
        }

        public Task ResumeNewSendsAsync(string reason, CancellationToken cancellationToken = default)
        {
            Paused = false;
            return Task.CompletedTask;
        }
    }
}
