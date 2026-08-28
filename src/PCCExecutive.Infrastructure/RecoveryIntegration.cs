using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PCCExecutive.Application;
using PCCExecutive.Browser;
using PCCExecutive.Domain;

namespace PCCExecutive.Infrastructure;

public enum RecoveryStartupKind
{
    CLEAN_SHUTDOWN,
    INTERRUPTED_IDLE,
    INTERRUPTED_DISPATCH,
    INTERRUPTED_GENERATION,
    INTERRUPTED_ROLLOVER,
    INTERRUPTED_UPDATE,
    STALE_LOCK,
    RECOVERY_REQUIRED
}

public enum BrowserReconciliationKind
{
    MATCHED,
    MISSING_RUNTIME,
    ORPHANED_OWNED_RUNTIME,
    IDENTITY_MISMATCH,
    UNKNOWN
}

public sealed record PersistedAssignment(TaskId TaskId, WorkerSlotId SlotId);

public sealed record PersistedOrchestrationSnapshot(
    ProjectRun ProjectRun,
    Wave? CurrentWave,
    IReadOnlyList<WorkerTask> Tasks,
    IReadOnlyList<PersistedAssignment> Assignments,
    IReadOnlyList<Dispatch> Dispatches,
    ConsolidatedManagerReviewPacket? ManagerReview,
    OrchestrationPhase Phase,
    DateTimeOffset SavedAt);

public sealed class SqliteOrchestrationStateStore : IOrchestrationStateStore
{
    private const string Kind = "orchestration-snapshot-v1";
    private static readonly JsonSerializerOptions Json = DurabilityJson.CreateOptions();
    private readonly SqliteStateStore _store;

    public SqliteOrchestrationStateStore(SqliteStateStore store) => _store = store;

    public Task SaveAsync(OrchestrationRecoverySnapshot snapshot, CancellationToken cancellationToken = default)
    {
        var persisted = new PersistedOrchestrationSnapshot(
            snapshot.ProjectRun,
            snapshot.CurrentWave,
            snapshot.Tasks,
            snapshot.Assignments.Select(x => new PersistedAssignment(x.Key, x.Value)).ToArray(),
            snapshot.Dispatches,
            snapshot.ManagerReview,
            snapshot.Phase,
            snapshot.SavedAt);
        var checkpoint = new DurableCheckpoint(
            SnapshotId(snapshot.ProjectRun.Id),
            snapshot.ProjectRun.Id.ToString(),
            Kind,
            JsonSerializer.Serialize(persisted, Json),
            snapshot.SavedAt);
        return _store.SaveCheckpointAsync(checkpoint, cancellationToken);
    }

    public async Task<OrchestrationRecoverySnapshot?> LoadAsync(ProjectRunId projectRunId, CancellationToken cancellationToken = default)
    {
        var checkpoint = await _store.LoadCheckpointAsync(SnapshotId(projectRunId), cancellationToken).ConfigureAwait(false);
        if (checkpoint is null) return null;
        var persisted = JsonSerializer.Deserialize<PersistedOrchestrationSnapshot>(checkpoint.Payload, Json);
        if (persisted is null) throw new InvalidDataException("Persisted orchestration snapshot is unreadable.");
        return new OrchestrationRecoverySnapshot(
            persisted.ProjectRun,
            persisted.CurrentWave,
            persisted.Tasks,
            persisted.Assignments.ToDictionary(x => x.TaskId, x => x.SlotId),
            persisted.Dispatches,
            persisted.ManagerReview,
            persisted.Phase,
            persisted.SavedAt);
    }

    public static string SnapshotId(ProjectRunId id) => $"orchestration:{id}";
}

public sealed record RecoveryCheckpointDocument(
    CheckpointId CheckpointId,
    ProjectRunId ProjectRunId,
    LogicalAgentId? LogicalAgentId,
    WorkerSlotId? WorkerSlotId,
    TaskId? TaskId,
    WaveId? WaveId,
    ConversationId? ConversationId,
    DispatchId? DispatchId,
    string? Branch,
    string? Head,
    string? PullRequest,
    string CurrentStatus,
    IReadOnlyList<string> CompletedWork,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<string> ImportantDecisions,
    string NextAction,
    DateTimeOffset CreatedAt,
    int SchemaVersion,
    string ApplicationVersion,
    string Reason);

