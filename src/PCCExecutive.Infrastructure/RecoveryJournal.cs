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

public enum RecoveryJournalKind
{
    APP_CRASH_DETECTED,
    UNCLEAN_SHUTDOWN,
    DB_INTEGRITY_CHECK,
    BACKUP_VERIFIED,
    BACKUP_RESTORED,
    MIGRATION_STARTED,
    MIGRATION_COMPLETED,
    MIGRATION_FAILED,
    UNCERTAIN_DISPATCH_RECOVERED,
    ROLLOVER_RECOVERED,
    ORPHAN_SESSION_DETECTED,
    RECOVERY_COMPLETE,
    RECOVERY_FAILED
}

public sealed class RecoveryJournalService
{
    private readonly string _databasePath;
    private readonly SqliteDurabilityPolicy _policy;
    public RecoveryJournalService(string databasePath, SqliteDurabilityPolicy? policy = null) { _databasePath = databasePath; _policy = policy ?? new(); }

    public async Task RecordAsync(RecoveryJournalKind kind, string detail, ProjectRunId? projectRunId = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await SqliteDurabilityConnection.OpenAsync(_databasePath, _policy, cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO recovery_journal(event_id,project_run_id,kind,detail,created_at) VALUES($id,$run,$kind,$detail,$at);";
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$run", (object?)projectRunId?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$kind", kind.ToString());
        command.Parameters.AddWithValue("$detail", detail);
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RecoveryJournalKind>> ListAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<RecoveryJournalKind>();
        await using var connection = await SqliteDurabilityConnection.OpenAsync(_databasePath, _policy, cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT kind FROM recovery_journal ORDER BY created_at,event_id;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(Enum.Parse<RecoveryJournalKind>(reader.GetString(0), true));
        return result;
    }
}
