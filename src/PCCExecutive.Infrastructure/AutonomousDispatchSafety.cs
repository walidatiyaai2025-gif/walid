using System.Text.Json;
using Microsoft.Data.Sqlite;
using PCCExecutive.Application;
using PCCExecutive.Browser;
using PCCExecutive.Domain;

namespace PCCExecutive.Infrastructure;

public sealed record AutonomousDispatchReconciliation(
    PCCExecutive.Domain.Dispatch Dispatch,
    bool SafeToSubmit,
    bool IsUncertain,
    bool AlreadyAccepted,
    string Evidence);

/// <summary>
/// Durable journal used by the production autonomous Browser provider path.
/// Domain dispatch intent is committed before Browser submission and reconciled
/// against the Browser ledger after crashes. Equivalent content for the same
/// run/logical-agent reuses the original DispatchId instead of creating a blind resend.
/// </summary>
public sealed class AutonomousDispatchJournal
{
    private static readonly JsonSerializerOptions Json = DurabilityJson.CreateOptions();
    private readonly SqliteStateStore _store;
    private readonly SqliteDurabilityPolicy _policy;

    public AutonomousDispatchJournal(SqliteStateStore store, SqliteDurabilityPolicy? policy = null)
    {
        _store = store;
        _policy = policy ?? new SqliteDurabilityPolicy();
    }

    public Task SaveAsync(PCCExecutive.Domain.Dispatch dispatch, CancellationToken cancellationToken = default) =>
        _store.SaveDispatchAsync(dispatch, cancellationToken);

    public async Task<PCCExecutive.Domain.Dispatch?> FindEquivalentAsync(
        ProjectRunId projectRunId,
        LogicalAgentId logicalAgentId,
        WorkerSlotId? workerSlotId,
        TaskId taskId,
        WaveId waveId,
        ConversationId conversationId,
        string providerConversationId,
        string contentHash,
        CancellationToken cancellationToken = default)
    {
        var dispatches = await ListAsync(projectRunId, cancellationToken).ConfigureAwait(false);
        return dispatches
            .Where(x => x.LogicalAgentId == logicalAgentId &&
                        x.TaskId == taskId &&
                        x.WaveId == waveId &&
                        x.ConversationId == conversationId &&
                        (x.WorkerSlotId == workerSlotId || x.WorkerSlotId is null) &&
                        (string.IsNullOrWhiteSpace(x.ProviderConversationId) ||
                         StringComparer.Ordinal.Equals(x.ProviderConversationId, providerConversationId) ||
                         StringComparer.OrdinalIgnoreCase.Equals(x.ProviderConversationId, "NEW")) &&
                        StringComparer.OrdinalIgnoreCase.Equals(x.ContentHash, contentHash))
            .OrderByDescending(x => x.PreparedAt)
            .FirstOrDefault();
    }

