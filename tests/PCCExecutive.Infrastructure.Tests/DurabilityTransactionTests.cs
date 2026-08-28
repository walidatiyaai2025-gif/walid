using PCCExecutive.Application;
using PCCExecutive.Browser;
using PCCExecutive.Domain;
using PCCExecutive.Infrastructure;
using Xunit;

namespace PCCExecutive.Infrastructure.Tests;

public sealed class DurabilityTransactionTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pcc-durability-txn", Guid.NewGuid().ToString("N"));
    public Task InitializeAsync() { Directory.CreateDirectory(_root); return Task.CompletedTask; }
    public Task DisposeAsync() { try { Directory.Delete(_root, true); } catch { } return Task.CompletedTask; }

    [Fact]
    public async Task Atomic_wave_creation()
    {
        await using var store = await DurabilityTestFixture.NewStoreAsync(_root, "wave.db");
        var durable = new CrashConsistentOrchestrationStore(store);
        var snapshot = DurabilityTestFixture.Wave(5);
        await durable.CreateWaveAsync(snapshot);
        var restored = await durable.LoadAsync(snapshot.ProjectRun.Id);
        Assert.Equal(5, restored!.Tasks.Count);
        Assert.Equal(5, restored.Assignments.Count);
        Assert.All(restored.Dispatches, d => Assert.Equal(PCCExecutive.Domain.DispatchState.PREPARED, d.State));
    }

    [Fact]
    public async Task Crash_before_wave_commit_rolls_back_everything()
    {
        await using var store = await DurabilityTestFixture.NewStoreAsync(_root, "wave-crash.db");
        var durable = new CrashConsistentOrchestrationStore(store);
        var snapshot = DurabilityTestFixture.Wave(3);
        await Assert.ThrowsAsync<InjectedCrashException>(() => durable.CreateWaveAsync(snapshot, new DeterministicCrashFaultInjector(CrashFaultPoint.BEFORE_COMMIT)));
        Assert.Null(await durable.LoadAsync(snapshot.ProjectRun.Id));
    }

    [Fact]
    public async Task Atomic_worker_handoff_updates_task_and_dispatch_together()
    {
        await using var store = await DurabilityTestFixture.NewStoreAsync(_root, "handoff.db");
        var durable = new CrashConsistentOrchestrationStore(store);
        var initial = DurabilityTestFixture.Wave(1);
        await durable.CreateWaveAsync(initial);
        var updated = initial with
        {
            Tasks = [initial.Tasks[0] with { State = TaskState.HandoffReceived }],
            Dispatches = [initial.Dispatches[0] with { State = PCCExecutive.Domain.DispatchState.COMPLETED, CompletedAt = DateTimeOffset.UtcNow }],
            SavedAt = DateTimeOffset.UtcNow
        };
        await durable.IngestWorkerHandoffAsync(updated, "handoff-1");
        var restored = await durable.LoadAsync(initial.ProjectRun.Id);
        Assert.Equal(TaskState.HandoffReceived, restored!.Tasks[0].State);
        Assert.Equal(PCCExecutive.Domain.DispatchState.COMPLETED, restored.Dispatches[0].State);
    }

    [Fact]
    public async Task Duplicate_handoff_replay_is_idempotent()
    {
        await using var store = await DurabilityTestFixture.NewStoreAsync(_root, "handoff-replay.db");
        var durable = new CrashConsistentOrchestrationStore(store);
        var snapshot = DurabilityTestFixture.Wave(1);
        var first = await durable.IngestWorkerHandoffAsync(snapshot, "handoff-same");
        var replay = await durable.IngestWorkerHandoffAsync(snapshot, "handoff-same");
        Assert.Equal(DurableCommitOutcome.COMMITTED, first.Outcome);
        Assert.Equal(DurableCommitOutcome.REPLAYED, replay.Outcome);
        Assert.Equal(first.Revision, replay.Revision);
    }

    [Fact]
    public async Task Manager_decision_replay_is_idempotent()
    {
        await using var store = await DurabilityTestFixture.NewStoreAsync(_root, "manager-replay.db");
        var durable = new CrashConsistentOrchestrationStore(store);
        var snapshot = DurabilityTestFixture.Wave(1);
        var first = await durable.CommitAsync(snapshot, "MANAGER_REVIEW", "decision-1", new NoCrashFaultInjector());
        var replay = await durable.CommitAsync(snapshot, "MANAGER_REVIEW", "decision-1", new NoCrashFaultInjector());
        Assert.Equal(DurableCommitOutcome.COMMITTED, first.Outcome);
        Assert.Equal(DurableCommitOutcome.REPLAYED, replay.Outcome);
    }

    [Fact]
    public async Task Crash_after_commit_recovers_committed_state()
    {
        await using var store = await DurabilityTestFixture.NewStoreAsync(_root, "after-commit.db");
        var durable = new CrashConsistentOrchestrationStore(store);
        var snapshot = DurabilityTestFixture.Wave(2);
        await Assert.ThrowsAsync<InjectedCrashException>(() => durable.CreateWaveAsync(snapshot, new DeterministicCrashFaultInjector(CrashFaultPoint.AFTER_COMMIT)));
        Assert.Equal(2, (await durable.LoadAsync(snapshot.ProjectRun.Id))!.Tasks.Count);
    }

    [Fact]
    public async Task Concurrent_worker_writes_are_serialized()
    {
        await using var store = await DurabilityTestFixture.NewStoreAsync(_root, "concurrent.db");
        var durable = new CrashConsistentOrchestrationStore(store);
        var run = DurabilityTestFixture.Wave(1).ProjectRun;
        var writes = Enumerable.Range(1, 5).Select(i => durable.CommitAsync(DurabilityTestFixture.Wave(1, run) with { SavedAt = DateTimeOffset.UtcNow.AddTicks(i) }, "WORKER_WRITE", $"worker-{i}", new NoCrashFaultInjector()));
        var results = await Task.WhenAll(writes);
        Assert.Equal(5, results.Select(x => x.Revision).Distinct().Count());
    }

    [Fact]
    public async Task Concurrent_ui_reads_are_safe_while_state_exists()
    {
        await using var store = await DurabilityTestFixture.NewStoreAsync(_root, "reads.db");
        var durable = new CrashConsistentOrchestrationStore(store);
        var snapshot = DurabilityTestFixture.Wave(1);
        await durable.CreateWaveAsync(snapshot);
        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => durable.LoadAsync(snapshot.ProjectRun.Id)));
        Assert.All(results, Assert.NotNull);
    }

    [Fact]
    public async Task Stale_writer_is_rejected()
    {
        await using var store = await DurabilityTestFixture.NewStoreAsync(_root, "stale.db");
        var durable = new CrashConsistentOrchestrationStore(store);
        var snapshot = DurabilityTestFixture.Wave(1);
        var first = await durable.CommitExpectedRevisionAsync(snapshot, "CAS", "first", 0);
        Assert.Equal(1, first.Revision);
        await Assert.ThrowsAsync<StaleDurableWriteException>(() => durable.CommitExpectedRevisionAsync(snapshot with { SavedAt = DateTimeOffset.UtcNow }, "CAS", "stale", 0));
    }

    [Fact]
    public async Task Idempotency_key_cannot_be_reused_for_different_payload()
    {
        await using var store = await DurabilityTestFixture.NewStoreAsync(_root, "idempotency.db");
        var durable = new CrashConsistentOrchestrationStore(store);
        var snapshot = DurabilityTestFixture.Wave(1);
        await durable.CommitAsync(snapshot, "HANDOFF", "same-key", new NoCrashFaultInjector());
        var changed = snapshot with { ProjectRun = snapshot.ProjectRun with { ManagerEstimate = new ManagerEstimate(99) }, SavedAt = DateTimeOffset.UtcNow };
        await Assert.ThrowsAsync<InvalidOperationException>(() => durable.CommitAsync(changed, "HANDOFF", "same-key", new NoCrashFaultInjector()));
    }
}
