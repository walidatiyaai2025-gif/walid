using PCCExecutive.Application;
using PCCExecutive.Domain;
using Xunit;

namespace PCCExecutive.Application.Tests;

public sealed class ManagerWorkerOrchestrationTests
{
    [Fact]
    public async Task Valid_five_worker_wave_is_staged_and_reconciled()
    {
        var provider = new FakeProvider();
        var orchestrator = new ManagerWorkerOrchestrator(provider, baseDispatchInterval: TimeSpan.Zero);
        var runId = ProjectRunId.New();
        var tasks = Enumerable.Range(1, 5).Select(index =>
        {
            var scope = TaskScope.Create("owner/repo", paths: [$"src/worker-{index}"]);
            return new WorkerTask(TaskId.New(), $"work-{index}", scope, new HashSet<TaskId>(), ["validated"], TaskState.Ready, TaskFingerprint.Create($"work-{index}", scope));
        }).ToArray();
        var plan = new WavePlan(WaveId.New(), new ManagerEstimate(55), tasks, Array.Empty<Blocker>());
        var bindings = Enumerable.Range(1, 5).Select(index => new WorkerExecutionBinding(new WorkerSlotId(index), LogicalAgentId.New(), ConversationId.New())).ToArray();

        var dispatched = await orchestrator.DispatchWaveAsync(runId, plan, bindings, new EmptyCompletedIndex());

        Assert.True(dispatched.IsAccepted);
        Assert.Equal(5, dispatched.Dispatches.Count);
        Assert.Equal(5, provider.Requests.Count);

        var handoffs = tasks.Select(task => new WorkerHandoff(task.Id, "READY_FOR_INTEGRATION", "abc123", ["src"], ["tests pass"], null, "integrate", DateTimeOffset.UtcNow)).ToArray();
        var evidence = tasks.Select(task => new EvidenceRecord(EvidenceId.New(), runId, task.Id, "HEAD", "github", task.Fingerprint, "abc123", DateTimeOffset.UtcNow)).ToArray();
        var review = orchestrator.Reconcile(plan, handoffs, evidence);
        Assert.Equal(5, review.AcceptedHandoffs.Count);
        Assert.Empty(review.RejectedHandoffs);
        Assert.Empty(review.Blockers);
        Assert.Contains("5/5", review.ConsolidatedSummary);
    }

    [Fact]
    public async Task Submitted_unknown_stops_later_dispatches_without_retry()
    {
        var provider = new FakeProvider(uncertainAt: 2);
        var orchestrator = new ManagerWorkerOrchestrator(provider, baseDispatchInterval: TimeSpan.Zero);
        var runId = ProjectRunId.New();
        var tasks = Enumerable.Range(1, 3).Select(index =>
        {
            var scope = TaskScope.Create("owner/repo", paths: [$"src/task-{index}"]);
            return new WorkerTask(TaskId.New(), $"task-{index}", scope, new HashSet<TaskId>(), ["ok"], TaskState.Ready, TaskFingerprint.Create($"task-{index}", scope));
        }).ToArray();
        var plan = new WavePlan(WaveId.New(), new ManagerEstimate(20), tasks, Array.Empty<Blocker>());
        var bindings = Enumerable.Range(1, 3).Select(index => new WorkerExecutionBinding(new WorkerSlotId(index), LogicalAgentId.New(), ConversationId.New())).ToArray();

        var result = await orchestrator.DispatchWaveAsync(runId, plan, bindings, new EmptyCompletedIndex());

        Assert.True(result.HasUncertainDispatch);
        Assert.Equal(2, result.Dispatches.Count);
        Assert.Equal(2, provider.Requests.Count);
    }

    [Fact]
    public async Task Overlap_is_rejected_before_provider_send()
    {
        var provider = new FakeProvider();
        var orchestrator = new ManagerWorkerOrchestrator(provider, baseDispatchInterval: TimeSpan.Zero);
        var scope = TaskScope.Create("owner/repo", paths: ["src/core"]);
        var tasks = new[]
        {
            new WorkerTask(TaskId.New(), "a", scope, new HashSet<TaskId>(), ["ok"], TaskState.Ready, "a"),
            new WorkerTask(TaskId.New(), "b", scope, new HashSet<TaskId>(), ["ok"], TaskState.Ready, "b")
        };
        var plan = new WavePlan(WaveId.New(), new ManagerEstimate(10), tasks, Array.Empty<Blocker>());
        var bindings = new[]
        {
            new WorkerExecutionBinding(new WorkerSlotId(1), LogicalAgentId.New(), ConversationId.New()),
            new WorkerExecutionBinding(new WorkerSlotId(2), LogicalAgentId.New(), ConversationId.New())
        };

        var result = await orchestrator.DispatchWaveAsync(ProjectRunId.New(), plan, bindings, new EmptyCompletedIndex());
        Assert.False(result.IsAccepted);
        Assert.Empty(provider.Requests);
        Assert.Contains(result.Validation.Issues, issue => issue.Code == "OVERLAPPING_SCOPE");
    }

    private sealed class FakeProvider : IAgentProvider
    {
        private readonly int? _uncertainAt;
        public FakeProvider(int? uncertainAt = null) => _uncertainAt = uncertainAt;
        public AgentProviderKind Kind => AgentProviderKind.BrowserChat;
        public List<AgentRequest> Requests { get; } = [];
        public Task<ProviderHealth> ProbeAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ProviderHealth(true, true, false, "READY", "fake"));
        public Task<AgentResult> SendAsync(AgentRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var uncertain = _uncertainAt == Requests.Count;
            return Task.FromResult(new AgentResult(request.DispatchId, !uncertain, !uncertain, false, uncertain, null, uncertain ? "submitted-unknown" : "submitted", uncertain ? "SUBMITTED_UNKNOWN" : null));
        }
    }

    private sealed class EmptyCompletedIndex : ICompletedTaskIndex
    {
        public bool IsCompleted(TaskId taskId) => false;
        public bool ContainsFingerprint(string fingerprint) => false;
    }
}
