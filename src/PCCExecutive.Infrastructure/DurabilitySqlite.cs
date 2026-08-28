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

public enum CrashFaultPoint
{
    NONE,
    BEFORE_BEGIN,
    AFTER_BEGIN,
    AFTER_FIRST_WRITE,
    BEFORE_COMMIT,
    AFTER_COMMIT,
    BEFORE_ACK_SAVE,
    AFTER_ACK_SAVE
}

public interface ICrashFaultInjector
{
    void Hit(CrashFaultPoint point);
}

public sealed class NoCrashFaultInjector : ICrashFaultInjector
{
    public void Hit(CrashFaultPoint point) { }
}

public sealed class DeterministicCrashFaultInjector : ICrashFaultInjector
{
    private readonly CrashFaultPoint _failAt;
    public DeterministicCrashFaultInjector(CrashFaultPoint failAt) => _failAt = failAt;
    public void Hit(CrashFaultPoint point)
    {
        if (point == _failAt) throw new InjectedCrashException(point);
    }
}

public sealed class InjectedCrashException : Exception
{
    public InjectedCrashException(CrashFaultPoint point) : base($"Injected crash at {point}.") => Point = point;
    public CrashFaultPoint Point { get; }
}

public sealed record SqliteDurabilityPolicy(
    string JournalMode = "WAL",
    string Synchronous = "FULL",
    bool ForeignKeys = true,
    int BusyTimeoutMilliseconds = 5000,
    int BusyRetryCount = 5,
    int WalAutoCheckpointPages = 1000);

public static class SqliteDurabilityConnection
{
    public static string ConnectionString(string databasePath, SqliteOpenMode mode = SqliteOpenMode.ReadWriteCreate) =>
        new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = mode, Cache = SqliteCacheMode.Shared }.ToString();

    public static async Task<SqliteConnection> OpenAsync(string databasePath, SqliteDurabilityPolicy policy, SqliteOpenMode mode = SqliteOpenMode.ReadWriteCreate, CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(ConnectionString(databasePath, mode));
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        if (mode != SqliteOpenMode.ReadOnly)
            await ExecuteAsync(connection, $"PRAGMA journal_mode={policy.JournalMode};", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, $"PRAGMA synchronous={policy.Synchronous};", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, $"PRAGMA foreign_keys={(policy.ForeignKeys ? "ON" : "OFF")};", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, $"PRAGMA busy_timeout={policy.BusyTimeoutMilliseconds};", cancellationToken).ConfigureAwait(false);
        if (mode != SqliteOpenMode.ReadOnly)
            await ExecuteAsync(connection, $"PRAGMA wal_autocheckpoint={policy.WalAutoCheckpointPages};", cancellationToken).ConfigureAwait(false);
        return connection;
    }

    public static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

public enum DatabaseIntegrityState { INTEGRITY_OK, INTEGRITY_WARNING, INTEGRITY_FAILED }
public sealed record DatabaseIntegrityResult(DatabaseIntegrityState State, IReadOnlyList<string> Findings, DateTimeOffset CheckedAt);

public sealed class SqliteIntegrityService
{
    private readonly SqliteDurabilityPolicy _policy;
    public SqliteIntegrityService(SqliteDurabilityPolicy? policy = null) => _policy = policy ?? new();

    public async Task<DatabaseIntegrityResult> CheckAsync(string databasePath, CancellationToken cancellationToken = default)
    {
        var findings = new List<string>();
        try
        {
            await using var connection = await SqliteDurabilityConnection.OpenAsync(databasePath, _policy, SqliteOpenMode.ReadOnly, cancellationToken).ConfigureAwait(false);
            var integrity = await ScalarAsync(connection, "PRAGMA integrity_check;", cancellationToken).ConfigureAwait(false);
            if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase)) findings.Add($"integrity:{integrity}");
            await using var fk = connection.CreateCommand();
            fk.CommandText = "PRAGMA foreign_key_check;";
            await using var reader = await fk.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) findings.Add("foreign-key-violation");
            var state = findings.Count == 0 ? DatabaseIntegrityState.INTEGRITY_OK : DatabaseIntegrityState.INTEGRITY_WARNING;
            return new(state, findings, DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or InvalidDataException)
        {
            return new(DatabaseIntegrityState.INTEGRITY_FAILED, [$"{ex.GetType().Name}:{ex.Message}"], DateTimeOffset.UtcNow);
        }
    }

    private static async Task<string?> ScalarAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
    }
}

public enum SchemaCompatibility { CURRENT, UPGRADE_REQUIRED, NEWER_THAN_APP, UNSUPPORTED, CORRUPTED }
public enum MigrationRunStatus { PENDING, RUNNING, APPLIED, FAILED, ROLLED_BACK, RECOVERY_REQUIRED }

public sealed record MigrationJournalEntry(
    string MigrationId,
    int FromVersion,
    int ToVersion,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    MigrationRunStatus Status,
    string? Failure);

