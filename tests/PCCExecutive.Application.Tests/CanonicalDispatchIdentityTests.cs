using PCCExecutive.Application;
using PCCExecutive.Domain;
using Xunit;

namespace PCCExecutive.Application.Tests;

public sealed class CanonicalDispatchIdentityTests
{
    [Fact]
    public void Manager_initial_crash_after_enter_reuses_same_dispatch_id()
    {
        var c = Manager("manager-initial");
        Assert.Equal(CanonicalDispatchIdentity.Create(c), CanonicalDispatchIdentity.Create(c));
    }

    [Fact]
    public void Manager_review_crash_after_enter_reuses_same_dispatch_id()
    {
        var c = Manager("manager-review");
        Assert.Equal(CanonicalDispatchIdentity.Create(c), CanonicalDispatchIdentity.Create(c));
    }

    [Fact]
    public void Worker_crash_after_enter_reuses_same_dispatch_id_and_full_correlation_matters()
    {
        var run = ProjectRunId.New(); var agent = LogicalAgentId.New(); var task = TaskId.New(); var wave = WaveId.New(); var conversation = ConversationId.New();
        var c = new DurableDispatchCorrelation(run, agent, new WorkerSlotId(1), task, wave, conversation, "provider-1", "hash");
        Assert.Equal(CanonicalDispatchIdentity.Create(c), CanonicalDispatchIdentity.Create(c with { }));
        Assert.NotEqual(CanonicalDispatchIdentity.Create(c), CanonicalDispatchIdentity.Create(c with { WorkerSlotId = new WorkerSlotId(2) }));
        Assert.NotEqual(CanonicalDispatchIdentity.Create(c), CanonicalDispatchIdentity.Create(c with { ProviderConversationId = "provider-2" }));
    }

    private static DurableDispatchCorrelation Manager(string key)
    {
        var run = ProjectRunId.New(); var agent = LogicalAgentId.New(); var conversation = ConversationId.New();
        return new(run, agent, null, CanonicalDispatchIdentity.StableTask(run, key), CanonicalDispatchIdentity.StableWave(run, key), conversation, "provider-manager", "hash");
    }
}