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

public sealed record MigrationRecoveryResult(
    bool Succeeded,
    SchemaCompatibility Compatibility,
    VerifiedBackup Backup,
    string Reason);

public sealed class SafeMigrationRecoveryCoordinator
{
    private readonly VerifiedBackupService _backups;
    private readonly DurabilitySchemaManager _schema;
    private readonly RecoveryJournalService _journal;

    public SafeMigrationRecoveryCoordinator(VerifiedBackupService backups, DurabilitySchemaManager schema, RecoveryJournalService journal)
    {
        _backups = backups;
        _schema = schema;
        _journal = journal;
    }

    public async Task<MigrationRecoveryResult> MigrateWithVerifiedBackupAsync(
        string backupDirectory,
        string applicationVersion,
        string? sourceSha = null,
        ICrashFaultInjector? faultInjector = null,
        ProjectRunId? projectRunId = null,
        CancellationToken cancellationToken = default)
    {
        var backup = await _backups.CreateAsync(backupDirectory, applicationVersion, "PRE_MIGRATION", sourceSha, cancellationToken).ConfigureAwait(false);
        if (!backup.IsVerified) return new(false, await _schema.ClassifyAsync(cancellationToken).ConfigureAwait(false), backup, "PRE_MIGRATION_BACKUP_NOT_VERIFIED");
        await _journal.RecordAsync(RecoveryJournalKind.BACKUP_VERIFIED, backup.Manifest.BackupId, projectRunId, cancellationToken).ConfigureAwait(false);
        await _journal.RecordAsync(RecoveryJournalKind.MIGRATION_STARTED, "durability-v2", projectRunId, cancellationToken).ConfigureAwait(false);
        try
        {
            var compatibility = await _schema.MigrateAsync(faultInjector, cancellationToken).ConfigureAwait(false);
            if (compatibility != SchemaCompatibility.CURRENT) return new(false, compatibility, backup, $"MIGRATION_NOT_CURRENT:{compatibility}");
            await _journal.RecordAsync(RecoveryJournalKind.MIGRATION_COMPLETED, "durability-v2", projectRunId, cancellationToken).ConfigureAwait(false);
            return new(true, compatibility, backup, "MIGRATION_APPLIED_WITH_VERIFIED_BACKUP");
        }
        catch (Exception ex) when (ex is InjectedCrashException or SqliteException or InvalidOperationException)
        {
            await _journal.RecordAsync(RecoveryJournalKind.MIGRATION_FAILED, ex.GetType().Name, projectRunId, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }
}

public sealed record CorruptionRecoveryResult(
    bool Recovered,
    DatabaseIntegrityState PrimaryIntegrity,
    string? PreservedCorruptDatabase,
    string Reason);

public sealed class ConservativeCorruptionRecoveryService
{
    private readonly SqliteStateStore _store;
    private readonly SqliteIntegrityService _integrity;
    private readonly VerifiedBackupService _backups;
    private readonly DurabilitySchemaManager _schema;
    private readonly RecoveryJournalService _journal;

    public ConservativeCorruptionRecoveryService(SqliteStateStore store, VerifiedBackupService backups, DurabilitySchemaManager schema, RecoveryJournalService journal, SqliteDurabilityPolicy? policy = null)
    {
        _store = store;
        _integrity = new SqliteIntegrityService(policy);
        _backups = backups;
        _schema = schema;
        _journal = journal;
    }

    public async Task<CorruptionRecoveryResult> RecoverAsync(VerifiedBackup candidate, bool activeDatabaseLease, ProjectRunId? projectRunId = null, CancellationToken cancellationToken = default)
    {
        var primary = await _integrity.CheckAsync(_store.DatabasePath, cancellationToken).ConfigureAwait(false);
        await _journal.RecordAsync(RecoveryJournalKind.DB_INTEGRITY_CHECK, primary.State.ToString(), projectRunId, cancellationToken).ConfigureAwait(false);
        if (primary.State == DatabaseIntegrityState.INTEGRITY_OK) return new(false, primary.State, null, "PRIMARY_DATABASE_HEALTHY_NO_RESTORE_REQUIRED");
        var verified = await _backups.VerifyAsync(candidate.ManifestPath, cancellationToken).ConfigureAwait(false);
        if (!verified.IsVerified) return new(false, primary.State, null, "NO_VERIFIED_COMPATIBLE_BACKUP");
        if (verified.Manifest.SchemaVersion > DurabilitySchemaManager.TargetSchemaVersion) return new(false, primary.State, null, "BACKUP_NEWER_THAN_APPLICATION");
        var preserved = await _backups.RestoreAsync(verified, activeDatabaseLease, cancellationToken).ConfigureAwait(false);
        var compatibility = await _schema.ClassifyAsync(cancellationToken).ConfigureAwait(false);
        if (compatibility is not (SchemaCompatibility.CURRENT or SchemaCompatibility.UPGRADE_REQUIRED))
            return new(false, primary.State, preserved, $"RESTORE_SCHEMA_NOT_COMPATIBLE:{compatibility}");
        await _journal.RecordAsync(RecoveryJournalKind.BACKUP_RESTORED, verified.Manifest.BackupId, projectRunId, cancellationToken).ConfigureAwait(false);
        await _journal.RecordAsync(RecoveryJournalKind.RECOVERY_COMPLETE, "CORRUPTION_RECOVERY_COMPLETE", projectRunId, cancellationToken).ConfigureAwait(false);
        return new(true, primary.State, preserved, "VERIFIED_BACKUP_RESTORED");
    }
}

public sealed class DurabilityMaintenanceService
{
    private readonly string _databasePath;
    private readonly SqliteDurabilityPolicy _policy;
    public DurabilityMaintenanceService(string databasePath, SqliteDurabilityPolicy? policy = null) { _databasePath = databasePath; _policy = policy ?? new(); }

    public async Task CheckpointAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await SqliteDurabilityConnection.OpenAsync(_databasePath, _policy, cancellationToken: cancellationToken).ConfigureAwait(false);
        await SqliteDurabilityConnection.ExecuteAsync(connection, "PRAGMA wal_checkpoint(PASSIVE);", cancellationToken).ConfigureAwait(false);
    }

    public async Task OptimizeAsync(bool criticalDispatchTransactionActive, CancellationToken cancellationToken = default)
    {
        if (criticalDispatchTransactionActive) throw new InvalidOperationException("Maintenance is blocked while a critical dispatch transaction is active.");
        await using var connection = await SqliteDurabilityConnection.OpenAsync(_databasePath, _policy, cancellationToken: cancellationToken).ConfigureAwait(false);
        await SqliteDurabilityConnection.ExecuteAsync(connection, "PRAGMA optimize;", cancellationToken).ConfigureAwait(false);
    }
}
