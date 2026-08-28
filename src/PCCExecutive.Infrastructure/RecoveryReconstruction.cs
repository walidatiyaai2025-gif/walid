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

public sealed record FullDurabilityRecoverySnapshot(
    OrchestrationRecoverySnapshot Orchestration,
    IReadOnlyList<LogicalAgentSession> LogicalSessions,
    IReadOnlyList<PCCExecutive.Domain.Conversation> Conversations);

public sealed class FullDurabilityRecoveryService
{
    private static readonly JsonSerializerOptions Json = DurabilityJson.CreateOptions();
    private readonly SqliteStateStore _store;
    private readonly IOrchestrationStateStore _orchestration;
    private readonly SqliteDurabilityPolicy _policy;

    public FullDurabilityRecoveryService(SqliteStateStore store, IOrchestrationStateStore orchestration, SqliteDurabilityPolicy? policy = null)
    {
        _store = store;
        _orchestration = orchestration;
        _policy = policy ?? new();
    }

    public async Task<FullDurabilityRecoverySnapshot?> ReconstructAsync(ProjectRunId projectRunId, CancellationToken cancellationToken = default)
    {
        var orchestration = await _orchestration.LoadAsync(projectRunId, cancellationToken).ConfigureAwait(false);
        if (orchestration is null) return null;
        var sessions = await LoadKindAsync<LogicalAgentSession>("logical-agent", projectRunId, cancellationToken).ConfigureAwait(false);
        var conversations = await LoadKindAsync<PCCExecutive.Domain.Conversation>("conversation", projectRunId, cancellationToken).ConfigureAwait(false);
        return new(orchestration, sessions, conversations);
    }

    private async Task<IReadOnlyList<T>> LoadKindAsync<T>(string kind, ProjectRunId projectRunId, CancellationToken cancellationToken)
    {
        var result = new List<T>();
        await using var connection = await SqliteDurabilityConnection.OpenAsync(_store.DatabasePath, _policy, SqliteOpenMode.ReadOnly, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM state_records WHERE kind=$kind AND project_run_id=$run ORDER BY id;";
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$run", projectRunId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var value = JsonSerializer.Deserialize<T>(reader.GetString(0), Json);
            if (value is not null) result.Add(value);
        }
        return result;
    }
}

public sealed class ConversationInvariantService
{
    private readonly FullDurabilityRecoveryService _recovery;
    public ConversationInvariantService(FullDurabilityRecoveryService recovery) => _recovery = recovery;

    public async Task<bool> ExactlyOneActiveAsync(ProjectRunId projectRunId, LogicalAgentId logicalAgentId, CancellationToken cancellationToken = default)
    {
        var snapshot = await _recovery.ReconstructAsync(projectRunId, cancellationToken).ConfigureAwait(false);
        if (snapshot is null) return false;
        return snapshot.Conversations.Count(x => x.LogicalAgentId == logicalAgentId && x.State == ConversationState.Active) == 1;
    }
}

public sealed record BrowserInventoryReconciliation(BrowserReconciliationKind Outcome, string? RuntimeId, LogicalAgentId? LogicalAgentId, string Reason);

public sealed class BrowserInventoryReconciliationService
{
    public IReadOnlyList<BrowserInventoryReconciliation> Reconcile(IReadOnlyList<LogicalAgentSession> sessions, IReadOnlyList<BrowserRuntimeRecord> runtimes)
    {
        var results = new List<BrowserInventoryReconciliation>();
        foreach (var session in sessions)
        {
            var candidates = runtimes.Where(x => StringComparer.Ordinal.Equals(x.ProjectRunId, session.ProjectRunId.ToString()) && StringComparer.Ordinal.Equals(x.LogicalAgentId, session.Id.ToString())).ToArray();
            if (candidates.Length == 0) { results.Add(new(BrowserReconciliationKind.MISSING_RUNTIME, null, session.Id, "Stored logical session has no PCC runtime.")); continue; }
            var runtime = candidates.FirstOrDefault(x => x.CreatedByPcc || x.AdoptedExplicitly);
            if (runtime is null) { results.Add(new(BrowserReconciliationKind.UNKNOWN, candidates[0].RuntimeId, session.Id, "Unknown runtime: DO_NOT_ADOPT.")); continue; }
            results.Add(Single(session, runtime));
        }
        foreach (var runtime in runtimes.Where(x => (x.CreatedByPcc || x.AdoptedExplicitly) && sessions.All(s => !StringComparer.Ordinal.Equals(s.Id.ToString(), x.LogicalAgentId))))
            results.Add(new(BrowserReconciliationKind.ORPHANED_OWNED_RUNTIME, runtime.RuntimeId, null, "Owned runtime has no active durable logical-session record."));
        foreach (var runtime in runtimes.Where(x => !x.CreatedByPcc && !x.AdoptedExplicitly && sessions.All(s => !StringComparer.Ordinal.Equals(s.Id.ToString(), x.LogicalAgentId))))
            results.Add(new(BrowserReconciliationKind.UNKNOWN, runtime.RuntimeId, null, "Unknown runtime is not adopted."));
        return results;
    }

    private static BrowserInventoryReconciliation Single(LogicalAgentSession session, BrowserRuntimeRecord runtime)
    {
        var single = new BrowserSessionReconciliationService().Reconcile(session, runtime);
        return new(single.Outcome, single.RuntimeId, session.Id, single.Reason);
    }
}
