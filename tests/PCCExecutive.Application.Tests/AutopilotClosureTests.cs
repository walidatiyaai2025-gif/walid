using PCCExecutive.Application;
using PCCExecutive.Domain;
using Xunit;

namespace PCCExecutive.Application.Tests;

public sealed class AutopilotClosureTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 1, 0, 0, TimeSpan.Zero);
    private const string Head = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public void Routine_temp_error_does_not_create_attention()
    {
        var result = new AttentionClassifier().Classify(OperationalCondition.TEMP_ERROR);
        Assert.False(result.RequiresAttention);
        Assert.Equal(AutopilotState.RECOVERING, result.SuggestedState);
    }

    [Fact]
    public void Login_creates_attention()
    {
        var result = new AttentionClassifier().Classify(OperationalCondition.LOGIN_REQUIRED);
        Assert.True(result.RequiresAttention);
        Assert.Equal(AttentionCategory.LOGIN_REQUIRED, result.Category);
    }

    [Fact]
    public async Task Duplicate_attention_deduplicates()
    {
        var store = new MemoryAttentionStore();
        var coordinator = new AttentionLifecycleCoordinator(store);
        var observation = Observation(AttentionCategory.LOGIN_REQUIRED);
        var first = await coordinator.ObserveAsync(observation);
        var second = await coordinator.ObserveAsync(observation with { ObservedAt = Now.AddMinutes(1) });
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(2, second.ObservationCount);
        Assert.Single(store.Items);
    }

    [Fact]
    public async Task Login_recovery_auto_resolves_attention()
    {
        var store = new MemoryAttentionStore();
        var coordinator = new AttentionLifecycleCoordinator(store);
        var item = await coordinator.ObserveAsync(Observation(AttentionCategory.LOGIN_REQUIRED));
        var resolved = await coordinator.AutoResolveLoginAsync(item, new ProviderHealth(true, true, false, "READY", "authenticated"), Now.AddMinutes(1));
        Assert.Equal(AttentionLifecycleState.AUTO_RESOLVED, resolved.State);
        Assert.NotNull(resolved.ResolvedAt);
    }

    [Fact]
    public void Stale_evidence_cannot_complete_gate()
    {
        var quality = new EvidenceQualityEvaluator().Evaluate(QualityInput(capturedAt: Now.AddHours(-2), maxAge: TimeSpan.FromMinutes(10)), Now);
        Assert.Equal(EvidenceQuality.STALE, quality.Quality);
        var result = Controller().Evaluate(new ManagerEstimate(100), [Gate(CompletionGateFamily.IMPLEMENTATION, GateState.Pass, true, 100, quality)], []);
        Assert.NotEqual(ProjectCompletionMode.VerifiedComplete, result.Mode);
        Assert.True(result.VerifiedCompletion.Percent < 100m);
    }

    [Fact]
    public void Contradicted_evidence_fails_quality()
    {
        var quality = new EvidenceQualityEvaluator().Evaluate(QualityInput(contradictions: ["head mismatch"]), Now);
        Assert.Equal(EvidenceQuality.CONTRADICTED, quality.Quality);
        Assert.False(quality.CanSatisfyGate);
    }

    [Fact]
    public void Ninety_eight_percent_remains_normal_execution()
    {
        var result = Controller().Evaluate(new ManagerEstimate(99),
            [Gate(CompletionGateFamily.IMPLEMENTATION, GateState.Pass, true, 98, Strong()), Gate(CompletionGateFamily.E2E, GateState.Pending, false, 2, Acceptable())], []);
        Assert.Equal(98m, result.VerifiedCompletion.Percent);
        Assert.Equal(ProjectCompletionMode.Active, result.Mode);
    }

    [Fact]
    public void Ninety_nine_percent_enters_closure_mode()
    {
        var result = Controller().Evaluate(new ManagerEstimate(100),
            [Gate(CompletionGateFamily.IMPLEMENTATION, GateState.Pass, true, 99, Strong()), Gate(CompletionGateFamily.RELEASE, GateState.Pending, false, 1, Acceptable())], []);
        Assert.Equal(99m, result.VerifiedCompletion.Percent);
        Assert.Equal(ProjectCompletionMode.ClosureMode, result.Mode);
    }

    [Fact]
    public void Closure_mode_rejects_feature_expansion()
    {
        var decision = new ClosureWorkPolicy().Evaluate(ClosureWorkKind.NEW_FEATURE, ClosurePriority.P2_POLISH);
        Assert.False(decision.Allowed);
    }

    [Fact]
    public void Critical_defect_is_allowed_in_closure_mode()
    {
        var decision = new ClosureWorkPolicy().Evaluate(ClosureWorkKind.CRITICAL_ACCEPTANCE_BUG, ClosurePriority.P0_VERIFICATION_BLOCKER);
        Assert.True(decision.Allowed);
    }

    [Fact]
    public void Same_task_repeated_triggers_stagnation()
    {
        var result = new StagnationEngine().Analyze(RepeatedObservations(task: "same-task"));
        Assert.True(result.IsStagnating);
        Assert.Contains(result.Signals, x => x.Type == LoopSignalType.RepeatedTaskFingerprint);
    }

    [Fact]
    public void Same_blocker_repeated_triggers_stagnation()
    {
        var result = new StagnationEngine().Analyze(RepeatedObservations(blocker: "same-blocker"));
        Assert.True(result.IsStagnating);
        Assert.Contains(result.Signals, x => x.Type == LoopSignalType.RepeatedBlocker);
    }

    [Fact]
    public void Verified_progress_delta_resets_stagnation()
    {
        var observations = RepeatedObservations(task: "same-task").ToArray();
        observations[0] = observations[0] with { VerifiedCompletion = new VerifiedCompletion(10) };
        observations[1] = observations[1] with { VerifiedCompletion = new VerifiedCompletion(10.5m) };
        observations[2] = observations[2] with { VerifiedCompletion = new VerifiedCompletion(11) };
        var result = new StagnationEngine().Analyze(observations);
        Assert.False(result.IsStagnating);
        Assert.Equal(StagnationAction.CONTINUE, result.Action);
    }

    [Fact]
    public void One_safe_reassignment_is_allowed_with_new_strategy()
    {
        var taskId = TaskId.New();
        var result = new ReassignmentPolicy().Evaluate(new ReassignmentAttempt(taskId, "task", "new-strategy", "", 0));
        Assert.True(result.Allowed);
        Assert.Equal(1, result.NewAutomaticReassignmentCount);
    }

    [Fact]
    public void Repeated_identical_reassignment_is_stopped()
    {
        var taskId = TaskId.New();
        var policy = new ReassignmentPolicy();
        Assert.False(policy.Evaluate(new ReassignmentAttempt(taskId, "task", "", "", 0)).Allowed);
        Assert.False(policy.Evaluate(new ReassignmentAttempt(taskId, "task", "another-strategy", "", 1)).Allowed);
    }

    [Fact]
    public void Internal_blocker_routes_to_worker()
    {
        var blocker = new PolicyBlocker("b", BlockerCategory.INTERNAL_FIXABLE, ClosurePriority.P0_VERIFICATION_BLOCKER, "repair", false);
        Assert.Equal(BlockerRoutingAction.ROUTE_TO_WORKER, new BlockerClassifier().Route(blocker).Action);
    }

    [Fact]
    public void External_blocker_reaches_terminal_blocked_state()
    {
        var completion = ActiveCompletion();
        var result = new WaveContinuationPolicy().Decide(new WaveContinuationContext(false, false, false, true, false, completion));
        Assert.Equal(WaveContinuationDecision.BLOCKED_EXTERNAL, result.Decision);
        Assert.Equal(AutopilotState.BLOCKED_EXTERNAL, result.State);
    }

    [Fact]
    public void User_product_decision_creates_attention()
    {
        var blocker = new PolicyBlocker("b", BlockerCategory.PRODUCT_DECISION, ClosurePriority.P1_RELEASE_BLOCKER, "choose product behavior", false);
        var result = new BlockerClassifier().Route(blocker);
        Assert.Equal(BlockerRoutingAction.CREATE_ATTENTION, result.Action);
        Assert.Equal(AttentionCategory.BUSINESS_DECISION_REQUIRED, result.AttentionCategory);
    }

    [Fact]
    public void Global_rate_limit_causes_automatic_pause()
    {
        var result = new SafeRecoveryPolicy().Decide(RecoveryCondition.RATE_LIMITED);
        Assert.Equal(RecoveryAction.GLOBAL_PAUSE_COOLDOWN, result.Action);
        Assert.Equal(AutopilotState.PAUSED, result.State);
        Assert.False(result.CreatesAttention);
        Assert.False(result.AllowSend);
    }

    [Fact]
    public void Submitted_unknown_requires_reconciliation()
    {
        var result = new SafeRecoveryPolicy().Decide(RecoveryCondition.SUBMITTED_UNKNOWN);
        Assert.Equal(RecoveryAction.RECONCILE_BEFORE_RETRY, result.Action);
        Assert.Equal(AutopilotState.WAITING_FOR_EVIDENCE, result.State);
        Assert.False(result.AllowSend);
    }

    [Fact]
    public void Adapter_uncertainty_prevents_send()
    {
        var result = new SafeRecoveryPolicy().Decide(RecoveryCondition.BROWSER_ADAPTER_UNCERTAIN);
        Assert.Equal(RecoveryAction.NO_SEND_RECOVER_ADAPTER, result.Action);
        Assert.False(result.AllowSend);
        Assert.False(result.CreatesAttention);
    }

    [Fact]
    public void Worker_slot_released_after_accepted_completion()
    {
        var slot = new WorkerSlot(new WorkerSlotId(5), LogicalAgentId.New(), TaskId.New(), true);
        var released = new WorkerSlotReusePolicy().ReleaseIfAccepted(slot, TaskState.Completed, HandoffQuality.Valid);
        Assert.Null(released.CurrentTaskId);
        Assert.False(released.IsActive);
        Assert.Equal(slot.LogicalAgentId, released.LogicalAgentId);
    }

    [Fact]
    public void One_hundred_percent_requires_all_mandatory_gates()
    {
        var result = Controller().Evaluate(new ManagerEstimate(100),
            [Gate(CompletionGateFamily.IMPLEMENTATION, GateState.Pass, true, 100, Strong()), Gate(CompletionGateFamily.TESTS, GateState.Unknown, true, 0, Acceptable())], []);
        Assert.True(result.VerifiedCompletion.Percent < 100m);
        Assert.NotEqual(ProjectCompletionMode.VerifiedComplete, result.Mode);
    }

    [Fact]
    public void Missing_e2e_prevents_one_hundred_percent()
    {
        var result = Controller().Evaluate(new ManagerEstimate(100),
            [Gate(CompletionGateFamily.IMPLEMENTATION, GateState.Pass, true, 100, Strong())], [],
            new HashSet<CompletionGateFamily> { CompletionGateFamily.IMPLEMENTATION, CompletionGateFamily.E2E });
        Assert.Contains(CompletionGateFamily.E2E, result.MissingRequiredFamilies);
        Assert.True(result.VerifiedCompletion.Percent < 100m);
    }

    [Fact]
    public void Stalled_terminal_state_carries_reason_and_evidence()
    {
        var record = new ProjectTerminalRecord(ProjectRunId.New(), ProjectTerminalState.STALLED_AUTO_STOPPED, "repeated no-progress evidence", ["same-head", "same-task"], Now);
        Assert.Equal(ProjectTerminalState.STALLED_AUTO_STOPPED, record.State);
        Assert.False(string.IsNullOrWhiteSpace(record.Reason));
        Assert.NotEmpty(record.Evidence);
    }

    [Fact]
    public void Destructive_action_never_silently_approved()
    {
        var result = new DestructiveApprovalGate().Evaluate(DestructiveActionKind.DESTRUCTIVE_GITHUB_OPERATION, false);
        Assert.False(result.Allowed);
        Assert.True(result.RequiresExplicitApproval);
    }

    [Fact]
    public void Routine_notifications_are_suppressed()
    {
        var policy = new SmartNotificationPolicy();
        Assert.False(policy.Evaluate(NotificationEventKind.ROUTINE_RETRY).Notify);
        Assert.False(policy.Evaluate(NotificationEventKind.WORKER_COMPLETED).Notify);
        Assert.True(policy.Evaluate(NotificationEventKind.ATTENTION_REQUIRED).Notify);
    }

    [Fact]
    public void Autopilot_transitions_are_deterministic_and_auditable()
    {
        var run = ProjectRunId.New();
        var record = new AutopilotStateMachine().Transition(run, AutopilotState.AUTOMATIC_STAGED, AutopilotState.CLOSURE_MODE, "verified reached 99", ["gate-ledger"], Now);
        Assert.Equal(AutopilotState.CLOSURE_MODE, record.To);
        Assert.Equal(run, record.ProjectRunId);
        Assert.Throws<InvalidOperationException>(() => new AutopilotStateMachine().Transition(run, AutopilotState.VERIFIED_COMPLETE, AutopilotState.AUTOMATIC_STAGED, "illegal"));
    }

    private static AttentionObservation Observation(AttentionCategory category) =>
        new(ProjectRunId.New(), category, "reason", "action", "resource", null, null, "blocker", "target", false, Now);

    private static EvidenceQualityInput QualityInput(
        DateTimeOffset? capturedAt = null,
        TimeSpan? maxAge = null,
        IReadOnlyList<string>? contradictions = null) =>
        new("github", Head, capturedAt ?? Now, maxAge ?? TimeSpan.FromHours(1), EvidenceCheckResult.PASSED, EvidenceCheckResult.PASSED,
            EvidenceCheckResult.NOT_REQUIRED, EvidenceCheckResult.NOT_REQUIRED, "branch", "branch", "14", "14", 0.95m, true, true, contradictions ?? []);

    private static EvidenceQualityAssessment Strong() => new(EvidenceQuality.STRONG, ["exact-head verified"]);
    private static EvidenceQualityAssessment Acceptable() => new(EvidenceQuality.ACCEPTABLE, ["sufficient"]);
    private static PolicyCompletionGate Gate(CompletionGateFamily family, GateState state, bool mandatory, decimal weight, EvidenceQualityAssessment quality) =>
        new(family, new CompletionGate(family.ToString(), mandatory, weight, state, "evidence"), quality, ClosurePriority.P0_VERIFICATION_BLOCKER);
    private static CompletionGateController Controller() => new();
    private static CompletionControlEvaluation ActiveCompletion() =>
        Controller().Evaluate(new ManagerEstimate(50), [Gate(CompletionGateFamily.IMPLEMENTATION, GateState.Partial, true, 100, Acceptable())], []);

    private static IReadOnlyList<StagnationObservation> RepeatedObservations(string? task = null, string? blocker = null)
    {
        var tasks = new HashSet<string>(task is null ? [] : [task]);
        var blockers = new HashSet<string>(blocker is null ? [] : [blocker]);
        return Enumerable.Range(0, 3).Select(i => new StagnationObservation(
            Now.AddMinutes(i), Head, new HashSet<string>(tasks), new HashSet<string>(blockers), [], ["14:open"], ["evidence"], ["continue"],
            new ManagerEstimate(80), new VerifiedCompletion(80))).ToArray();
    }

    private sealed class MemoryAttentionStore : IAttentionLifecycleStore
    {
        public List<AttentionLifecycleItem> Items { get; } = [];

        public Task<AttentionLifecycleItem?> FindActiveAsync(ProjectRunId projectRunId, string fingerprint, CancellationToken cancellationToken = default) =>
            Task.FromResult(Items.LastOrDefault(x => x.ProjectRunId == projectRunId && x.Fingerprint == fingerprint && x.State is AttentionLifecycleState.OPEN or AttentionLifecycleState.ACKNOWLEDGED));

        public Task UpsertAsync(AttentionLifecycleItem item, CancellationToken cancellationToken = default)
        {
            Items.RemoveAll(x => x.Id == item.Id);
            Items.Add(item);
            return Task.CompletedTask;
        }
    }
}
