using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PCCExecutive.Browser;
using PCCExecutive.Domain;

namespace PCCExecutive.Infrastructure;

public sealed record PccExecutiveSettings(
    string Provider = "BrowserChat",
    string DispatchMode = "AutomaticStaged",
    int MaxWorkers = 5,
    int BaseDispatchIntervalSeconds = 10,
    bool AdaptivePacing = true,
    bool AutoResume = true);

public sealed record DurableCheckpoint(
    string Id,
    string ProjectRunId,
    string Kind,
    string Payload,
    DateTimeOffset CreatedAt);

public interface IDurableStateStore
{
    string DatabasePath { get; }
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<int> GetSchemaVersionAsync(CancellationToken cancellationToken = default);
    Task SaveProjectRunAsync(ProjectRun value, CancellationToken cancellationToken = default);
    Task<ProjectRun?> LoadProjectRunAsync(ProjectRunId id, CancellationToken cancellationToken = default);
    Task SaveWaveAsync(Wave value, CancellationToken cancellationToken = default);
    Task<Wave?> LoadWaveAsync(WaveId id, CancellationToken cancellationToken = default);
    Task SaveTaskAsync(WorkerTask value, ProjectRunId projectRunId, CancellationToken cancellationToken = default);
    Task<WorkerTask?> LoadTaskAsync(TaskId id, CancellationToken cancellationToken = default);
    Task SaveLogicalAgentAsync(LogicalAgentSession value, CancellationToken cancellationToken = default);
    Task<LogicalAgentSession?> LoadLogicalAgentAsync(LogicalAgentId id, CancellationToken cancellationToken = default);
    Task SaveConversationAsync(PCCExecutive.Domain.Conversation value, ProjectRunId projectRunId, CancellationToken cancellationToken = default);
    Task<PCCExecutive.Domain.Conversation?> LoadConversationAsync(ConversationId id, CancellationToken cancellationToken = default);
    Task SaveDispatchAsync(PCCExecutive.Domain.Dispatch value, CancellationToken cancellationToken = default);
    Task<PCCExecutive.Domain.Dispatch?> LoadDispatchAsync(PCCExecutive.Domain.DispatchId id, CancellationToken cancellationToken = default);
    Task SaveEvidenceAsync(EvidenceRecord value, CancellationToken cancellationToken = default);
    Task<EvidenceRecord?> LoadEvidenceAsync(EvidenceId id, CancellationToken cancellationToken = default);
    Task SaveAttentionAsync(AttentionRequest value, CancellationToken cancellationToken = default);
    Task<AttentionRequest?> LoadAttentionAsync(AttentionRequestId id, CancellationToken cancellationToken = default);
    Task SaveSettingsAsync(PccExecutiveSettings value, CancellationToken cancellationToken = default);
    Task<PccExecutiveSettings> LoadSettingsAsync(CancellationToken cancellationToken = default);
}

