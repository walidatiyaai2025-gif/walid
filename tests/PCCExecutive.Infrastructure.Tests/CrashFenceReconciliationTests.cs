using PCCExecutive.Application;
using PCCExecutive.Browser;
using PCCExecutive.Domain;
using PCCExecutive.Infrastructure;
using Xunit;

namespace PCCExecutive.Infrastructure.Tests;

public sealed class CrashFenceReconciliationTests
{
    [Fact]
    public async Task Submitting_crash_fence_is_materialized_as_submitted_unknown_on_restart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pcc-final-fence-{Guid.NewGuid():N}.db");
        try
        {
            var run = ProjectRunId.New();
            var agent = LogicalAgentId.New();
            var task = TaskId.New();
            var wave = WaveId.New();
            var conversation = ConversationId.New();
            var correlation = new DurableDispatchCorrelation(run, agent, new WorkerSlotId(1), task, wave, conversation, "provider", "hash");
            DispatchId id;

            await using (var store = new SqliteStateStore(path))
            {
                await store.InitializeAsync();
                var prepared = await new CanonicalDispatchReservationService(store).ReserveOrRecoverAsync(correlation);
                id = prepared.Id;
                await store.ReserveAsync(id.ToString(), prepared.ContentHash);
                await store.UpdateAsync(id.ToString(), PCCExecutive.Browser.DispatchState.Submitting, "final-enter-authorized-before-crash");
            }

            await using (var reopened = new SqliteStateStore(path))
            {
                await reopened.InitializeAsync();
                var recovered = await new CanonicalDispatchReservationService(reopened).ReserveOrRecoverAsync(correlation);
                var browserLedger = await reopened.GetDispatchLedgerAsync(id.ToString());

                Assert.Equal(id, recovered.Id);
                Assert.Equal(PCCExecutive.Domain.DispatchState.SUBMITTED_UNKNOWN, recovered.State);
                Assert.Equal(PCCExecutive.Browser.DispatchState.SubmittedUnknown, browserLedger!.State);
                Assert.Contains("RECOVERED_SUBMITTING_FENCE_AS_SUBMITTED_UNKNOWN", browserLedger.ReconciliationEvidence);
            }
        }
        finally { try { File.Delete(path); } catch { } }
    }
}
