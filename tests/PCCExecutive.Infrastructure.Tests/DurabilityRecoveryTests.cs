using PCCExecutive.Application;
using PCCExecutive.Browser;
using PCCExecutive.Domain;
using PCCExecutive.Infrastructure;
using Xunit;

namespace PCCExecutive.Infrastructure.Tests;

public sealed class DurabilityRecoveryTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pcc-durability-recovery", Guid.NewGuid().ToString("N"));
    public Task InitializeAsync() { Directory.CreateDirectory(_root); return Task.CompletedTask; }
    public Task DisposeAsync() { try { Directory.Delete(_root, true); } catch { } return Task.CompletedTask; }

    [Fact]
    public async Task Crash_before_browser_send_keeps_prepared()
    {
        await using var store = await DurabilityTestFixture.NewStoreAsync(_root, "before-send.db");
        var adapter = new SqliteOrchestrationStateStore(store);
        var snapshot = DurabilityTestFixture.Wave(1);
        await adapter.SaveAsync(snapshot);
        var restored = await new DurableStartupRecoveryService(store, adapter).ReconstructAsync(snapshot.ProjectRun.Id);
        Assert.Equal(PCCExecutive.Domain.DispatchState.PREPARED, restored!.Dispatches[0].State);
    }

    [Fact]
    public async Task Crash_during_browser_send_recovers_submitted_unknown()
    {
        await using var store = await DurabilityTestFixture.NewStoreAsync(_root, "during-send.db");
        var adapter = new SqliteOrchestrationStateStore(store);
        var snapshot = DurabilityTestFixture.Wave(1);
        await adapter.SaveAsync(snapshot);
        var dispatch = snapshot.Dispatches[0];
        await store.ReserveAsync(dispatch.Id.ToString(), dispatch.ContentHash);
        await store.UpdateAsync(dispatch.Id.ToString(), PCCExecutive.Browser.DispatchState.Submitting, "send-entered");
        var restored = await new DurableStartupRecoveryService(store, adapter).ReconstructAsync(snapshot.ProjectRun.Id);
        Assert.Equal(PCCExecutive.Domain.DispatchState.SUBMITTED_UNKNOWN, restored!.Dispatches[0].State);
    }

    [Fact]
    public async Task Crash_after_browser_send_before_ack_never_returns_to_prepared()
    {
        await using var store = await DurabilityTestFixture.NewStoreAsync(_root, "after-send.db");
        var adapter = new SqliteOrchestrationStateStore(store);
        var snapshot = DurabilityTestFixture.Wave(1);
        await adapter.SaveAsync(snapshot);
        var dispatch = snapshot.Dispatches[0];
        await store.ReserveAsync(dispatch.Id.ToString(), dispatch.ContentHash);
        await store.UpdateAsync(dispatch.Id.ToString(), PCCExecutive.Browser.DispatchState.Submitted, "submission-proven");
        var restored = await new DurableStartupRecoveryService(store, adapter).ReconstructAsync(snapshot.ProjectRun.Id);
        Assert.Equal(PCCExecutive.Domain.DispatchState.SUBMITTED, restored!.Dispatches[0].State);
    }

    [Fact]
    public async Task Submitted_unknown_survives_restart_with_identity_and_hash()
    {
        await using var store = await DurabilityTestFixture.NewStoreAsync(_root, "unknown.db");
        var adapter = new SqliteOrchestrationStateStore(store);
        var snapshot = DurabilityTestFixture.Wave(1);
        var uncertain = snapshot.Dispatches[0] with { State = PCCExecutive.Domain.DispatchState.SUBMITTED_UNKNOWN };
        snapshot = snapshot with { Dispatches = [uncertain] };
        await adapter.SaveAsync(snapshot);
        var restored = await new DurableStartupRecoveryService(store, adapter).ReconstructAsync(snapshot.ProjectRun.Id);
        Assert.Equal(uncertain.Id, restored!.Dispatches[0].Id);
        Assert.Equal(uncertain.ContentHash, restored.Dispatches[0].ContentHash);
        Assert.Equal(uncertain.ConversationId, restored.Dispatches[0].ConversationId);
        Assert.Equal(PCCExecutive.Domain.DispatchState.SUBMITTED_UNKNOWN, restored.Dispatches[0].State);
    }

    [Fact]
    public async Task Five_worker_restart_preserves_mixed_states_slots_tasks_dispatches_and_conversations()
    {
        await using var store = await DurabilityTestFixture.NewStoreAsync(_root, "five.db");
        var durable = new CrashConsistentOrchestrationStore(store);
        var snapshot = DurabilityTestFixture.Wave(5);
        var tasks = snapshot.Tasks.ToArray();
        tasks[0] = tasks[0] with { State = TaskState.Completed };
        tasks[1] = tasks[1] with { State = TaskState.Running };
        tasks[2] = tasks[2] with { State = TaskState.Dispatched };
        tasks[3] = tasks[3] with { State = TaskState.Blocked };
        tasks[4] = tasks[4] with { State = TaskState.Assigned };
        var dispatches = snapshot.Dispatches.ToArray();
        dispatches[0] = dispatches[0] with { State = PCCExecutive.Domain.DispatchState.COMPLETED };
        dispatches[1] = dispatches[1] with { State = PCCExecutive.Domain.DispatchState.GENERATING };
        dispatches[2] = dispatches[2] with { State = PCCExecutive.Domain.DispatchState.SUBMITTED_UNKNOWN };
        dispatches[3] = dispatches[3] with { State = PCCExecutive.Domain.DispatchState.FAILED };
        dispatches[4] = dispatches[4] with { State = PCCExecutive.Domain.DispatchState.PREPARED };
        snapshot = snapshot with { Tasks = tasks, Dispatches = dispatches, SavedAt = DateTimeOffset.UtcNow };
        await durable.CreateWaveAsync(snapshot);
        for (var i = 0; i < 5; i++)
        {
            var agent = dispatches[i].LogicalAgentId;
            var conversation = new PCCExecutive.Domain.Conversation(dispatches[i].ConversationId, agent, 1, AgentProviderKind.BrowserChat, $"provider-{i}", $"url-{i}", ConversationState.Active, DateTimeOffset.UtcNow, null, null, null, 1, 1, null, null);
            await store.SaveConversationAsync(conversation, snapshot.ProjectRun.Id);
            await store.SaveLogicalAgentAsync(new LogicalAgentSession(agent, snapshot.ProjectRun.Id, AgentRole.Worker, new WorkerSlotId(i + 1), tasks[i].Id, conversation.Id, LogicalSessionState.Active));
        }
        var full = await new FullDurabilityRecoveryService(store, durable).ReconstructAsync(snapshot.ProjectRun.Id);
        Assert.Equal(5, full!.LogicalSessions.Count);
        Assert.Equal(5, full.Conversations.Count);
        Assert.Equal(PCCExecutive.Domain.DispatchState.COMPLETED, full.Orchestration.Dispatches[0].State);
        Assert.Equal(PCCExecutive.Domain.DispatchState.GENERATING, full.Orchestration.Dispatches[1].State);
        Assert.Equal(PCCExecutive.Domain.DispatchState.SUBMITTED_UNKNOWN, full.Orchestration.Dispatches[2].State);
        Assert.Equal(TaskState.Blocked, full.Orchestration.Tasks[3].State);
        Assert.Equal(PCCExecutive.Domain.DispatchState.PREPARED, full.Orchestration.Dispatches[4].State);
        for (var i = 0; i < 5; i++) Assert.Equal(i + 1, full.Orchestration.Assignments[tasks[i].Id].Value);
    }

    [Fact]
    public async Task Rollover_crash_before_active_switch_keeps_predecessor_canonical()
    {
        await using var store = await DurabilityTestFixture.NewStoreAsync(_root, "roll-before.db");
        var state = await RolloverStateAsync(store);
        var lifecycle = new DurableConversationLifecycleStore(store);
        await lifecycle.SaveCandidateAsync(state.Successor, state.Checkpoint);
        Assert.Equal(ConversationState.Active, (await store.LoadConversationAsync(state.PredecessorId))!.State);
        Assert.Equal(ConversationState.Fresh, (await store.LoadConversationAsync(state.SuccessorId))!.State);
        Assert.Equal(state.PredecessorId, (await store.LoadLogicalAgentAsync(state.AgentId))!.CurrentConversationId);
    }

    [Fact]
    public async Task Rollover_after_switch_has_exactly_one_active_conversation()
    {
        await using var store = await DurabilityTestFixture.NewStoreAsync(_root, "roll-after.db");
        var state = await RolloverStateAsync(store);
        var adapter = new SqliteOrchestrationStateStore(store);
        await adapter.SaveAsync(new OrchestrationRecoverySnapshot(new ProjectRun(state.RunId, ProjectId.New(), ProjectRunState.WaveRunning, DateTimeOffset.UtcNow, new ManagerEstimate(0), new VerifiedCompletion(0), ProjectCompletionMode.Active), null, [], new Dictionary<TaskId, WorkerSlotId>(), [], null, OrchestrationPhase.WaveRunning, DateTimeOffset.UtcNow));
        var lifecycle = new DurableConversationLifecycleStore(store);
        await lifecycle.SaveCandidateAsync(state.Successor, state.Checkpoint);
        await lifecycle.CommitRolloverAsync(state.Predecessor with { State = ConversationLifecycleState.Archived, SuccessorConversationId = state.SuccessorId.ToString(), RetiredAt = DateTimeOffset.UtcNow }, state.Successor with { State = ConversationLifecycleState.Active }, state.Checkpoint);
        var full = new FullDurabilityRecoveryService(store, adapter);
        Assert.True(await new ConversationInvariantService(full).ExactlyOneActiveAsync(state.RunId, state.AgentId));
        Assert.Equal(state.SuccessorId, (await store.LoadLogicalAgentAsync(state.AgentId))!.CurrentConversationId);
    }

    [Fact]
    public async Task Checkpoint_recompaction_is_non_recursive()
    {
        await using var store = await DurabilityTestFixture.NewStoreAsync(_root, "compact.db");
        var service = new RecoveryCheckpointService(store);
        var run = DurabilityTestFixture.Wave(1).ProjectRun;
        var source = await service.CreateAsync(run.Id, null, null, null, null, null, null, "branch", "head", "#19", "RUNNING", ["a", "a"], ["b", "b"], ["d", "d"], "next", "0.1.0", "TEST");
        var compact = await new CheckpointCompactionService(store, service).RecompactAsync(source.CheckpointId);
        Assert.Single(compact.CompletedWork);
        Assert.Single(compact.Blockers);
        Assert.Single(compact.ImportantDecisions);
        var raw = await store.LoadCheckpointAsync(compact.CheckpointId.ToString());
        Assert.DoesNotContain(source.CheckpointId.ToString(), raw!.Payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Safe_shutdown_marks_clean_restart_only_after_checkpoint_and_flush()
    {
        await using var store = await DurabilityTestFixture.NewStoreAsync(_root, "shutdown.db");
        var adapter = new SqliteOrchestrationStateStore(store);
        var snapshot = DurabilityTestFixture.Wave(1);
        await adapter.SaveAsync(snapshot);
        var recovery = new DurableStartupRecoveryService(store, adapter);
        var gate = new FakeNewSendGate();
        await new SafeShutdownCoordinator(gate, new RecoveryCheckpointService(store), recovery, adapter, store).ShutdownAsync(snapshot, "0.1.0");
        Assert.True(gate.Paused);
        Assert.Equal(RecoveryStartupKind.CLEAN_SHUTDOWN, await recovery.BeginStartupAsync(snapshot.ProjectRun.Id));
    }

    [Fact]
    public async Task Forced_termination_is_recoverable_and_not_clean_shutdown()
    {
        await using var store = await DurabilityTestFixture.NewStoreAsync(_root, "forced.db");
        var adapter = new SqliteOrchestrationStateStore(store);
        var snapshot = DurabilityTestFixture.Wave(1);
        await adapter.SaveAsync(snapshot);
        var recovery = new DurableStartupRecoveryService(store, adapter);
        Assert.Equal(RecoveryStartupKind.INTERRUPTED_DISPATCH, await recovery.BeginStartupAsync(snapshot.ProjectRun.Id));
        Assert.NotNull(await recovery.ReconstructAsync(snapshot.ProjectRun.Id));
    }

    [Fact]
    public async Task Retention_preserves_explicitly_protected_active_checkpoint()
    {
        await using var store = await DurabilityTestFixture.NewStoreAsync(_root, "retention.db");
        var service = new RecoveryCheckpointService(store);
        var run = DurabilityTestFixture.Wave(1).ProjectRun;
        var old = await service.CreateAsync(run.Id, null, null, null, null, null, null, null, null, null, "OLD", [], [], [], "old", "0.1.0", "TEST");
        var active = await service.CreateAsync(run.Id, null, null, null, null, null, null, null, null, null, "ACTIVE", [], [], [], "active", "0.1.0", "TEST");
        await DurabilityTestFixture.AgeCheckpointAsync(store.DatabasePath, old.CheckpointId.ToString(), DateTimeOffset.UtcNow.AddDays(-30));
        var deleted = await new CheckpointCompactionService(store, service).PruneOldRecoveryCheckpointsAsync(DateTimeOffset.UtcNow.AddDays(-7), new HashSet<string> { active.CheckpointId.ToString() });
        Assert.Equal(1, deleted);
        Assert.Null(await service.LoadAsync(old.CheckpointId));
        Assert.NotNull(await service.LoadAsync(active.CheckpointId));
    }

    [Fact]
    public void Unknown_browser_is_do_not_adopt_and_owned_orphan_is_classified()
    {
        var snapshot = DurabilityTestFixture.Wave(1);
        var session = new LogicalAgentSession(LogicalAgentId.New(), snapshot.ProjectRun.Id, AgentRole.Worker, new WorkerSlotId(1), null, null, LogicalSessionState.Active);
        var unknown = DurabilityTestFixture.Runtime("foreign", snapshot.ProjectRun.Id, session.Id, false, false);
        var orphan = DurabilityTestFixture.Runtime("orphan", snapshot.ProjectRun.Id, LogicalAgentId.New(), true, false);
        var results = new BrowserInventoryReconciliationService().Reconcile([session], [unknown, orphan]);
        Assert.Contains(results, x => x.Outcome == BrowserReconciliationKind.UNKNOWN && x.Reason.Contains("DO_NOT_ADOPT", StringComparison.Ordinal));
        Assert.Contains(results, x => x.Outcome == BrowserReconciliationKind.ORPHANED_OWNED_RUNTIME && x.RuntimeId == "orphan");
    }

    [Fact]
    public async Task Recovery_journal_records_required_semantic_event_types()
    {
        await using var store = await DurabilityTestFixture.NewStoreAsync(_root, "journal.db");
        await new DurabilitySchemaManager(store.DatabasePath).MigrateAsync();
        var journal = new RecoveryJournalService(store.DatabasePath);
        foreach (var kind in Enum.GetValues<RecoveryJournalKind>()) await journal.RecordAsync(kind, kind.ToString());
        var restored = await journal.ListAsync();
        Assert.Equal(Enum.GetValues<RecoveryJournalKind>(), restored);
    }

    [Fact]
    public void Privacy_guard_blocks_operational_secrets_and_allows_canonical_metadata()
    {
        var guard = new OperationalStatePrivacyGuard();
        guard.Validate("TASK=abc; HEAD=deadbeef; NEXT=continue");
        Assert.Throws<InvalidDataException>(() => guard.Validate("Author" + "ization: Bearer secret"));
        Assert.Throws<InvalidDataException>(() => guard.Validate("Coo" + "kie: session=secret"));
        Assert.Throws<InvalidDataException>(() => guard.Validate("pass" + "word=secret"));
        Assert.Throws<InvalidDataException>(() => guard.Validate("api" + "_key=secret"));
    }

    [Fact]
    public async Task Maintenance_is_blocked_during_critical_dispatch_transaction()
    {
        await using var store = await DurabilityTestFixture.NewStoreAsync(_root, "maintenance.db");
        var maintenance = new DurabilityMaintenanceService(store.DatabasePath);
        await Assert.ThrowsAsync<InvalidOperationException>(() => maintenance.OptimizeAsync(true));
        await maintenance.CheckpointAsync();
        await maintenance.OptimizeAsync(false);
    }

    private async Task<(ProjectRunId RunId, LogicalAgentId AgentId, ConversationId PredecessorId, ConversationId SuccessorId, ConversationRecord Predecessor, ConversationRecord Successor, string Checkpoint)> RolloverStateAsync(SqliteStateStore store)
    {
        var runId = ProjectRunId.New();
        var agentId = LogicalAgentId.New();
        var predecessorId = ConversationId.New();
        var successorId = ConversationId.New();
        await store.SaveConversationAsync(new PCCExecutive.Domain.Conversation(predecessorId, agentId, 1, AgentProviderKind.BrowserChat, "provider-1", "url-1", ConversationState.Active, DateTimeOffset.UtcNow, null, null, null, 1, 1, null, null), runId);
        await store.SaveLogicalAgentAsync(new LogicalAgentSession(agentId, runId, AgentRole.Worker, new WorkerSlotId(1), null, predecessorId, LogicalSessionState.Active));
        var predecessor = new ConversationRecord { ConversationId = predecessorId.ToString(), LogicalAgentId = agentId.ToString(), ProjectRunId = runId.ToString(), Sequence = 1, UrlOrProviderIdentity = "url-1", CreatedAt = DateTimeOffset.UtcNow, State = ConversationLifecycleState.Active };
        var successor = new ConversationRecord { ConversationId = successorId.ToString(), LogicalAgentId = agentId.ToString(), ProjectRunId = runId.ToString(), Sequence = 2, UrlOrProviderIdentity = "url-2", CreatedAt = DateTimeOffset.UtcNow, PredecessorConversationId = predecessorId.ToString(), State = ConversationLifecycleState.Candidate };
        return (runId, agentId, predecessorId, successorId, predecessor, successor, CheckpointId.New().ToString());
    }
}
