using PCCExecutive.Application;
using PCCExecutive.Domain;

namespace PCCExecutive.Infrastructure;

public sealed class CanonicalDispatchReservationService : ICanonicalDispatchReservationService
{
    private readonly AutonomousDispatchJournal _journal;

    public CanonicalDispatchReservationService(SqliteStateStore store) => _journal = new AutonomousDispatchJournal(store);

    public async Task<Dispatch> ReserveOrRecoverAsync(DurableDispatchCorrelation correlation, CancellationToken cancellationToken = default)
    {
        var existing = await _journal.FindEquivalentAsync(
            correlation.ProjectRunId,
            correlation.LogicalAgentId,
            correlation.WorkerSlotId,
            correlation.TaskId,
            correlation.WaveId,
            correlation.LogicalConversationId,
            correlation.ProviderConversationId,
            correlation.ContentHash,
            cancellationToken).ConfigureAwait(false);

        if (existing is not null)
            return (await _journal.ReconcileAsync(existing, cancellationToken).ConfigureAwait(false)).Dispatch;

        var dispatch = new Dispatch(
            CanonicalDispatchIdentity.Create(correlation),
            correlation.ProjectRunId,
            correlation.WaveId,
            correlation.TaskId,
            correlation.LogicalAgentId,
            correlation.LogicalConversationId,
            correlation.ContentHash,
            DateTimeOffset.UtcNow,
            DispatchState.PREPARED,
            null, null, null, null,
            "canonical-durable-reservation",
            correlation.WorkerSlotId,
            correlation.ProviderConversationId);
        await _journal.SaveAsync(dispatch, cancellationToken).ConfigureAwait(false);
        return dispatch;
    }
}