public sealed class DurabilitySchemaManager
{
    public const int TargetSchemaVersion = 2;
    private readonly string _databasePath;
    private readonly SqliteDurabilityPolicy _policy;
    private readonly SqliteIntegrityService _integrity;

    public DurabilitySchemaManager(string databasePath, SqliteDurabilityPolicy? policy = null)
    {
        _databasePath = databasePath;
        _policy = policy ?? new();
        _integrity = new SqliteIntegrityService(_policy);
    }

    public async Task InitializeMetadataAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await SqliteDurabilityConnection.OpenAsync(_databasePath, _policy, cancellationToken: cancellationToken).ConfigureAwait(false);
        await SqliteDurabilityConnection.ExecuteAsync(connection, "CREATE TABLE IF NOT EXISTS durability_metadata(key TEXT NOT NULL PRIMARY KEY, value TEXT NOT NULL, updated_at TEXT NOT NULL);", cancellationToken).ConfigureAwait(false);
        await SqliteDurabilityConnection.ExecuteAsync(connection, "CREATE TABLE IF NOT EXISTS migration_journal(migration_id TEXT NOT NULL PRIMARY KEY, from_version INTEGER NOT NULL, to_version INTEGER NOT NULL, started_at TEXT NOT NULL, completed_at TEXT NULL, status TEXT NOT NULL, failure TEXT NULL);", cancellationToken).ConfigureAwait(false);
        await EnsureDatabaseIdAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SchemaCompatibility> ClassifyAsync(CancellationToken cancellationToken = default)
    {
        var integrity = await _integrity.CheckAsync(_databasePath, cancellationToken).ConfigureAwait(false);
        if (integrity.State == DatabaseIntegrityState.INTEGRITY_FAILED) return SchemaCompatibility.CORRUPTED;
        await using var connection = await SqliteDurabilityConnection.OpenAsync(_databasePath, _policy, cancellationToken: cancellationToken).ConfigureAwait(false);
        var current = await CurrentVersionAsync(connection, cancellationToken).ConfigureAwait(false);
        if (current > TargetSchemaVersion) return SchemaCompatibility.NEWER_THAN_APP;
        if (current == TargetSchemaVersion) return SchemaCompatibility.CURRENT;
        if (current is >= 1 and < TargetSchemaVersion) return SchemaCompatibility.UPGRADE_REQUIRED;
        return SchemaCompatibility.UNSUPPORTED;
    }