public sealed record RecoveryCheckpointEnvelope(
    int EnvelopeVersion,
    string PayloadSha256,
    RecoveryCheckpointDocument Document);

public sealed class RecoveryCheckpointService
{
    private const int EnvelopeVersion = 1;
    private static readonly JsonSerializerOptions Json = DurabilityJson.CreateOptions();
    private readonly SqliteStateStore _store;

    public RecoveryCheckpointService(SqliteStateStore store) => _store = store;

    public async Task<RecoveryCheckpointDocument> CreateAsync(
        ProjectRunId projectRunId,
        LogicalAgentId? logicalAgentId,
        WorkerSlotId? workerSlotId,
        TaskId? taskId,
        WaveId? waveId,
        ConversationId? conversationId,
        DispatchId? dispatchId,
        string? branch,
        string? head,
        string? pullRequest,
        string currentStatus,
        IReadOnlyList<string> completedWork,
        IReadOnlyList<string> blockers,
        IReadOnlyList<string> importantDecisions,
        string nextAction,
        string applicationVersion,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var document = new RecoveryCheckpointDocument(
            CheckpointId.New(), projectRunId, logicalAgentId, workerSlotId, taskId, waveId,
            conversationId, dispatchId, branch, head, pullRequest, currentStatus,
            completedWork, blockers, importantDecisions, nextAction, DateTimeOffset.UtcNow,
            await _store.GetSchemaVersionAsync(cancellationToken).ConfigureAwait(false), applicationVersion, reason);
        var payload = JsonSerializer.Serialize(document, Json);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        var envelope = new RecoveryCheckpointEnvelope(EnvelopeVersion, hash, document);
        await _store.SaveCheckpointAsync(new DurableCheckpoint(
            document.CheckpointId.ToString(), projectRunId.ToString(), "recovery-checkpoint-v1",
            JsonSerializer.Serialize(envelope, Json), document.CreatedAt), cancellationToken).ConfigureAwait(false);
        return document;
    }

    public async Task<RecoveryCheckpointDocument?> LoadAsync(CheckpointId checkpointId, CancellationToken cancellationToken = default)
    {
        var stored = await _store.LoadCheckpointAsync(checkpointId.ToString(), cancellationToken).ConfigureAwait(false);
        if (stored is null) return null;
        var envelope = JsonSerializer.Deserialize<RecoveryCheckpointEnvelope>(stored.Payload, Json)
            ?? throw new InvalidDataException("Checkpoint envelope is unreadable.");
        var payload = JsonSerializer.Serialize(envelope.Document, Json);
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(envelope.PayloadSha256)))
            throw new InvalidDataException("Checkpoint hash verification failed.");
        return envelope.Document;
    }
}

public sealed record ShutdownMarker(ProjectRunId ProjectRunId, bool Clean, DateTimeOffset At, string Reason);

public sealed class DurableStartupRecoveryService
{
    private static readonly JsonSerializerOptions Json = DurabilityJson.CreateOptions();
    private readonly SqliteStateStore _store;
    private readonly IOrchestrationStateStore _orchestration;

    public DurableStartupRecoveryService(SqliteStateStore store, IOrchestrationStateStore orchestration)
    {
        _store = store;
        _orchestration = orchestration;
    }

