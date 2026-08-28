using PCCExecutive.Application;
using PCCExecutive.Browser;
using PCCExecutive.Domain;
using PCCExecutive.Infrastructure;
using Xunit;

namespace PCCExecutive.Infrastructure.Tests;

public sealed class CanonicalDispatchReservationServiceTests
{
    [Fact]
    public async Task Restart_submitted_unknown_recovers_same_dispatch_id_without_replacement()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pcc-dispatch-{Guid.NewGuid():N}.db");
        try
        {
            var run = ProjectRunId.New(); var agent = LogicalAgentId.New(); var task = TaskId.New(); var wave = WaveId.New(); var conversation = ConversationId.New();
            var correlation = new DurableDispatchCorrelation(run, agent, new WorkerSlotId(1), task, wave, conversation, "NEW", "hash");
            DispatchId id;
            await using (var store = new SqliteStateStore(path))
            {
                await store.InitializeAsync();
                var first = await new CanonicalDispatchReservationService(store).ReserveOrRecoverAsync(correlation);
                id = first.Id;
                await store.ReserveAsync(id.ToString(), first.ContentHash);
                await store.UpdateAsync(id.ToString(), PCCExecutive.Browser.DispatchState.Submitting, "crash-after-enter");
                await store.SaveDispatchAsync(first with { State = PCCExecutive.Domain.DispatchState.SUBMITTED_UNKNOWN });
            }
            await using (var reopened = new SqliteStateStore(path))
            {
                await reopened.InitializeAsync();
                var recovered = await new CanonicalDispatchReservationService(reopened).ReserveOrRecoverAsync(correlation with { ProviderConversationId = "provider-established" });
                Assert.Equal(id, recovered.Id);
                Assert.Equal(PCCExecutive.Domain.DispatchState.SUBMITTED_UNKNOWN, recovered.State);
                Assert.Single((await new AutonomousDispatchJournal(reopened).ListAsync(run)).Where(x => x.ContentHash == "hash"));
            }
        }
        finally { try { File.Delete(path); } catch { } }
    }
}