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

public sealed class CheckpointCompactionService
{
    private readonly SqliteStateStore _store;
    private readonly RecoveryCheckpointService _checkpoints;
    public CheckpointCompactionService(SqliteStateStore store, RecoveryCheckpointService checkpoints) { _store = store; _checkpoints = checkpoints; }

    public async Task<RecoveryCheckpointDocument> RecompactAsync(CheckpointId sourceId, CancellationToken cancellationToken = default)
    {
        var source = await _checkpoints.LoadAsync(sourceId, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException("Checkpoint not found.");
        return await _checkpoints.CreateAsync(source.ProjectRunId, source.LogicalAgentId, source.WorkerSlotId, source.TaskId, source.WaveId, source.ConversationId, source.DispatchId,
            source.Branch, source.Head, source.PullRequest, source.CurrentStatus, source.CompletedWork.Distinct().ToArray(), source.Blockers.Distinct().ToArray(), source.ImportantDecisions.Distinct().ToArray(), source.NextAction,
            source.ApplicationVersion, "RECOMPACTED_CHECKPOINT", cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> PruneOldRecoveryCheckpointsAsync(DateTimeOffset olderThan, IReadOnlySet<string> protectedCheckpointIds, CancellationToken cancellationToken = default)
    {
        var cs = SqliteDurabilityConnection.ConnectionString(_store.DatabasePath);
        await using var connection = new SqliteConnection(cs);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var protectedIds = protectedCheckpointIds.ToArray();
        var clauses = new List<string>();
        for (var i = 0; i < protectedIds.Length; i++)
        {
            var name = $"$protected{i}";
            clauses.Add(name);
            command.Parameters.AddWithValue(name, protectedIds[i]);
        }
        var notIn = clauses.Count == 0 ? string.Empty : $" AND checkpoint_id NOT IN ({string.Join(',', clauses)})";
        command.CommandText = $"DELETE FROM checkpoints WHERE kind='recovery-checkpoint-v1' AND created_at < $cutoff{notIn};";
        command.Parameters.AddWithValue("$cutoff", olderThan.ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

public sealed class OperationalStatePrivacyGuard
{
    private static readonly string[] Forbidden = ["password", "authorization:", "cookie:", "set-cookie", "bearer ", "api_key", "api-key", "chatgpt_session"];
    public void Validate(string text)
    {
        if (Forbidden.Any(x => text.Contains(x, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Operational persistence rejected credential/session material.");
    }
}