public sealed class SqliteStateStore : IDurableStateStore, IBrowserRuntimeRegistry, IDispatchLedger, IConversationCheckpointPort, IConversationLifecycleStore, IAsyncDisposable
{
    private const int CurrentSchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = DurabilityJson.CreateOptions();
    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public SqliteStateStore(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath)) throw new ArgumentException("Database path is required.", nameof(databasePath));
        DatabasePath = Path.GetFullPath(databasePath);
        var directory = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString();
    }

    public string DatabasePath { get; }

    public static string DefaultDatabasePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PCC Executive",
        "state",
        "pcc-executive.db");

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, "PRAGMA journal_mode=WAL;", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, "PRAGMA foreign_keys=ON;", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, "CREATE TABLE IF NOT EXISTS schema_migrations(version INTEGER NOT NULL PRIMARY KEY, applied_at TEXT NOT NULL);", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, "CREATE TABLE IF NOT EXISTS state_records(kind TEXT NOT NULL, id TEXT NOT NULL, project_run_id TEXT NULL, payload TEXT NOT NULL, updated_at TEXT NOT NULL, PRIMARY KEY(kind,id));", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, "CREATE INDEX IF NOT EXISTS ix_state_records_project_run ON state_records(project_run_id, kind);", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, "CREATE TABLE IF NOT EXISTS browser_dispatch_ledger(dispatch_id TEXT NOT NULL PRIMARY KEY, content_hash TEXT NOT NULL, state TEXT NOT NULL, reconciliation_evidence TEXT NULL, updated_at TEXT NOT NULL);", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, "CREATE TABLE IF NOT EXISTS checkpoints(checkpoint_id TEXT NOT NULL PRIMARY KEY, project_run_id TEXT NOT NULL, kind TEXT NOT NULL, payload TEXT NOT NULL, created_at TEXT NOT NULL);", cancellationToken).ConfigureAwait(false);

            await using var migration = connection.CreateCommand();
            migration.CommandText = "INSERT OR IGNORE INTO schema_migrations(version, applied_at) VALUES ($version, $at);";
            migration.Parameters.AddWithValue("$version", CurrentSchemaVersion);
            migration.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            await migration.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<int> GetSchemaVersionAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(version),0) FROM schema_migrations;";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    public Task SaveProjectRunAsync(ProjectRun value, CancellationToken cancellationToken = default) => SaveAsync("project-run", value.Id.ToString(), value.Id.ToString(), value, cancellationToken);
    public Task<ProjectRun?> LoadProjectRunAsync(ProjectRunId id, CancellationToken cancellationToken = default) => LoadAsync<ProjectRun>("project-run", id.ToString(), cancellationToken);
    public Task SaveWaveAsync(Wave value, CancellationToken cancellationToken = default) => SaveAsync("wave", value.Id.ToString(), value.ProjectRunId.ToString(), value, cancellationToken);
    public Task<Wave?> LoadWaveAsync(WaveId id, CancellationToken cancellationToken = default) => LoadAsync<Wave>("wave", id.ToString(), cancellationToken);
    public Task SaveTaskAsync(WorkerTask value, ProjectRunId projectRunId, CancellationToken cancellationToken = default) => SaveAsync("task", value.Id.ToString(), projectRunId.ToString(), value, cancellationToken);
    public Task<WorkerTask?> LoadTaskAsync(TaskId id, CancellationToken cancellationToken = default) => LoadAsync<WorkerTask>("task", id.ToString(), cancellationToken);
    public Task SaveLogicalAgentAsync(LogicalAgentSession value, CancellationToken cancellationToken = default) => SaveAsync("logical-agent", value.Id.ToString(), value.ProjectRunId.ToString(), value, cancellationToken);
    public Task<LogicalAgentSession?> LoadLogicalAgentAsync(LogicalAgentId id, CancellationToken cancellationToken = default) => LoadAsync<LogicalAgentSession>("logical-agent", id.ToString(), cancellationToken);
    public Task SaveConversationAsync(PCCExecutive.Domain.Conversation value, ProjectRunId projectRunId, CancellationToken cancellationToken = default) => SaveAsync("conversation", value.Id.ToString(), projectRunId.ToString(), value, cancellationToken);
    public Task<PCCExecutive.Domain.Conversation?> LoadConversationAsync(ConversationId id, CancellationToken cancellationToken = default) => LoadAsync<PCCExecutive.Domain.Conversation>("conversation", id.ToString(), cancellationToken);
    public Task SaveDispatchAsync(PCCExecutive.Domain.Dispatch value, CancellationToken cancellationToken = default) => SaveAsync("dispatch", value.Id.ToString(), value.ProjectRunId.ToString(), value, cancellationToken);
    public Task<PCCExecutive.Domain.Dispatch?> LoadDispatchAsync(PCCExecutive.Domain.DispatchId id, CancellationToken cancellationToken = default) => LoadAsync<PCCExecutive.Domain.Dispatch>("dispatch", id.ToString(), cancellationToken);
    public Task SaveEvidenceAsync(EvidenceRecord value, CancellationToken cancellationToken = default) => SaveAsync("evidence", value.Id.ToString(), value.ProjectRunId.ToString(), value, cancellationToken);
    public Task<EvidenceRecord?> LoadEvidenceAsync(EvidenceId id, CancellationToken cancellationToken = default) => LoadAsync<EvidenceRecord>("evidence", id.ToString(), cancellationToken);
    public Task SaveAttentionAsync(AttentionRequest value, CancellationToken cancellationToken = default) => SaveAsync("attention", value.Id.ToString(), value.ProjectRunId.ToString(), value, cancellationToken);
    public Task<AttentionRequest?> LoadAttentionAsync(AttentionRequestId id, CancellationToken cancellationToken = default) => LoadAsync<AttentionRequest>("attention", id.ToString(), cancellationToken);
    public Task SaveSettingsAsync(PccExecutiveSettings value, CancellationToken cancellationToken = default) => SaveAsync("settings", "application", null, value, cancellationToken);

    public async Task<PccExecutiveSettings> LoadSettingsAsync(CancellationToken cancellationToken = default) =>
        await LoadAsync<PccExecutiveSettings>("settings", "application", cancellationToken).ConfigureAwait(false) ?? new PccExecutiveSettings();

    public Task<BrowserRuntimeRecord?> GetBrowserRuntimeAsync(string runtimeId, CancellationToken cancellationToken = default) =>
        LoadAsync<BrowserRuntimeRecord>("browser-runtime", runtimeId, cancellationToken);

    public Task SaveBrowserConversationAsync(ConversationRecord conversation, CancellationToken cancellationToken = default) =>
        SaveAsync("browser-conversation", conversation.ConversationId, conversation.ProjectRunId, conversation, cancellationToken);

    public async Task<IReadOnlyList<ConversationRecord>> ListBrowserConversationsAsync(CancellationToken cancellationToken = default) =>
        await ListKindAsync<ConversationRecord>("browser-conversation", cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<BrowserRuntimeRecord>> ListBrowserRuntimesAsync(CancellationToken cancellationToken = default) =>
        await ListKindAsync<BrowserRuntimeRecord>("browser-runtime", cancellationToken).ConfigureAwait(false);

    Task IBrowserRuntimeRegistry.UpsertAsync(BrowserRuntimeRecord runtime, CancellationToken cancellationToken) =>
        SaveAsync("browser-runtime", runtime.RuntimeId, runtime.ProjectRunId, runtime, cancellationToken);

    Task<BrowserRuntimeRecord?> IBrowserRuntimeRegistry.GetAsync(string runtimeId, CancellationToken cancellationToken) =>
        GetBrowserRuntimeAsync(runtimeId, cancellationToken);

    Task<IReadOnlyList<BrowserRuntimeRecord>> IBrowserRuntimeRegistry.ListAsync(CancellationToken cancellationToken) =>
        ListBrowserRuntimesAsync(cancellationToken);

    public async Task<DispatchReservation> ReserveAsync(string dispatchId, string contentHash, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            var existing = await ReadLedgerAsync(connection, dispatchId, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                if (!StringComparer.Ordinal.Equals(existing.ContentHash, contentHash))
                    return new(DispatchReservationStatus.ContentConflict, existing, "DISPATCH_ID_CONTENT_HASH_CONFLICT");
                if (existing.State is PCCExecutive.Browser.DispatchState.Prepared or PCCExecutive.Browser.DispatchState.SafeRetry)
                    return new(DispatchReservationStatus.RetryAllowed, existing, existing.State == PCCExecutive.Browser.DispatchState.Prepared ? "PREPARED_REPLAY_SAME_DISPATCH_ALLOWED" : "SAFE_RETRY_EXPLICITLY_ALLOWED");
                return new(DispatchReservationStatus.DuplicateBlocked, existing, $"DISPATCH_ALREADY_{existing.State.ToString().ToUpperInvariant()}");
            }

            var created = new DispatchLedgerEntry(dispatchId, contentHash, PCCExecutive.Browser.DispatchState.Prepared, DateTimeOffset.UtcNow);
            await WriteLedgerAsync(connection, created, cancellationToken).ConfigureAwait(false);
            return new(DispatchReservationStatus.New, created, "DISPATCH_RESERVED");
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task UpdateAsync(string dispatchId, PCCExecutive.Browser.DispatchState state, string? reconciliationEvidence = null, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            var existing = await ReadLedgerAsync(connection, dispatchId, cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Dispatch '{dispatchId}' is not reserved.");
            await WriteLedgerAsync(connection, existing with { State = state, ReconciliationEvidence = reconciliationEvidence, UpdatedAt = DateTimeOffset.UtcNow }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<DispatchLedgerEntry?> GetDispatchLedgerAsync(string dispatchId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadLedgerAsync(connection, dispatchId, cancellationToken).ConfigureAwait(false);
    }

    Task<DispatchLedgerEntry?> IDispatchLedger.GetAsync(string dispatchId, CancellationToken cancellationToken) =>
        GetDispatchLedgerAsync(dispatchId, cancellationToken);

    public async Task<string> CreateCheckpointAsync(ConversationRecord activeConversation, CancellationToken cancellationToken = default)
    {
        var checkpointId = Guid.NewGuid().ToString("N");
        await SaveCheckpointAsync(new DurableCheckpoint(checkpointId, activeConversation.ProjectRunId, "conversation-rollover", JsonSerializer.Serialize(activeConversation, JsonOptions), DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
        return checkpointId;
    }

    public Task SaveCandidateAsync(ConversationRecord candidate, string checkpointId, CancellationToken cancellationToken = default) =>
        SaveAsync("browser-conversation-candidate", candidate.ConversationId, candidate.ProjectRunId, new ConversationCandidateState(candidate, checkpointId, null), cancellationToken);

    public async Task CommitRolloverAsync(ConversationRecord predecessorArchived, ConversationRecord successorActive, string checkpointId, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transactionBase = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var transaction = (SqliteTransaction)transactionBase;
            await SaveWithConnectionAsync(connection, transaction, "browser-conversation", predecessorArchived.ConversationId, predecessorArchived.ProjectRunId, predecessorArchived, cancellationToken).ConfigureAwait(false);
            await SaveWithConnectionAsync(connection, transaction, "browser-conversation", successorActive.ConversationId, successorActive.ProjectRunId, successorActive, cancellationToken).ConfigureAwait(false);
            await SaveWithConnectionAsync(connection, transaction, "browser-conversation-candidate", successorActive.ConversationId, successorActive.ProjectRunId, new ConversationCandidateState(successorActive, checkpointId, "COMMITTED"), cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public Task RecordFailedRolloverAsync(ConversationRecord predecessorStillActive, ConversationRecord? failedCandidate, string reason, CancellationToken cancellationToken = default) =>
        SaveAsync("browser-rollover-failure", Guid.NewGuid().ToString("N"), predecessorStillActive.ProjectRunId, new RolloverFailureState(predecessorStillActive, failedCandidate, reason, DateTimeOffset.UtcNow), cancellationToken);

    public async Task SaveCheckpointAsync(DurableCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT OR REPLACE INTO checkpoints(checkpoint_id, project_run_id, kind, payload, created_at) VALUES ($id,$run,$kind,$payload,$at);";
            command.Parameters.AddWithValue("$id", checkpoint.Id);
            command.Parameters.AddWithValue("$run", checkpoint.ProjectRunId);
            command.Parameters.AddWithValue("$kind", checkpoint.Kind);
            command.Parameters.AddWithValue("$payload", checkpoint.Payload);
            command.Parameters.AddWithValue("$at", checkpoint.CreatedAt.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<DurableCheckpoint?> LoadCheckpointAsync(string checkpointId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT project_run_id, kind, payload, created_at FROM checkpoints WHERE checkpoint_id=$id;";
        command.Parameters.AddWithValue("$id", checkpointId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        return new DurableCheckpoint(checkpointId, reader.GetString(0), reader.GetString(1), reader.GetString(2), DateTimeOffset.Parse(reader.GetString(3), System.Globalization.CultureInfo.InvariantCulture));
    }

    private async Task SaveAsync<T>(string kind, string id, string? projectRunId, T value, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await SaveWithConnectionAsync(connection, null, kind, id, projectRunId, value, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task<T?> LoadAsync<T>(string kind, string id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM state_records WHERE kind=$kind AND id=$id;";
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$id", id);
        var payload = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        return payload is null ? default : JsonSerializer.Deserialize<T>(payload, JsonOptions);
    }

    private async Task<IReadOnlyList<T>> ListKindAsync<T>(string kind, CancellationToken cancellationToken)
    {
        var result = new List<T>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM state_records WHERE kind=$kind ORDER BY id;";
        command.Parameters.AddWithValue("$kind", kind);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var value = JsonSerializer.Deserialize<T>(reader.GetString(0), JsonOptions);
            if (value is not null) result.Add(value);
        }
        return result;
    }

    private static async Task SaveWithConnectionAsync<T>(SqliteConnection connection, SqliteTransaction? transaction, string kind, string id, string? projectRunId, T value, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO state_records(kind,id,project_run_id,payload,updated_at) VALUES ($kind,$id,$run,$payload,$at) ON CONFLICT(kind,id) DO UPDATE SET project_run_id=excluded.project_run_id,payload=excluded.payload,updated_at=excluded.updated_at;";
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$run", (object?)projectRunId ?? DBNull.Value);
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(value, JsonOptions));
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<DispatchLedgerEntry?> ReadLedgerAsync(SqliteConnection connection, string dispatchId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT content_hash,state,reconciliation_evidence,updated_at FROM browser_dispatch_ledger WHERE dispatch_id=$id;";
        command.Parameters.AddWithValue("$id", dispatchId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        var state = Enum.Parse<PCCExecutive.Browser.DispatchState>(reader.GetString(1), ignoreCase: true);
        return new DispatchLedgerEntry(dispatchId, reader.GetString(0), state, DateTimeOffset.Parse(reader.GetString(3), System.Globalization.CultureInfo.InvariantCulture), reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    private static async Task WriteLedgerAsync(SqliteConnection connection, DispatchLedgerEntry entry, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO browser_dispatch_ledger(dispatch_id,content_hash,state,reconciliation_evidence,updated_at) VALUES ($id,$hash,$state,$evidence,$at) ON CONFLICT(dispatch_id) DO UPDATE SET content_hash=excluded.content_hash,state=excluded.state,reconciliation_evidence=excluded.reconciliation_evidence,updated_at=excluded.updated_at;";
        command.Parameters.AddWithValue("$id", entry.DispatchId);
        command.Parameters.AddWithValue("$hash", entry.ContentHash);
        command.Parameters.AddWithValue("$state", entry.State.ToString());
        command.Parameters.AddWithValue("$evidence", (object?)entry.ReconciliationEvidence ?? DBNull.Value);
        command.Parameters.AddWithValue("$at", entry.UpdatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        _writeGate.Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed record ConversationCandidateState(ConversationRecord Conversation, string CheckpointId, string? State);
    private sealed record RolloverFailureState(ConversationRecord Predecessor, ConversationRecord? Candidate, string Reason, DateTimeOffset At);
}

public sealed class ProjectRunLock : IDisposable
{
    private static readonly ConcurrentDictionary<string, byte> ActiveLocks = new(StringComparer.Ordinal);
    private readonly FileStream? _lease;
    private readonly string _key;
    private int _owns;
    private int _disposed;

    private ProjectRunLock(FileStream? lease, string key, bool owns)
    {
        _lease = lease;
        _key = key;
        _owns = owns ? 1 : 0;
    }

    public bool IsOwned => Volatile.Read(ref _owns) == 1;

    public static ProjectRunLock TryAcquire(string projectIdentity)
    {
        if (string.IsNullOrWhiteSpace(projectIdentity)) throw new ArgumentException("Project identity is required.", nameof(projectIdentity));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(projectIdentity))).ToLowerInvariant();
        var key = $"PCCExecutive.Project.{hash}";
        if (!ActiveLocks.TryAdd(key, 0)) return new ProjectRunLock(null, key, false);

        FileStream? lease = null;
        try
        {
            var lockDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PCC Executive",
                "project-locks");
            Directory.CreateDirectory(lockDirectory);
            var lockPath = Path.Combine(lockDirectory, $"{hash}.lock");

            // A named Mutex is thread-affine and therefore unsafe to hold across this runtime's
            // async lifecycle. An exclusive OS file handle preserves cross-process project
            // exclusivity, is releasable from any thread, and is closed by the OS on process exit.
            lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return new ProjectRunLock(lease, key, true);
        }
        catch (IOException)
        {
            lease?.Dispose();
            ActiveLocks.TryRemove(key, out _);
            return new ProjectRunLock(null, key, false);
        }
        catch
        {
            lease?.Dispose();
            ActiveLocks.TryRemove(key, out _);
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            _lease?.Dispose();
        }
        finally
        {
            if (Interlocked.Exchange(ref _owns, 0) == 1)
                ActiveLocks.TryRemove(_key, out _);
        }
    }
}