    public async Task<RecoveryStartupKind> BeginStartupAsync(ProjectRunId projectRunId, CancellationToken cancellationToken = default)
    {
        var previous = await _store.LoadCheckpointAsync(MarkerId(projectRunId), cancellationToken).ConfigureAwait(false);
        var previousClean = previous is not null &&
            JsonSerializer.Deserialize<ShutdownMarker>(previous.Payload, Json) is { Clean: true };

        var marker = new ShutdownMarker(projectRunId, false, DateTimeOffset.UtcNow, "APP_START");
        await _store.SaveCheckpointAsync(new DurableCheckpoint(MarkerId(projectRunId), projectRunId.ToString(), "shutdown-marker-v1", JsonSerializer.Serialize(marker, Json), marker.At), cancellationToken).ConfigureAwait(false);

        var snapshot = await _orchestration.LoadAsync(projectRunId, cancellationToken).ConfigureAwait(false);
        if (snapshot is null) return previousClean ? RecoveryStartupKind.CLEAN_SHUTDOWN : RecoveryStartupKind.RECOVERY_REQUIRED;

        var repaired = await ReconcileDispatchFenceAsync(snapshot, cancellationToken).ConfigureAwait(false);
        if (!ReferenceEquals(repaired, snapshot)) snapshot = repaired;

        if (previousClean) return RecoveryStartupKind.CLEAN_SHUTDOWN;
        if (snapshot.Dispatches.Any(x => x.State == PCCExecutive.Domain.DispatchState.SUBMITTED_UNKNOWN)) return RecoveryStartupKind.INTERRUPTED_DISPATCH;
        if (snapshot.Dispatches.Any(x => x.State == PCCExecutive.Domain.DispatchState.GENERATING)) return RecoveryStartupKind.INTERRUPTED_GENERATION;
        if (snapshot.Phase is OrchestrationPhase.Dispatching or OrchestrationPhase.WaveRunning) return RecoveryStartupKind.INTERRUPTED_DISPATCH;
        return RecoveryStartupKind.INTERRUPTED_IDLE;
    }

    public async Task MarkCleanShutdownAsync(ProjectRunId projectRunId, CancellationToken cancellationToken = default)
    {
        var marker = new ShutdownMarker(projectRunId, true, DateTimeOffset.UtcNow, "CLEAN_SHUTDOWN");
        await _store.SaveCheckpointAsync(new DurableCheckpoint(MarkerId(projectRunId), projectRunId.ToString(), "shutdown-marker-v1", JsonSerializer.Serialize(marker, Json), marker.At), cancellationToken).ConfigureAwait(false);
    }

    public async Task<OrchestrationRecoverySnapshot?> ReconstructAsync(ProjectRunId projectRunId, CancellationToken cancellationToken = default)
    {
        var snapshot = await _orchestration.LoadAsync(projectRunId, cancellationToken).ConfigureAwait(false);
        return snapshot is null ? null : await ReconcileDispatchFenceAsync(snapshot, cancellationToken).ConfigureAwait(false);
    }

