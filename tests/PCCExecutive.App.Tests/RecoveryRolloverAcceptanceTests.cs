using PCCExecutive.App.Presentation;
using PCCExecutive.Browser;
using Xunit;

namespace PCCExecutive.App.Tests;

public sealed class RecoveryRolloverAcceptanceTests
{
    [Fact]
    public void Crash_after_atomic_switch_keeps_successor_active_and_retires_predecessor_runtime()
    {
        var agent = Guid.NewGuid().ToString();
        var predecessor = Conversation(agent, 1, ConversationLifecycleState.Archived);
        var successor = Conversation(agent, 2, ConversationLifecycleState.Active, predecessor.ConversationId);
        var runtimes = new[]
        {
            Runtime(agent, predecessor.ConversationId, "manager-old"),
            Runtime(agent, successor.ConversationId, "manager-new")
        };

        var plan = ConversationRecoveryInvariantPlanner.Build([predecessor, successor], predecessor.ConversationId, runtimes);

        Assert.Equal(successor.ConversationId, plan.ActiveConversationId);
        Assert.True(plan.UpdateLogicalSession);
        Assert.False(plan.PromoteSelectedConversation);
        Assert.Contains("manager-old", plan.RetireRuntimeIds);
        Assert.DoesNotContain("manager-new", plan.RetireRuntimeIds);
    }

    [Fact]
    public void Two_active_conversations_normalize_to_durable_current_tip()
    {
        var agent = Guid.NewGuid().ToString();
        var first = Conversation(agent, 1, ConversationLifecycleState.Active);
        var second = Conversation(agent, 2, ConversationLifecycleState.Active, first.ConversationId);

        var plan = ConversationRecoveryInvariantPlanner.Build([first, second], first.ConversationId, Array.Empty<BrowserRuntimeRecord>());

        Assert.Equal(first.ConversationId, plan.ActiveConversationId);
        Assert.Contains(second.ConversationId, plan.ArchiveConversationIds);
        Assert.False(plan.UpdateLogicalSession);
    }

    [Fact]
    public void Zero_active_recoverable_conversation_is_restored_but_candidate_is_never_promoted()
    {
        var agent = Guid.NewGuid().ToString();
        var predecessor = Conversation(agent, 1, ConversationLifecycleState.Archived);
        var candidate = Conversation(agent, 2, ConversationLifecycleState.Candidate, predecessor.ConversationId);
        var runtime = Runtime(agent, predecessor.ConversationId, "worker-live", workerSlotId: "3", taskId: "task-3");

        var plan = ConversationRecoveryInvariantPlanner.Build([predecessor, candidate], predecessor.ConversationId, [runtime]);

        Assert.Equal(predecessor.ConversationId, plan.ActiveConversationId);
        Assert.True(plan.PromoteSelectedConversation);
        Assert.NotEqual(candidate.ConversationId, plan.ActiveConversationId);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("2")]
    [InlineData("3")]
    [InlineData("4")]
    [InlineData("5")]
    public void Worker_slots_preserve_selected_successor_and_retire_only_predecessor_runtime(string workerSlot)
    {
        var agent = Guid.NewGuid().ToString();
        var predecessor = Conversation(agent, 7, ConversationLifecycleState.Archived);
        var successor = Conversation(agent, 8, ConversationLifecycleState.Active, predecessor.ConversationId);
        var task = Guid.NewGuid().ToString();
        var runtimes = new[]
        {
            Runtime(agent, predecessor.ConversationId, $"worker-{workerSlot}-old", workerSlot, task),
            Runtime(agent, successor.ConversationId, $"worker-{workerSlot}-new", workerSlot, task)
        };

        var plan = ConversationRecoveryInvariantPlanner.Build([predecessor, successor], predecessor.ConversationId, runtimes);

        Assert.Equal(successor.ConversationId, plan.ActiveConversationId);
        Assert.Single(plan.RetireRuntimeIds);
        Assert.Equal($"worker-{workerSlot}-old", plan.RetireRuntimeIds[0]);
        Assert.All(runtimes, x => Assert.Equal(workerSlot, x.WorkerSlotId));
        Assert.All(runtimes, x => Assert.Equal(task, x.TaskId));
    }

    [Fact]
    public void Failed_candidate_is_not_recoverable_when_no_predecessor_truth_exists()
    {
        var agent = Guid.NewGuid().ToString();
        var failed = Conversation(agent, 4, ConversationLifecycleState.FailedCandidate);

        var plan = ConversationRecoveryInvariantPlanner.Build([failed], failed.ConversationId, Array.Empty<BrowserRuntimeRecord>());

        Assert.Null(plan.ActiveConversationId);
        Assert.False(plan.PromoteSelectedConversation);
    }

    private static ConversationRecord Conversation(string agent, int sequence, ConversationLifecycleState state, string? predecessor = null) => new()
    {
        ConversationId = Guid.NewGuid().ToString(),
        LogicalAgentId = agent,
        ProjectRunId = Guid.NewGuid().ToString(),
        Sequence = sequence,
        UrlOrProviderIdentity = $"provider-{sequence}",
        CreatedAt = DateTimeOffset.UtcNow.AddMinutes(sequence),
        PredecessorConversationId = predecessor,
        State = state
    };

    private static BrowserRuntimeRecord Runtime(string agent, string conversationId, string runtimeId, string? workerSlotId = null, string? taskId = null) => new()
    {
        RuntimeId = runtimeId,
        ProjectRunId = Guid.NewGuid().ToString(),
        LogicalAgentId = agent,
        WorkerSlotId = workerSlotId,
        TaskId = taskId,
        ProfilePath = $"profile-{runtimeId}",
        CreatedByPcc = true,
        ConversationIdentity = conversationId,
        ProviderConversationIdentity = $"provider-{runtimeId}",
        State = BrowserSessionState.Active,
        LastHeartbeatAt = DateTimeOffset.UtcNow,
        LastActivityAt = DateTimeOffset.UtcNow,
        OwnershipNonce = $"nonce-{runtimeId}"
    };
}
