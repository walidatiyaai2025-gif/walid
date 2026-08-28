using System.Collections.Concurrent;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PCCExecutive.Application;
using PCCExecutive.Browser;
using PCCExecutive.Domain;

namespace PCCExecutive.Infrastructure;

public enum DurableCommitOutcome { COMMITTED, REPLAYED }
public sealed record DurableCommitResult(DurableCommitOutcome Outcome, long Revision, string PayloadSha256);

public sealed class StaleDurableWriteException : InvalidOperationException
{
    public StaleDurableWriteException(long expected, long actual) : base($"Stale canonical write rejected: expected revision {expected}, actual revision {actual}.")
    {
        ExpectedRevision = expected;
        ActualRevision = actual;
    }
    public long ExpectedRevision { get; }
    public long ActualRevision { get; }
}

public sealed class CrashConsistentOrchestrationStore : IOrchestrationStateStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly SqliteStateStore _store;
    private readonly SqliteDurabilityPolicy _policy;
    private readonly DurabilitySchemaManager _schema;

    public CrashConsistentOrchestrationStore(SqliteStateStore store, SqliteDurabilityPolicy? policy = null)
    {
        _store = store;
        _policy = policy ?? new();
        _schema = new DurabilitySchemaManager(store.DatabasePath, _policy);
    }

    public async Task SaveAsync(OrchestrationRecoverySnapshot snapshot, CancellationToken cancellationToken = default)
    {
        var merged = await DispatchMergedOrchestrationStateStore.MergeAsync(_store, snapshot, cancellationToken).ConfigureAwait(false);
        await CommitAsync(merged, "ORCHESTRATION_SNAPSHOT", $"snapshot:{merged.ProjectRun.Id}:{merged.SavedAt.UtcDateTime.Ticks}", new NoCrashFaultInjector(), cancellationToken).ConfigureAwait(false);
    }

    public async Task<OrchestrationRecoverySnapshot?> LoadAsync(ProjectRunId projectRunId, CancellationToken cancellationToken = default)
    {
        var snapshot = await new SqliteOrchestrationStateStore(_store).LoadAsync(projectRunId, cancellationToken).ConfigureAwait(false);
        return snapshot is null ? null : await DispatchMergedOrchestrationStateStore.MergeAsync(_store, snapshot, cancellationToken).ConfigureAwait(false);
    }

    public Task<DurableCommitResult> CreateWaveAsync(OrchestrationRecoverySnapshot snapshot, ICrashFaultInjector? faultInjector = null, CancellationToken cancellationToken = default)
    {
        if (snapshot.CurrentWave is null) throw new InvalidOperationException("Atomic Wave creation requires CurrentWave.");
        if (!snapshot.CurrentWave.TaskIds.OrderBy(x => x.ToString()).SequenceEqual(snapshot.Tasks.Select(x => x.Id).OrderBy(x => x.ToString())))
            throw new InvalidOperationException("Wave TaskIds and persisted Tasks must match atomically.");
        if (snapshot.Assignments.Keys.Any(x => snapshot.Tasks.All(t => t.Id != x)))
            throw new InvalidOperationException("Worker assignment references a task outside the Wave.");
        return CommitAsync(snapshot, "CREATE_WAVE", snapshot.CurrentWave.Id.ToString(), faultInjector ?? new NoCrashFaultInjector(), cancellationToken);
    }

    public Task<DurableCommitResult> IngestWorkerHandoffAsync(OrchestrationRecoverySnapshot snapshot, string handoffId, ICrashFaultInjector? faultInjector = null, CancellationToken cancellationToken = default) =>
        CommitAsync(snapshot, "WORKER_HANDOFF", RequireKey(handoffId, nameof(handoffId)), faultInjector ?? new NoCrashFaultInjector(), cancellationToken);

    public Task<DurableCommitResult> SaveManagerReviewAsync(OrchestrationRecoverySnapshot snapshot, string managerDecisionId, ICrashFaultInjector? faultInjector = null, CancellationToken cancellationToken = default)
    {
        if (snapshot.ManagerReview is null) throw new InvalidOperationException("Manager review snapshot must contain ManagerReview.");
        return CommitAsync(snapshot, "MANAGER_REVIEW", RequireKey(managerDecisionId, nameof(managerDecisionId)), faultInjector ?? new NoCrashFaultInjector(), cancellationToken);
    }

    public Task<DurableCommitResult> CommitExpectedRevisionAsync(OrchestrationRecoverySnapshot snapshot, string operationKind, string idempotencyKey, long expectedRevision, ICrashFaultInjector? faultInjector = null, CancellationToken cancellationToken = default) =>
        CommitCoreAsync(snapshot, operationKind, idempotencyKey, faultInjector ?? new NoCrashFaultInjector(), expectedRevision, cancellationToken);

    public Task<DurableCommitResult> CommitAsync(OrchestrationRecoverySnapshot snapshot, string operationKind, string idempotencyKey, ICrashFaultInjector faultInjector, CancellationToken cancellationToken = default) =>
        CommitCoreAsync(snapshot, operationKind, idempotencyKey, faultInjector, null, cancellationToken);

    private async Task<DurableCommitResult> CommitCoreAsync(OrchestrationRecoverySnapshot snapshot, string operationKind, string idempotencyKey, ICrashFaultInjector faultInjector, long? expectedRevision, CancellationToken cancellationToken)
    {
        await _schema.InitializeMetadataAsync(cancellationToken).ConfigureAwait(false);
        if (await _schema.ClassifyAsync(cancellationToken).ConfigureAwait(false) == SchemaCompatibility.UPGRADE_REQUIRED)
            await _schema.MigrateAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var payload = Persist(snapshot);
        var hash = Hash(payload);
        var gate = Gates.GetOrAdd(Path.GetFullPath(_store.DatabasePath), static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await WithBusyRetryAsync(async () =>
            {
                faultInjector.Hit(CrashFaultPoint.BEFORE_BEGIN);
                await using var connection = await SqliteDurabilityConnection.OpenAsync(_store.DatabasePath, _policy, cancellationToken: cancellationToken).ConfigureAwait(false);
                await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
                faultInjector.Hit(CrashFaultPoint.AFTER_BEGIN);

                var existing = await ReadOperationAsync(connection, transaction, operationKind, idempotencyKey, cancellationToken).ConfigureAwait(false);
                if (existing is not null)
                {
                    if (!string.Equals(existing.Value.Hash, hash, StringComparison.Ordinal)) throw new InvalidOperationException("Idempotency key reused with different canonical state.");
                    transaction.Rollback();
                    return new DurableCommitResult(DurableCommitOutcome.REPLAYED, existing.Value.Revision, hash);
                }

                var currentRevision = await CurrentRevisionAsync(connection, transaction, snapshot.ProjectRun.Id, cancellationToken).ConfigureAwait(false);
                if (expectedRevision is not null && currentRevision != expectedRevision.Value)
                    throw new StaleDurableWriteException(expectedRevision.Value, currentRevision);
                var revision = checked(currentRevision + 1);
                await WriteOperationAsync(connection, transaction, operationKind, idempotencyKey, snapshot.ProjectRun.Id, hash, revision, "RUNNING", cancellationToken).ConfigureAwait(false);
                faultInjector.Hit(CrashFaultPoint.AFTER_FIRST_WRITE);
                await WriteSnapshotAsync(connection, transaction, snapshot, payload, cancellationToken).ConfigureAwait(false);
                await WriteRevisionAsync(connection, transaction, snapshot.ProjectRun.Id, revision, cancellationToken).ConfigureAwait(false);
                await WriteOperationAsync(connection, transaction, operationKind, idempotencyKey, snapshot.ProjectRun.Id, hash, revision, "COMMITTED", cancellationToken).ConfigureAwait(false);
                faultInjector.Hit(CrashFaultPoint.BEFORE_COMMIT);
                transaction.Commit();
                faultInjector.Hit(CrashFaultPoint.AFTER_COMMIT);
                return new DurableCommitResult(DurableCommitOutcome.COMMITTED, revision, hash);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<T> WithBusyRetryAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return await action().ConfigureAwait(false); }
            catch (SqliteException ex) when ((ex.SqliteErrorCode == 5 || ex.SqliteErrorCode == 6) && attempt < _policy.BusyRetryCount)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25 * (attempt + 1)), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static string Persist(OrchestrationRecoverySnapshot snapshot) => JsonSerializer.Serialize(new PersistedOrchestrationSnapshot(
        snapshot.ProjectRun, snapshot.CurrentWave, snapshot.Tasks,
        snapshot.Assignments.Select(x => new PersistedAssignment(x.Key, x.Value)).ToArray(),
        snapshot.Dispatches, snapshot.ManagerReview, snapshot.Phase, snapshot.SavedAt), Json);

    private static string Hash(string payload) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    private static string RequireKey(string value, string name) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Idempotency key is required.", name) : value.Trim();

    private static async Task<(string Hash, long Revision)?> ReadOperationAsync(SqliteConnection connection, SqliteTransaction transaction, string kind, string key, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT payload_sha256,revision FROM durability_operations WHERE operation_kind=$kind AND idempotency_key=$key;";
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$key", key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? (reader.GetString(0), reader.GetInt64(1)) : null;
    }

    private static async Task<long> CurrentRevisionAsync(SqliteConnection connection, SqliteTransaction transaction, ProjectRunId projectRunId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT revision FROM orchestration_revisions WHERE project_run_id=$run;";
        command.Parameters.AddWithValue("$run", projectRunId.ToString());
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? 0L : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task WriteRevisionAsync(SqliteConnection connection, SqliteTransaction transaction, ProjectRunId projectRunId, long revision, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO orchestration_revisions(project_run_id,revision,updated_at) VALUES($run,$revision,$at) ON CONFLICT(project_run_id) DO UPDATE SET revision=excluded.revision,updated_at=excluded.updated_at;";
        command.Parameters.AddWithValue("$run", projectRunId.ToString());
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteOperationAsync(SqliteConnection connection, SqliteTransaction transaction, string kind, string key, ProjectRunId runId, string hash, long revision, string status, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO durability_operations(operation_kind,idempotency_key,project_run_id,payload_sha256,revision,status,created_at,committed_at) VALUES($kind,$key,$run,$hash,$revision,$status,$at,$committed) ON CONFLICT(operation_kind,idempotency_key) DO UPDATE SET status=excluded.status,committed_at=excluded.committed_at;";
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$run", runId.ToString());
        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$committed", status == "COMMITTED" ? DateTimeOffset.UtcNow.ToString("O") : DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteSnapshotAsync(SqliteConnection connection, SqliteTransaction transaction, OrchestrationRecoverySnapshot snapshot, string payload, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO checkpoints(checkpoint_id,project_run_id,kind,payload,created_at) VALUES($id,$run,'orchestration-snapshot-v1',$payload,$at) ON CONFLICT(checkpoint_id) DO UPDATE SET project_run_id=excluded.project_run_id,kind=excluded.kind,payload=excluded.payload,created_at=excluded.created_at;";
        command.Parameters.AddWithValue("$id", SqliteOrchestrationStateStore.SnapshotId(snapshot.ProjectRun.Id));
        command.Parameters.AddWithValue("$run", snapshot.ProjectRun.Id.ToString());
        command.Parameters.AddWithValue("$payload", payload);
        command.Parameters.AddWithValue("$at", snapshot.SavedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