    public async Task<IReadOnlyList<PCCExecutive.Domain.Dispatch>> ListAsync(
        ProjectRunId projectRunId,
        CancellationToken cancellationToken = default)
    {
        var result = new List<PCCExecutive.Domain.Dispatch>();
        await using var connection = await SqliteDurabilityConnection.OpenAsync(
            _store.DatabasePath,
            _policy,
            SqliteOpenMode.ReadOnly,
            cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM state_records WHERE kind='dispatch' AND project_run_id=$run ORDER BY updated_at,id;";
        command.Parameters.AddWithValue("$run", projectRunId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var dispatch = JsonSerializer.Deserialize<PCCExecutive.Domain.Dispatch>(reader.GetString(0), Json);
            if (dispatch is not null) result.Add(dispatch);
        }
        return result;
    }

    public async Task<AutonomousDispatchReconciliation> ReconcileAsync(
        PCCExecutive.Domain.Dispatch dispatch,
        CancellationToken cancellationToken = default)
    {
        var ledger = await _store.GetDispatchLedgerAsync(dispatch.Id.ToString(), cancellationToken).ConfigureAwait(false);
        if (ledger is null)
        {
            return dispatch.State switch
            {
                PCCExecutive.Domain.DispatchState.PREPARED => new(dispatch, true, false, false, "DOMAIN_PREPARED_BROWSER_LEDGER_ABSENT"),
                PCCExecutive.Domain.DispatchState.SUBMITTED_UNKNOWN => new(dispatch, false, true, false, "SUBMITTED_UNKNOWN_BROWSER_LEDGER_ABSENT"),
                PCCExecutive.Domain.DispatchState.SUBMITTED or PCCExecutive.Domain.DispatchState.ACKNOWLEDGED or PCCExecutive.Domain.DispatchState.GENERATING or PCCExecutive.Domain.DispatchState.COMPLETED => new(dispatch, false, false, true, $"DOMAIN_ALREADY_{dispatch.State}"),
                _ => new(dispatch, false, false, false, $"DOMAIN_{dispatch.State}")
            };
        }

        // Submitting is the crash fence written only after the final Enter-boundary
        // authorization. A restart cannot know whether Enter executed after that
        // fence, so materialize the uncertainty in the durable Browser ledger too.
        if (ledger.State == PCCExecutive.Browser.DispatchState.Submitting)
        {
            const string recoveredFence = "RECOVERED_SUBMITTING_FENCE_AS_SUBMITTED_UNKNOWN";
            await _store.UpdateAsync(dispatch.Id.ToString(), PCCExecutive.Browser.DispatchState.SubmittedUnknown, recoveredFence, cancellationToken).ConfigureAwait(false);
            ledger = ledger with
            {
                State = PCCExecutive.Browser.DispatchState.SubmittedUnknown,
                ReconciliationEvidence = recoveredFence,
                UpdatedAt = DateTimeOffset.UtcNow
            };
        }

        var mapped = ledger.State switch
        {
            PCCExecutive.Browser.DispatchState.Prepared => PCCExecutive.Domain.DispatchState.PREPARED,
            PCCExecutive.Browser.DispatchState.SafeRetry => PCCExecutive.Domain.DispatchState.PREPARED,
            PCCExecutive.Browser.DispatchState.Submitting => PCCExecutive.Domain.DispatchState.SUBMITTED_UNKNOWN,
            PCCExecutive.Browser.DispatchState.SubmittedUnknown => PCCExecutive.Domain.DispatchState.SUBMITTED_UNKNOWN,
            PCCExecutive.Browser.DispatchState.Submitted => PCCExecutive.Domain.DispatchState.SUBMITTED,
            PCCExecutive.Browser.DispatchState.Acknowledged => PCCExecutive.Domain.DispatchState.ACKNOWLEDGED,
            PCCExecutive.Browser.DispatchState.Generating => PCCExecutive.Domain.DispatchState.GENERATING,
            PCCExecutive.Browser.DispatchState.ResponseComplete => PCCExecutive.Domain.DispatchState.COMPLETED,
            PCCExecutive.Browser.DispatchState.Failed => PCCExecutive.Domain.DispatchState.FAILED,
            _ => dispatch.State
        };

        var evidence = string.Join(";", new[]
        {
            dispatch.ReconciliationEvidence,
            ledger.ReconciliationEvidence,
            $"browser-ledger:{ledger.State}"
        }.Where(x => !string.IsNullOrWhiteSpace(x)));

        var reconciled = dispatch with
        {
            State = mapped,
            SubmittedAt = mapped is PCCExecutive.Domain.DispatchState.SUBMITTED or PCCExecutive.Domain.DispatchState.ACKNOWLEDGED or PCCExecutive.Domain.DispatchState.GENERATING or PCCExecutive.Domain.DispatchState.COMPLETED
                ? dispatch.SubmittedAt ?? ledger.UpdatedAt
                : dispatch.SubmittedAt,
            AcknowledgedAt = mapped is PCCExecutive.Domain.DispatchState.ACKNOWLEDGED or PCCExecutive.Domain.DispatchState.GENERATING or PCCExecutive.Domain.DispatchState.COMPLETED
                ? dispatch.AcknowledgedAt ?? ledger.UpdatedAt
                : dispatch.AcknowledgedAt,
            CompletedAt = mapped == PCCExecutive.Domain.DispatchState.COMPLETED ? dispatch.CompletedAt ?? ledger.UpdatedAt : dispatch.CompletedAt,
            ReconciliationEvidence = evidence
        };
        if (reconciled != dispatch) await SaveAsync(reconciled, cancellationToken).ConfigureAwait(false);

        return mapped switch
        {
            PCCExecutive.Domain.DispatchState.PREPARED when ledger.State is PCCExecutive.Browser.DispatchState.Prepared or PCCExecutive.Browser.DispatchState.SafeRetry => new(reconciled, true, false, false, evidence),
            PCCExecutive.Domain.DispatchState.SUBMITTED_UNKNOWN => new(reconciled, false, true, false, evidence),
            PCCExecutive.Domain.DispatchState.SUBMITTED or PCCExecutive.Domain.DispatchState.ACKNOWLEDGED or PCCExecutive.Domain.DispatchState.GENERATING or PCCExecutive.Domain.DispatchState.COMPLETED => new(reconciled, false, false, true, evidence),
            _ => new(reconciled, false, false, false, evidence)
        };
    }
}

/// <summary>
/// Recovery view that always merges standalone pre-submit domain dispatch rows
/// into the orchestration snapshot. This prevents a crash between Enter and a
/// later host snapshot from losing the stable DispatchId correlation.
/// </summary>
public sealed class DispatchMergedOrchestrationStateStore : IOrchestrationStateStore
{
    private readonly SqliteStateStore _store;
    private readonly SqliteOrchestrationStateStore _inner;
    private readonly AutonomousDispatchJournal _journal;

    public DispatchMergedOrchestrationStateStore(SqliteStateStore store)
    {
        _store = store;
        _inner = new SqliteOrchestrationStateStore(store);
        _journal = new AutonomousDispatchJournal(store);
    }

    public async Task SaveAsync(OrchestrationRecoverySnapshot snapshot, CancellationToken cancellationToken = default) =>
        await _inner.SaveAsync(await MergeAsync(_store, snapshot, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    public async Task<OrchestrationRecoverySnapshot?> LoadAsync(ProjectRunId projectRunId, CancellationToken cancellationToken = default)
    {
        var snapshot = await _inner.LoadAsync(projectRunId, cancellationToken).ConfigureAwait(false);
        if (snapshot is null) return null;
        return await MergeAsync(_store, snapshot, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<OrchestrationRecoverySnapshot> MergeAsync(
        SqliteStateStore store,
        OrchestrationRecoverySnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        var durable = await new AutonomousDispatchJournal(store).ListAsync(snapshot.ProjectRun.Id, cancellationToken).ConfigureAwait(false);
        if (durable.Count == 0) return snapshot;
        var byId = snapshot.Dispatches.ToDictionary(x => x.Id, x => x);
        foreach (var dispatch in durable) byId[dispatch.Id] = dispatch;
        return snapshot with { Dispatches = byId.Values.OrderBy(x => x.PreparedAt).ToArray() };
    }
}