    private async Task<OrchestrationRecoverySnapshot> ReconcileDispatchFenceAsync(OrchestrationRecoverySnapshot snapshot, CancellationToken cancellationToken)
    {
        var changed = false;
        var dispatches = new List<Dispatch>(snapshot.Dispatches.Count);
        foreach (var dispatch in snapshot.Dispatches)
        {
            var ledger = await _store.GetDispatchLedgerAsync(dispatch.Id.ToString(), cancellationToken).ConfigureAwait(false);
            var repaired = dispatch;
            if (ledger is not null)
            {
                var mapped = Map(ledger.State, dispatch.State);
                if (mapped != dispatch.State)
                {
                    repaired = dispatch with
                    {
                        State = mapped,
                        ReconciliationEvidence = string.Join(";", new[] { dispatch.ReconciliationEvidence, ledger.ReconciliationEvidence, $"recovered-browser-ledger:{ledger.State}" }.Where(x => !string.IsNullOrWhiteSpace(x)))
                    };
                    changed = true;
                }
            }
            dispatches.Add(repaired);
        }
        if (!changed) return snapshot;
        var updated = snapshot with { Dispatches = dispatches, SavedAt = DateTimeOffset.UtcNow };
        await _orchestration.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    private static PCCExecutive.Domain.DispatchState Map(PCCExecutive.Browser.DispatchState state, PCCExecutive.Domain.DispatchState current) => state switch
    {
        PCCExecutive.Browser.DispatchState.Submitting => PCCExecutive.Domain.DispatchState.SUBMITTED_UNKNOWN,
        PCCExecutive.Browser.DispatchState.SubmittedUnknown => PCCExecutive.Domain.DispatchState.SUBMITTED_UNKNOWN,
        PCCExecutive.Browser.DispatchState.Submitted => PCCExecutive.Domain.DispatchState.SUBMITTED,
        PCCExecutive.Browser.DispatchState.Acknowledged => PCCExecutive.Domain.DispatchState.ACKNOWLEDGED,
        PCCExecutive.Browser.DispatchState.Generating => PCCExecutive.Domain.DispatchState.GENERATING,
        PCCExecutive.Browser.DispatchState.ResponseComplete => PCCExecutive.Domain.DispatchState.COMPLETED,
        PCCExecutive.Browser.DispatchState.Failed => PCCExecutive.Domain.DispatchState.FAILED,
        PCCExecutive.Browser.DispatchState.SafeRetry when current == PCCExecutive.Domain.DispatchState.PREPARED => PCCExecutive.Domain.DispatchState.SUBMITTED_UNKNOWN,
        _ => current
    };

    private static string MarkerId(ProjectRunId id) => $"shutdown:{id}";
}

public sealed class SafeShutdownCoordinator
{
    private readonly INewSendPausePort _sendGate;
    private readonly RecoveryCheckpointService _checkpoints;
    private readonly DurableStartupRecoveryService _startup;
    private readonly IOrchestrationStateStore _orchestration;
    private readonly SqliteStateStore _store;

    public SafeShutdownCoordinator(INewSendPausePort sendGate, RecoveryCheckpointService checkpoints, DurableStartupRecoveryService startup, IOrchestrationStateStore orchestration, SqliteStateStore store)
    {
        _sendGate = sendGate;
        _checkpoints = checkpoints;
        _startup = startup;
        _orchestration = orchestration;
        _store = store;
    }

    public async Task ShutdownAsync(OrchestrationRecoverySnapshot snapshot, string applicationVersion, CancellationToken cancellationToken = default)
    {
        await _sendGate.PauseNewSendsAsync("SAFE_SHUTDOWN", cancellationToken).ConfigureAwait(false);
        await _orchestration.SaveAsync(snapshot with { SavedAt = DateTimeOffset.UtcNow }, cancellationToken).ConfigureAwait(false);
        await _checkpoints.CreateAsync(snapshot.ProjectRun.Id, null, null, null, snapshot.CurrentWave?.Id, null, null, null, null, null,
            snapshot.Phase.ToString(), [], [], [], "Restart from durable orchestration snapshot.", applicationVersion, "APP_SHUTDOWN", cancellationToken).ConfigureAwait(false);
        await FlushAsync(_store.DatabasePath, cancellationToken).ConfigureAwait(false);
        await _startup.MarkCleanShutdownAsync(snapshot.ProjectRun.Id, cancellationToken).ConfigureAwait(false);
    }

