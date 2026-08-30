using Microsoft.Data.Sqlite;
using PCCExecutive.Application;
using PCCExecutive.Browser;
using PCCExecutive.Domain;
using PCCExecutive.Infrastructure;

namespace PCCExecutive.Infrastructure.Tests;

internal static class DurabilityTestFixture
{
    public static async Task<SqliteStateStore> NewStoreAsync(string root, string name)
    {
        Directory.CreateDirectory(root);
        var store = new SqliteStateStore(Path.Combine(root, name));
        await store.InitializeAsync();
        return store;
    }

    public static OrchestrationRecoverySnapshot Wave(int count, ProjectRun? existingRun = null)
    {
        var run = existingRun ?? new ProjectRun(ProjectRunId.New(), ProjectId.New(), ProjectRunState.Dispatching, DateTimeOffset.UtcNow, new ManagerEstimate(25), new VerifiedCompletion(10), ProjectCompletionMode.Active);
        var tasks = Enumerable.Range(1, count).Select(i => new WorkerTask(TaskId.New(), $"task-{i}", TaskScope.Create("owner/repo", paths: [$"src/{i}"]), new HashSet<TaskId>(), ["done"], TaskState.Assigned, $"fp-{i}-{Guid.NewGuid():N}")).ToArray();
        var wave = new Wave(WaveId.New(), run.Id, 1, WaveState.Dispatching, tasks.Select(x => x.Id).ToArray(), DateTimeOffset.UtcNow);
        var assignments = tasks.Select((x, i) => (x.Id, Slot: new WorkerSlotId(i + 1))).ToDictionary(x => x.Id, x => x.Slot);
        var dispatches = tasks.Select((task, i) => new Dispatch(DispatchId.New(), run.Id, wave.Id, task.Id, LogicalAgentId.New(), ConversationId.New(), $"hash-{i}-{Guid.NewGuid():N}", DateTimeOffset.UtcNow, PCCExecutive.Domain.DispatchState.PREPARED, null, null, null, null, null)).ToArray();
        return new(run, wave, tasks, assignments, dispatches, null, OrchestrationPhase.Dispatching, DateTimeOffset.UtcNow);
    }

    public static BrowserRuntimeRecord Runtime(string id, ProjectRunId runId, LogicalAgentId agentId, bool createdByPcc, bool adopted) => new()
    {
        RuntimeId = id,
        ProjectRunId = runId.ToString(),
        LogicalAgentId = agentId.ToString(),
        ProfilePath = "profile",
        CreatedByPcc = createdByPcc,
        AdoptedExplicitly = adopted,
        OwnershipNonce = "nonce",
        LastHeartbeatAt = DateTimeOffset.UtcNow,
        LastActivityAt = DateTimeOffset.UtcNow
    };

    public static async Task AgeCheckpointAsync(string path, string checkpointId, DateTimeOffset createdAt)
    {
        await using var connection = new SqliteConnection(SqliteDurabilityConnection.ConnectionString(path));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE checkpoints SET created_at=$at WHERE checkpoint_id=$id;";
        command.Parameters.AddWithValue("$at", createdAt.ToString("O"));
        command.Parameters.AddWithValue("$id", checkpointId);
        await command.ExecuteNonQueryAsync();
    }
}

internal sealed class FakeNewSendGate : INewSendPausePort
{
    public bool Paused { get; private set; }
    public Task PauseNewSendsAsync(string reason, CancellationToken cancellationToken = default) { Paused = true; return Task.CompletedTask; }
    public Task ResumeNewSendsAsync(string reason, CancellationToken cancellationToken = default) { Paused = false; return Task.CompletedTask; }
}