    public async Task<SchemaCompatibility> MigrateAsync(ICrashFaultInjector? faultInjector = null, CancellationToken cancellationToken = default)
    {
        faultInjector ??= new NoCrashFaultInjector();
        await InitializeMetadataAsync(cancellationToken).ConfigureAwait(false);
        var classification = await ClassifyAsync(cancellationToken).ConfigureAwait(false);
        if (classification is SchemaCompatibility.CURRENT or SchemaCompatibility.NEWER_THAN_APP or SchemaCompatibility.UNSUPPORTED or SchemaCompatibility.CORRUPTED)
            return classification;

        await using var connection = await SqliteDurabilityConnection.OpenAsync(_databasePath, _policy, cancellationToken: cancellationToken).ConfigureAwait(false);
        var from = await CurrentVersionAsync(connection, cancellationToken).ConfigureAwait(false);
        if (from != 1) return SchemaCompatibility.UNSUPPORTED;
        const string migrationId = "durability-v2";
        var started = DateTimeOffset.UtcNow;
        await UpsertJournalAsync(connection, new(migrationId, 1, 2, started, null, MigrationRunStatus.RUNNING, null), null, cancellationToken).ConfigureAwait(false);

        SqliteTransaction? transaction = null;
        try
        {
            faultInjector.Hit(CrashFaultPoint.BEFORE_BEGIN);
            transaction = connection.BeginTransaction(IsolationLevel.Serializable);
            faultInjector.Hit(CrashFaultPoint.AFTER_BEGIN);
            await ExecuteAsync(connection, transaction, "CREATE TABLE IF NOT EXISTS durability_operations(operation_kind TEXT NOT NULL, idempotency_key TEXT NOT NULL, project_run_id TEXT NOT NULL, payload_sha256 TEXT NOT NULL, revision INTEGER NOT NULL, status TEXT NOT NULL, created_at TEXT NOT NULL, committed_at TEXT NULL, PRIMARY KEY(operation_kind,idempotency_key));", cancellationToken).ConfigureAwait(false);
            faultInjector.Hit(CrashFaultPoint.AFTER_FIRST_WRITE);
            await ExecuteAsync(connection, transaction, "CREATE TABLE IF NOT EXISTS durability_backups(backup_id TEXT NOT NULL PRIMARY KEY, source_database_id TEXT NOT NULL, schema_version INTEGER NOT NULL, application_version TEXT NOT NULL, source_sha TEXT NULL, created_at TEXT NOT NULL, reason TEXT NOT NULL, file_path TEXT NOT NULL, file_hash TEXT NOT NULL, integrity_status TEXT NOT NULL, manifest_json TEXT NOT NULL);", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction, "CREATE TABLE IF NOT EXISTS recovery_journal(event_id TEXT NOT NULL PRIMARY KEY, project_run_id TEXT NULL, kind TEXT NOT NULL, detail TEXT NOT NULL, created_at TEXT NOT NULL);", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction, "CREATE TABLE IF NOT EXISTS active_conversations(logical_agent_id TEXT NOT NULL PRIMARY KEY, project_run_id TEXT NOT NULL, conversation_id TEXT NOT NULL UNIQUE, checkpoint_id TEXT NULL, updated_at TEXT NOT NULL);", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction, "CREATE TABLE IF NOT EXISTS orchestration_revisions(project_run_id TEXT NOT NULL PRIMARY KEY, revision INTEGER NOT NULL, updated_at TEXT NOT NULL);", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction, "INSERT OR IGNORE INTO schema_migrations(version,applied_at) VALUES (2,$at);", cancellationToken, ("$at", DateTimeOffset.UtcNow.ToString("O"))).ConfigureAwait(false);
            await UpsertJournalAsync(connection, new(migrationId, 1, 2, started, DateTimeOffset.UtcNow, MigrationRunStatus.APPLIED, null), transaction, cancellationToken).ConfigureAwait(false);
            faultInjector.Hit(CrashFaultPoint.BEFORE_COMMIT);
            transaction.Commit();
            faultInjector.Hit(CrashFaultPoint.AFTER_COMMIT);
            return SchemaCompatibility.CURRENT;
        }
        catch (InjectedCrashException ex)
        {
            if (transaction is not null)
            {
                try { transaction.Rollback(); } catch { }
            }
            if (ex.Point != CrashFaultPoint.AFTER_COMMIT)
                await UpsertJournalAsync(connection, new(migrationId, 1, 2, started, DateTimeOffset.UtcNow, MigrationRunStatus.ROLLED_BACK, ex.Message), null, cancellationToken).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            if (transaction is not null)
            {
                try { transaction.Rollback(); } catch { }
            }
            await UpsertJournalAsync(connection, new(migrationId, 1, 2, started, DateTimeOffset.UtcNow, MigrationRunStatus.FAILED, ex.Message), null, cancellationToken).ConfigureAwait(false);
            throw;
        }
        finally
        {
            transaction?.Dispose();
        }
    }

    public async Task<MigrationJournalEntry?> GetMigrationAsync(string migrationId, CancellationToken cancellationToken = default)
    {
        await using var connection = await SqliteDurabilityConnection.OpenAsync(_databasePath, _policy, cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT from_version,to_version,started_at,completed_at,status,failure FROM migration_journal WHERE migration_id=$id;";
        command.Parameters.AddWithValue("$id", migrationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        return new(migrationId, reader.GetInt32(0), reader.GetInt32(1), DateTimeOffset.Parse(reader.GetString(2)), reader.IsDBNull(3) ? null : DateTimeOffset.Parse(reader.GetString(3)), Enum.Parse<MigrationRunStatus>(reader.GetString(4), true), reader.IsDBNull(5) ? null : reader.GetString(5));
    }

    public async Task<string> GetDatabaseIdAsync(CancellationToken cancellationToken = default)
    {
        await InitializeMetadataAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await SqliteDurabilityConnection.OpenAsync(_databasePath, _policy, cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM durability_metadata WHERE key='database-id';";
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture)
            ?? throw new InvalidDataException("Database identity missing.");
    }

    private static async Task<int> CurrentVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(version),0) FROM schema_migrations;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task EnsureDatabaseIdAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO durability_metadata(key,value,updated_at) VALUES ('database-id',$value,$at);";
        command.Parameters.AddWithValue("$value", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpsertJournalAsync(SqliteConnection connection, MigrationJournalEntry entry, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO migration_journal(migration_id,from_version,to_version,started_at,completed_at,status,failure) VALUES($id,$from,$to,$started,$completed,$status,$failure) ON CONFLICT(migration_id) DO UPDATE SET completed_at=excluded.completed_at,status=excluded.status,failure=excluded.failure;";
        command.Parameters.AddWithValue("$id", entry.MigrationId);
        command.Parameters.AddWithValue("$from", entry.FromVersion);
        command.Parameters.AddWithValue("$to", entry.ToVersion);
        command.Parameters.AddWithValue("$started", entry.StartedAt.ToString("O"));
        command.Parameters.AddWithValue("$completed", (object?)entry.CompletedAt?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", entry.Status.ToString());
        command.Parameters.AddWithValue("$failure", (object?)entry.Failure ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