    private static async Task FlushAsync(string path, CancellationToken cancellationToken)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString();
        await using var connection = new SqliteConnection(cs);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(FULL);";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

public sealed record BackupVerification(string BackupPath, string Sha256, int SchemaVersion, DateTimeOffset VerifiedAt);

public sealed class SqliteBackupService
{
    public async Task<BackupVerification> CreateAndVerifyAsync(SqliteStateStore store, string backupDirectory, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(backupDirectory);
        var file = Path.Combine(backupDirectory, $"pcc-state-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.db");
        var sourceCs = new SqliteConnectionStringBuilder { DataSource = store.DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString();
        var targetCs = new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString();
        await using (var source = new SqliteConnection(sourceCs))
        await using (var target = new SqliteConnection(targetCs))
        {
            await source.OpenAsync(cancellationToken).ConfigureAwait(false);
            await target.OpenAsync(cancellationToken).ConfigureAwait(false);
            source.BackupDatabase(target);
        }

        await using (var verify = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        {
            await verify.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = verify.CreateCommand();
            command.CommandText = "PRAGMA integrity_check;";
            var result = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Backup integrity check failed: {result}");
        }

        await using var backupStream = File.OpenRead(file);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(backupStream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
        return new BackupVerification(file, hash, await store.GetSchemaVersionAsync(cancellationToken).ConfigureAwait(false), DateTimeOffset.UtcNow);
    }
}

public sealed record PreUpdateRecoveryCheckpoint(
    string AttemptId,
    ProjectRunId ProjectRunId,
    string OrchestrationCheckpointId,
    BackupVerification Backup,
    DateTimeOffset CreatedAt,
    string ApplicationVersion,
    bool SafeToUpdate);

public sealed class PreUpdateRecoveryCoordinator
{
    private static readonly JsonSerializerOptions Json = DurabilityJson.CreateOptions();
    private readonly INewSendPausePort _sendGate;
    private readonly IOrchestrationStateStore _orchestration;
    private readonly SqliteStateStore _store;
    private readonly SqliteBackupService _backup;

    public PreUpdateRecoveryCoordinator(INewSendPausePort sendGate, IOrchestrationStateStore orchestration, SqliteStateStore store, SqliteBackupService backup)
    {
        _sendGate = sendGate;
        _orchestration = orchestration;
        _store = store;
        _backup = backup;
    }

    public async Task<PreUpdateRecoveryCheckpoint> PrepareAsync(OrchestrationRecoverySnapshot snapshot, string backupDirectory, string applicationVersion, CancellationToken cancellationToken = default)
    {
        await _sendGate.PauseNewSendsAsync("PRE_UPDATE_CHECKPOINT", cancellationToken).ConfigureAwait(false);
        await _orchestration.SaveAsync(snapshot with { SavedAt = DateTimeOffset.UtcNow }, cancellationToken).ConfigureAwait(false);
        var backup = await _backup.CreateAndVerifyAsync(_store, backupDirectory, cancellationToken).ConfigureAwait(false);
        var attemptId = Guid.NewGuid().ToString("N");
        var result = new PreUpdateRecoveryCheckpoint(attemptId, snapshot.ProjectRun.Id, SqliteOrchestrationStateStore.SnapshotId(snapshot.ProjectRun.Id), backup, DateTimeOffset.UtcNow, applicationVersion, true);
        await _store.SaveCheckpointAsync(new DurableCheckpoint($"update:{attemptId}", snapshot.ProjectRun.Id.ToString(), "pre-update-v1", JsonSerializer.Serialize(result, Json), result.CreatedAt), cancellationToken).ConfigureAwait(false);
        return result;
    }
}

public sealed record BrowserReconciliationResult(BrowserReconciliationKind Outcome, string? RuntimeId, string Reason);

public sealed class BrowserSessionReconciliationService
{
    public BrowserReconciliationResult Reconcile(LogicalAgentSession storedSession, BrowserRuntimeRecord? runtime)
    {
        if (runtime is null) return new(BrowserReconciliationKind.MISSING_RUNTIME, null, "No PCC-owned runtime is present for the durable logical session.");
        if (!runtime.CreatedByPcc && !runtime.AdoptedExplicitly) return new(BrowserReconciliationKind.UNKNOWN, runtime.RuntimeId, "Unknown Browser runtime is not eligible for automatic adoption.");
        if (!StringComparer.Ordinal.Equals(runtime.ProjectRunId, storedSession.ProjectRunId.ToString()) ||
            !StringComparer.Ordinal.Equals(runtime.LogicalAgentId, storedSession.Id.ToString()))
            return new(BrowserReconciliationKind.IDENTITY_MISMATCH, runtime.RuntimeId, "Runtime identity does not match the durable logical session.");
        if (storedSession.CurrentConversationId is not null &&
            !string.IsNullOrWhiteSpace(runtime.ConversationIdentity) &&
            !ConversationIdentityMatches(runtime.ConversationIdentity, storedSession.CurrentConversationId.Value))
            return new(BrowserReconciliationKind.IDENTITY_MISMATCH, runtime.RuntimeId, "Runtime conversation does not match the durable active conversation.");
        return new(BrowserReconciliationKind.MATCHED, runtime.RuntimeId, "Durable logical session matches the PCC-owned Browser runtime.");
    }

    private static bool ConversationIdentityMatches(string runtimeIdentity, ConversationId durableIdentity) =>
        Guid.TryParse(runtimeIdentity, out var runtimeConversation) && runtimeConversation == durableIdentity.Value;
}

public sealed record RolloverIntent(
    string CheckpointId,
    string ProjectRunId,
    string LogicalAgentId,
    string PredecessorConversationId,
    string CandidateConversationId,
    string Status,
    string? FailureReason,
    DateTimeOffset UpdatedAt);

public sealed class DurableConversationLifecycleStore : IConversationLifecycleStore, IConversationArchiveEvidencePort
{
    private static readonly JsonSerializerOptions Json = DurabilityJson.CreateOptions();
    private readonly SqliteStateStore _store;

    public DurableConversationLifecycleStore(SqliteStateStore store) => _store = store;

    public async Task SaveCandidateAsync(ConversationRecord candidate, string checkpointId, CancellationToken cancellationToken = default)
    {
        var predecessorId = candidate.PredecessorConversationId ?? throw new InvalidOperationException("Rollover candidate must have a predecessor.");
        var predecessor = await _store.LoadConversationAsync(ParseConversation(predecessorId), cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Durable predecessor conversation was not found.");
        var domainCandidate = new PCCExecutive.Domain.Conversation(
            ParseConversation(candidate.ConversationId), predecessor.LogicalAgentId, candidate.Sequence, predecessor.Provider,
            candidate.UrlOrProviderIdentity, candidate.UrlOrProviderIdentity, ConversationState.Fresh, candidate.CreatedAt, null,
            predecessor.Id, null, predecessor.HealthScore, predecessor.EstimatedGrowth, ParseCheckpoint(checkpointId), candidate.RolloverReason);
        await WriteStateTransactionAsync(
            [("conversation", domainCandidate.Id.ToString(), candidate.ProjectRunId, JsonSerializer.Serialize(domainCandidate, Json))],
            new RolloverIntent(checkpointId, candidate.ProjectRunId, candidate.LogicalAgentId, predecessorId, candidate.ConversationId, "CANDIDATE", null, DateTimeOffset.UtcNow),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task CommitRolloverAsync(ConversationRecord predecessorArchived, ConversationRecord successorActive, string checkpointId, CancellationToken cancellationToken = default)
    {
        var predecessorId = ParseConversation(predecessorArchived.ConversationId);
        var successorId = ParseConversation(successorActive.ConversationId);
        var predecessor = await _store.LoadConversationAsync(predecessorId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Durable predecessor conversation was not found.");
        var session = await _store.LoadLogicalAgentAsync(predecessor.LogicalAgentId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Durable logical session was not found.");
        var existingCandidate = await _store.LoadConversationAsync(successorId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Durable rollover candidate was not found.");
        var now = DateTimeOffset.UtcNow;
        var archived = predecessor with { State = ConversationState.Archived, RetiredAt = now, SuccessorId = successorId, CheckpointId = ParseCheckpoint(checkpointId), RolloverReason = predecessorArchived.RolloverReason };
        var successor = existingCandidate with { State = ConversationState.Active, RetiredAt = null, PredecessorId = predecessorId, CheckpointId = ParseCheckpoint(checkpointId), RolloverReason = successorActive.RolloverReason };
        var updatedSession = session with { CurrentConversationId = successorId, State = LogicalSessionState.Active };

        await WriteStateTransactionAsync(
            [
                ("conversation", archived.Id.ToString(), successorActive.ProjectRunId, JsonSerializer.Serialize(archived, Json)),
                ("conversation", successor.Id.ToString(), successorActive.ProjectRunId, JsonSerializer.Serialize(successor, Json)),
                ("logical-agent", updatedSession.Id.ToString(), updatedSession.ProjectRunId.ToString(), JsonSerializer.Serialize(updatedSession, Json))
            ],
            new RolloverIntent(checkpointId, successorActive.ProjectRunId, successorActive.LogicalAgentId, predecessorArchived.ConversationId, successorActive.ConversationId, "COMMITTED", null, now),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordFailedRolloverAsync(ConversationRecord predecessorStillActive, ConversationRecord? failedCandidate, string reason, CancellationToken cancellationToken = default)
    {
        var predecessorId = ParseConversation(predecessorStillActive.ConversationId);
        var predecessor = await _store.LoadConversationAsync(predecessorId, cancellationToken).ConfigureAwait(false);
        if (predecessor is not null && predecessor.State != ConversationState.Active)
            await _store.SaveConversationAsync(predecessor with { State = ConversationState.Active, RetiredAt = null }, new ProjectRunId(Guid.Parse(predecessorStillActive.ProjectRunId)), cancellationToken).ConfigureAwait(false);

        var checkpointId = failedCandidate is null ? Guid.NewGuid().ToString("N") : (failedCandidate.PredecessorConversationId ?? Guid.NewGuid().ToString("N"));
        var intent = new RolloverIntent(checkpointId, predecessorStillActive.ProjectRunId, predecessorStillActive.LogicalAgentId, predecessorStillActive.ConversationId, failedCandidate?.ConversationId ?? string.Empty, "FAILED", reason, DateTimeOffset.UtcNow);
        await SaveIntentAsync(intent, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> IsLineageSafelyArchivedAsync(string logicalAgentId, string conversationIdentity, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(conversationIdentity, out var conversationGuid)) return false;
        var conversation = await _store.LoadConversationAsync(new ConversationId(conversationGuid), cancellationToken).ConfigureAwait(false);
        return conversation is { State: ConversationState.Archived, SuccessorId: not null, RetiredAt: not null } &&
               StringComparer.Ordinal.Equals(conversation.LogicalAgentId.ToString(), logicalAgentId);
    }

    private async Task WriteStateTransactionAsync(IReadOnlyList<(string Kind, string Id, string? ProjectRunId, string Payload)> writes, RolloverIntent intent, CancellationToken cancellationToken)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = _store.DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Shared, Pooling = false }.ToString();
        await using var connection = new SqliteConnection(cs);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var write in writes)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO state_records(kind,id,project_run_id,payload,updated_at) VALUES ($kind,$id,$run,$payload,$at) ON CONFLICT(kind,id) DO UPDATE SET project_run_id=excluded.project_run_id,payload=excluded.payload,updated_at=excluded.updated_at;";
            command.Parameters.AddWithValue("$kind", write.Kind);
            command.Parameters.AddWithValue("$id", write.Id);
            command.Parameters.AddWithValue("$run", (object?)write.ProjectRunId ?? DBNull.Value);
            command.Parameters.AddWithValue("$payload", write.Payload);
            command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await WriteIntentAsync(connection, transaction, intent, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task SaveIntentAsync(RolloverIntent intent, CancellationToken cancellationToken)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = _store.DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Shared, Pooling = false }.ToString();
        await using var connection = new SqliteConnection(cs);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await WriteIntentAsync(connection, transaction, intent, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteIntentAsync(SqliteConnection connection, SqliteTransaction transaction, RolloverIntent intent, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO state_records(kind,id,project_run_id,payload,updated_at) VALUES ('rollover-intent',$id,$run,$payload,$at) ON CONFLICT(kind,id) DO UPDATE SET project_run_id=excluded.project_run_id,payload=excluded.payload,updated_at=excluded.updated_at;";
        command.Parameters.AddWithValue("$id", $"{intent.PredecessorConversationId}:{intent.CandidateConversationId}");
        command.Parameters.AddWithValue("$run", intent.ProjectRunId);
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(intent, Json));
        command.Parameters.AddWithValue("$at", intent.UpdatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static ConversationId ParseConversation(string value) => new(Guid.Parse(value));
    private static CheckpointId ParseCheckpoint(string value) => new(Guid.Parse(value));
}
