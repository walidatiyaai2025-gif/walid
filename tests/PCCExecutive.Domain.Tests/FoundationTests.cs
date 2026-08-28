using PCCExecutive.Application;
using PCCExecutive.Domain;
using Xunit;

namespace PCCExecutive.Domain.Tests;

public sealed class FoundationTests
{
    [Fact]
    public void ProjectRunStateMachine_rejects_illegal_transition()
    {
        var machine = new ProjectRunStateMachine();
        Assert.True(machine.CanTransition(ProjectRunState.Idle, ProjectRunState.Initializing));
        Assert.Throws<IllegalStateTransitionException<ProjectRunState>>(() => machine.Transition(ProjectRunState.Idle, ProjectRunState.VerifiedComplete));
    }

    [Fact]
    public void Dispatch_submitted_unknown_never_silently_becomes_completed()
    {
        var machine = new DispatchStateMachine();
        Assert.True(machine.RequiresReconciliation(DispatchState.SUBMITTED_UNKNOWN));
        Assert.True(machine.CanTransition(DispatchState.SUBMITTED_UNKNOWN, DispatchState.ACKNOWLEDGED));
        Assert.Throws<IllegalStateTransitionException<DispatchState>>(() => machine.Transition(DispatchState.SUBMITTED_UNKNOWN, DispatchState.COMPLETED));
    }

    [Fact]
    public void Worker_limit_is_five_and_zero_to_five_wave_tasks_are_legal()
    {
        var policy = new WorkerSlotPolicy(); policy.EnsureValidActiveCount(5); policy.EnsureWaveTaskCount(0); policy.EnsureWaveTaskCount(5);
        Assert.Throws<InvalidOperationException>(() => policy.EnsureValidActiveCount(6));
        Assert.Throws<InvalidOperationException>(() => policy.EnsureWaveTaskCount(6));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorkerSlotId(6));
    }

    [Fact]
    public void Duplicate_fingerprints_are_rejected()
    {
        var scope = TaskScope.Create("owner/repo", paths: ["src/a"]); var fingerprint = TaskFingerprint.Create("same work", scope);
        var result = Validate([Task(TaskId.New(), "same work", scope, fingerprint: fingerprint), Task(TaskId.New(), "same work", scope, fingerprint: fingerprint)]);
        Assert.Contains(result.Issues, x => x.Code == "DUPLICATE_TASK_FINGERPRINT");
    }

    [Fact]
    public void Overlapping_paths_are_rejected_for_parallel_workers()
    {
        var result = Validate([Task(TaskId.New(), "left", TaskScope.Create("owner/repo", paths: ["src/core"])), Task(TaskId.New(), "right", TaskScope.Create("owner/repo", paths: ["src/core/domain"]))]);
        Assert.Contains(result.Issues, x => x.Code == "OVERLAPPING_SCOPE");
    }

    [Fact]
    public void Missing_dependency_is_rejected_but_completed_dependency_is_allowed()
    {
        var missing = TaskId.New();
        var dependencies = new HashSet<TaskId> { missing };
        var task = Task(TaskId.New(), "dependent", TaskScope.Create("owner/repo", paths: ["src/a"]), dependencies);
        var validator = new WaveValidator();
        var invalid = validator.Validate(new WavePlan(WaveId.New(), new ManagerEstimate(20), [task], []), new FakeCompletedIndex());
        Assert.Contains(invalid.Issues, x => x.Code == "MISSING_DEPENDENCY");
        var valid = validator.Validate(new WavePlan(WaveId.New(), new ManagerEstimate(20), [task], []), new FakeCompletedIndex(completedIds: [missing]));
        Assert.DoesNotContain(valid.Issues, x => x.Code == "MISSING_DEPENDENCY");
    }

    [Fact]
    public void Already_completed_fingerprint_is_rejected()
    {
        var scope = TaskScope.Create("owner/repo", paths: ["src/a"]); var task = Task(TaskId.New(), "done before", scope);
        var result = new WaveValidator().Validate(new WavePlan(WaveId.New(), new ManagerEstimate(10), [task], []), new FakeCompletedIndex(fingerprints: [task.Fingerprint]));
        Assert.Contains(result.Issues, x => x.Code == "ALREADY_COMPLETED");
    }

    [Fact]
    public void Manager_estimate_cannot_set_verified_completion()
    {
        var estimate = new ManagerEstimate(100); var result = new CompletionEngine().Evaluate([new CompletionGate("implementation", true, 50, GateState.Pass, "sha"), new CompletionGate("tests", true, 50, GateState.Pending, null)], []);
        Assert.Equal(100m, estimate.Percent); Assert.Equal(50m, result.Verified.Percent); Assert.NotEqual(ProjectCompletionMode.VerifiedComplete, result.Mode);
    }

    [Fact]
    public void Verified_ninety_nine_enters_closure_mode_not_done()
    {
        var result = new CompletionEngine().Evaluate([new CompletionGate("core", true, 99, GateState.Pass, "evidence"), new CompletionGate("optional-polish", false, 1, GateState.Pending, null)], []);
        Assert.Equal(99m, result.Verified.Percent); Assert.Equal(ProjectCompletionMode.ClosureMode, result.Mode);
    }

    [Fact]
    public void Loop_guard_emits_stagnation_signals()
    {
        var snapshots = Enumerable.Range(0, 3).Select(_ => new LoopSnapshot(WaveId.New(), new HashSet<string> { "task-x" }, new HashSet<string> { "blocker-x" }, new HashSet<string> { "same-head" }, new HashSet<string> { "failed-test" }, new HashSet<string> { "same-assignment" }, new VerifiedCompletion(40))).ToArray();
        var result = new LoopGuardService().Analyze(snapshots);
        Assert.Equal(LoopGuardLevel.LoopDetected, result.Level);
        Assert.Contains(result.Signals, x => x.Type == LoopSignalType.RepeatedTaskFingerprint);
        Assert.Contains(result.Signals, x => x.Type == LoopSignalType.RepeatedBlocker);
        Assert.Contains(result.Signals, x => x.Type == LoopSignalType.UnchangedSourceOrEvidence);
        Assert.Contains(result.Signals, x => x.Type == LoopSignalType.NegligibleProgress);
        Assert.Contains(result.Signals, x => x.Type == LoopSignalType.RepeatedFailedCheck);
        Assert.Contains(result.Signals, x => x.Type == LoopSignalType.RepeatedManagerReassignment);
    }

    [Fact]
    public void Application_defaults_to_browser_without_api_requirement()
    {
        var options = new PccExecutiveOptions(); options.Validate(); Assert.Equal(AgentProviderKind.BrowserChat, options.DefaultProvider); Assert.False(options.OpenAiApiEnabled);
    }

    private static WaveValidationResult Validate(IReadOnlyList<WorkerTask> tasks) => new WaveValidator().Validate(new WavePlan(WaveId.New(), new ManagerEstimate(10), tasks, []), new FakeCompletedIndex());
    private static WorkerTask Task(TaskId id, string objective, TaskScope scope, IReadOnlySet<TaskId>? dependencies = null, string? fingerprint = null) => new(id, objective, scope, dependencies ?? new HashSet<TaskId>(), ["accept"], TaskState.Proposed, fingerprint ?? TaskFingerprint.Create(objective, scope, dependencies));

    private sealed class FakeCompletedIndex : ICompletedTaskIndex
    {
        private readonly HashSet<TaskId> _completedIds; private readonly HashSet<string> _fingerprints;
        public FakeCompletedIndex(IEnumerable<TaskId>? completedIds = null, IEnumerable<string>? fingerprints = null) { _completedIds = new HashSet<TaskId>(completedIds ?? []); _fingerprints = new HashSet<string>(fingerprints ?? [], StringComparer.OrdinalIgnoreCase); }
        public bool IsCompleted(TaskId taskId) => _completedIds.Contains(taskId);
        public bool ContainsFingerprint(string fingerprint) => _fingerprints.Contains(fingerprint);
    }
}
