using System.Text.Json;
using PCCExecutive.Domain;
using Xunit;

namespace PCCExecutive.Application.Tests;

public sealed class FirstRunApplicationAcceptanceTests
{
    private const string Repository = "walidatiyaai2025-gif/walid";
    private const string Project = "PCCEXECUTIVE";
    private const string Head = "1111111111111111111111111111111111111111";
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 1, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task OneWorkerHappyPathRunsFromProjectSelectionThroughManagerReview()
    {
        var route = Route();
        var baseline = Baseline(route);
        var resolver = new FakeProjectControlResolver(route);
        var resolved = await resolver.ResolveProjectAsync(Project);
        Assert.True(resolved.IsSuccess);
        Assert.Equal(Project, resolved.Project!.ProjectControlId);

        var store = new MemoryOrchestrationStore();
        var coordinator = new ProjectRunCoordinator(store);
        var snapshot = await coordinator.InitializeAsync(ProjectId.New());
        snapshot = await coordinator.EnterManagerPlanningAsync(snapshot);

        var task = Spec(TaskId.New(), "Implement deterministic acceptance", "src/acceptance/one", priority: 1);
        var parsed = new StructuredManagerPlanParser().Parse(PlanJson(route, 45m, "CONTINUE", task));
        Assert.True(parsed.IsValid);
        var plan = Assert.IsType<StructuredManagerPlan>(parsed.Plan);

        var validation = new ManagerWaveValidator().Validate(plan, route, baseline, new EmptyCompletedTaskIndex(), ProjectCompletionMode.Active);
        Assert.True(validation.IsValid);

        var batch = new SafeDispatchPlanner().Schedule(
            plan,
            new Dictionary<TaskId, TaskState>(),
            new HashSet<WorkerSlotId>(),
            HealthyRuntime());
        var assignment = Assert.Single(batch.Assignments);
        Assert.Equal(new WorkerSlotId(1), assignment.SlotId);

        snapshot = await coordinator.AcceptWaveAsync(
            snapshot,
            plan,
            validation,
            new Dictionary<TaskId, WorkerSlotId> { [assignment.TaskId] = assignment.SlotId });
        Assert.Equal(ProjectRunState.WaveReady, snapshot.ProjectRun.State);

        var provider = FakeAgentProvider.Acknowledged();
        var idempotency = new MemoryDispatchStore();
        var dispatchCoordinator = new DispatchCoordinator(provider, FakeSessionGuard.Valid(), idempotency, FakeReconciliation.Unresolved());
        var agentId = LogicalAgentId.New();
        var conversationId = ConversationId.New();
        var prepared = await dispatchCoordinator.PrepareDispatchAsync(
            snapshot.ProjectRun.Id,
            snapshot.CurrentWave!.Id,
            assignment.TaskId,
            agentId,
            conversationId,
            "FIRST-RUN-ACCEPTANCE");
        Assert.False(prepared.ExistingReservation);
        Assert.NotEqual(Guid.Empty, prepared.Dispatch.Id.Value);

        var submitted = await dispatchCoordinator.SubmitDispatchAsync(prepared.Dispatch, "FIRST-RUN-ACCEPTANCE");
        Assert.Equal(DispatchState.ACKNOWLEDGED, submitted.State);
        Assert.Equal(1, provider.SendCount);

        var expected = Assert.Single(plan.Tasks);
        var parsedHandoff = new WorkerHandoffParser().Parse(Handoff(expected.Task.Id, assignment.SlotId, Head));
        var assessed = new WorkerHandoffQualityGate().Validate(parsedHandoff, expected, assignment.SlotId, route, baseline);
        Assert.Equal(HandoffQuality.Valid, assessed.Quality);

        var reconciliation = await new LiveWaveEvidenceReconciler(
                new FakeBaselineBuilder(baseline),
                resolver)
            .ReconcileAsync(Project, baseline, new[] { (expected, assignment.SlotId, parsedHandoff) });
        Assert.True(reconciliation.IsSuccess);
        Assert.NotNull(reconciliation.Value);
        Assert.False(reconciliation.Value!.HasContradiction);
        var liveAssessment = Assert.Single(reconciliation.Value.Handoffs);
        Assert.Equal(HandoffQuality.Valid, liveAssessment.Quality);

        var review = new ManagerReviewPacketBuilder().Build(
            Project,
            snapshot.CurrentWave.Id,
            new[] { (expected, assignment.SlotId, liveAssessment) },
            reconciliation.Value.Live,
            Array.Empty<EvidenceEnvelope>(),
            Array.Empty<CompletionGate>(),
            NormalLoop(),
            Array.Empty<AttentionRequest>(),
            OrchestrationDecision.Continue);
        Assert.Single(review.TaskResults);
        Assert.Equal(expected.Task.Id, review.TaskResults[0].TaskId);

        var activeSlot = new WorkerSlot(assignment.SlotId, agentId, expected.Task.Id, true);
        var released = new WorkerSlotReusePolicy().ReleaseIfAccepted(activeSlot, TaskState.Completed, liveAssessment.Quality);
        Assert.False(released.IsActive);
        Assert.Null(released.CurrentTaskId);
        Assert.Equal(agentId, released.LogicalAgentId);

        var autonomous = new AttentionClassifier().Classify(OperationalCondition.WORKER_COMPLETED);
        Assert.False(autonomous.RequiresAttention);
        Assert.Equal(AutopilotState.AUTOMATIC_STAGED, autonomous.SuggestedState);
        Assert.Equal(ProjectCompletionMode.Active, snapshot.ProjectRun.CompletionMode);
    }

    [Fact]
    public void FiveWorkerHappyPathAssignsExactlyWorkersOneThroughFiveAndConsolidatesResults()
    {
        var route = Route();
        var baseline = Baseline(route);
        var specs = Enumerable.Range(1, 5)
            .Select(i => Spec(TaskId.New(), $"Independent task {i}", $"src/work-{i}", priority: i))
            .ToArray();
        var parsed = new StructuredManagerPlanParser().Parse(PlanJson(route, 50m, "CONTINUE", specs));
        var plan = Assert.IsType<StructuredManagerPlan>(parsed.Plan);
        Assert.True(parsed.IsValid);

        var validation = new ManagerWaveValidator().Validate(plan, route, baseline, new EmptyCompletedTaskIndex(), ProjectCompletionMode.Active);
        Assert.True(validation.IsValid);
        Assert.False(validation.RequiresSequentialization);

        var batch = new SafeDispatchPlanner().Schedule(
            plan,
            new Dictionary<TaskId, TaskState>(),
            new HashSet<WorkerSlotId>(),
            HealthyRuntime());
        Assert.Equal(5, batch.Assignments.Count);
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, batch.Assignments.Select(x => x.SlotId.Value).OrderBy(x => x).ToArray());
        Assert.DoesNotContain(batch.Assignments, x => x.SlotId.Value > WorkerSlotPolicy.MaximumActiveWorkers);

        var gate = new WorkerHandoffQualityGate();
        var results = batch.Assignments.Select(assignment =>
        {
            var expected = plan.Tasks.Single(x => x.Task.Id == assignment.TaskId);
            var parsedHandoff = new WorkerHandoffParser().Parse(Handoff(expected.Task.Id, assignment.SlotId, Head));
            var assessed = gate.Validate(parsedHandoff, expected, assignment.SlotId, route, baseline);
            Assert.Equal(HandoffQuality.Valid, assessed.Quality);
            Assert.Equal(expected.Task.Id, assessed.Handoff!.TaskId);
            return (expected, assignment.SlotId, assessed);
        }).ToArray();

        var review = new ManagerReviewPacketBuilder().Build(
            Project,
            WaveId.New(),
            results,
            baseline,
            Array.Empty<EvidenceEnvelope>(),
            Array.Empty<CompletionGate>(),
            NormalLoop(),
            Array.Empty<AttentionRequest>(),
            OrchestrationDecision.Continue);
        Assert.Equal(5, review.TaskResults.Count);
        Assert.Equal(5, review.TaskResults.Select(x => x.TaskId).Distinct().Count());
        Assert.All(review.TaskResults, x => Assert.Equal(HandoffQuality.Valid, x.Quality));
    }

    [Fact]
    public void ZeroTaskWaveIsValidAndProducesNoAssignments()
    {
        var route = Route();
        var baseline = Baseline(route);
        var parsed = new StructuredManagerPlanParser().Parse(PlanJson(route, 10m, "CONTINUE"));
        var plan = Assert.IsType<StructuredManagerPlan>(parsed.Plan);
        Assert.True(parsed.IsValid);

        var validation = new ManagerWaveValidator().Validate(plan, route, baseline, new EmptyCompletedTaskIndex(), ProjectCompletionMode.Active);
        var batch = new SafeDispatchPlanner().Schedule(plan, new Dictionary<TaskId, TaskState>(), new HashSet<WorkerSlotId>(), HealthyRuntime());
        Assert.True(validation.IsValid);
        Assert.Empty(batch.Assignments);
        Assert.Empty(batch.Deferred);
    }

    [Fact]
    public void SixTaskPlanIsRejectedByWorkerLimit()
    {
        var route = Route();
        var specs = Enumerable.Range(1, 6).Select(i => Spec(TaskId.New(), $"Task {i}", $"src/{i}", i)).ToArray();
        var parsed = new StructuredManagerPlanParser().Parse(PlanJson(route, 20m, "CONTINUE", specs));
        Assert.False(parsed.IsValid);
        Assert.Contains(parsed.Findings, x => x.Code == "WORKER_LIMIT");
        Assert.Equal(FirstRunErrorCode.WORKER_LIMIT, FirstRunErrorContract.FromManagerPlan(parsed));
    }

    [Fact]
    public void DependencyOrderingDefersDependentTaskUntilPrerequisiteCompletes()
    {
        var route = Route();
        var baseline = Baseline(route);
        var first = Spec(TaskId.New(), "Foundation", "src/foundation", 1);
        var second = Spec(TaskId.New(), "Dependent", "src/dependent", 2, dependencies: new[] { first.Id });
        var plan = Assert.IsType<StructuredManagerPlan>(new StructuredManagerPlanParser().Parse(PlanJson(route, 25m, "CONTINUE", first, second)).Plan);
        var validation = new ManagerWaveValidator().Validate(plan, route, baseline, new EmptyCompletedTaskIndex(), ProjectCompletionMode.Active);
        Assert.True(validation.IsValid);

        var scheduler = new SafeDispatchPlanner();
        var firstBatch = scheduler.Schedule(plan, new Dictionary<TaskId, TaskState>(), new HashSet<WorkerSlotId>(), HealthyRuntime());
        Assert.Contains(firstBatch.Assignments, x => x.TaskId == first.Id);
        Assert.Contains(firstBatch.Deferred, x => x.TaskId == second.Id && x.Reason.StartsWith("WAITING_DEPENDENCY", StringComparison.Ordinal));

        var states = new Dictionary<TaskId, TaskState> { [first.Id] = TaskState.Completed };
        var secondBatch = scheduler.Schedule(plan, states, new HashSet<WorkerSlotId>(), HealthyRuntime());
        Assert.Contains(secondBatch.Assignments, x => x.TaskId == second.Id);
    }

    [Fact]
    public void OverlappingTasksAreSequentializedRatherThanDispatchedTogether()
    {
        var route = Route();
        var baseline = Baseline(route);
        var first = Spec(TaskId.New(), "Parent scope", "src/shared", 1);
        var second = Spec(TaskId.New(), "Child scope", "src/shared/child", 2);
        var plan = Assert.IsType<StructuredManagerPlan>(new StructuredManagerPlanParser().Parse(PlanJson(route, 30m, "CONTINUE", first, second)).Plan);
        var validation = new ManagerWaveValidator().Validate(plan, route, baseline, new EmptyCompletedTaskIndex(), ProjectCompletionMode.Active);
        Assert.True(validation.IsValid);
        Assert.True(validation.RequiresSequentialization);
        Assert.Equal(FirstRunErrorCode.OVERLAP, FirstRunErrorContract.FromWaveValidation(validation));

        var batch = new SafeDispatchPlanner().Schedule(plan, new Dictionary<TaskId, TaskState>(), new HashSet<WorkerSlotId>(), HealthyRuntime());
        Assert.Single(batch.Assignments);
        Assert.Single(batch.Deferred);
        Assert.Equal("SEQUENTIALIZED_SCOPE_COLLISION", batch.Deferred[0].Reason);
    }

    [Fact]
    public void DuplicateTaskIdIsRejected()
    {
        var route = Route();
        var id = TaskId.New();
        var parsed = new StructuredManagerPlanParser().Parse(PlanJson(
            route,
            10m,
            "CONTINUE",
            Spec(id, "One", "src/one", 1),
            Spec(id, "Two", "src/two", 2)));
        Assert.False(parsed.IsValid);
        Assert.Contains(parsed.Findings, x => x.Code == "DUPLICATE_TASK_ID");
        Assert.Equal(FirstRunErrorCode.INVALID_MANAGER_PLAN, FirstRunErrorContract.FromManagerPlan(parsed));
    }

    [Fact]
    public void DuplicateFingerprintIsRejectedByWaveValidation()
    {
        var route = Route();
        var baseline = Baseline(route);
        var first = Spec(TaskId.New(), "Same objective", "src/same", 1);
        var second = Spec(TaskId.New(), "Same objective", "src/same", 2);
        var plan = Assert.IsType<StructuredManagerPlan>(new StructuredManagerPlanParser().Parse(PlanJson(route, 10m, "CONTINUE", first, second)).Plan);
        var validation = new ManagerWaveValidator().Validate(plan, route, baseline, new EmptyCompletedTaskIndex(), ProjectCompletionMode.Active);
        Assert.False(validation.IsValid);
        Assert.Contains(validation.Findings, x => x.Code == "DUPLICATE_TASK_FINGERPRINT");
    }

    [Fact]
    public void InvalidHandoffProducesMachineReadableFailure()
    {
        var parsed = new WorkerHandoffParser().Parse("TASK: not-a-guid\nWORKER_SLOT: Worker 1\nPROJECT: PCCEXECUTIVE\nREPOSITORY: walidatiyaai2025-gif/walid\nSTATUS: DONE\nHEAD: N/A\nBRANCH: N/A\nPR: N/A\nCHANGED: file.cs\nTESTS: PASS\nBUILD: PASS\nBLOCKER: N/A\nNEXT_ACTION: none");
        Assert.Equal(HandoffQuality.Invalid, parsed.Quality);
        Assert.Equal(FirstRunErrorCode.HANDOFF_INVALID, FirstRunErrorContract.FromHandoff(parsed));
    }

    [Fact]
    public void StaleHandoffIsDetectedAgainstLivePullRequestEvidence()
    {
        var route = Route();
        var baseline = Baseline(route);
        var task = Spec(TaskId.New(), "Stale PR", "src/stale", 1);
        var plan = Assert.IsType<StructuredManagerPlan>(new StructuredManagerPlanParser().Parse(PlanJson(route, 10m, "CONTINUE", task)).Plan);
        var expected = Assert.Single(plan.Tasks);
        var parsed = new WorkerHandoffParser().Parse(Handoff(expected.Task.Id, new WorkerSlotId(1), Head, pullRequest: 999));
        var assessed = new WorkerHandoffQualityGate().Validate(parsed, expected, new WorkerSlotId(1), route, baseline);
        Assert.Equal(HandoffQuality.Stale, assessed.Quality);
        Assert.Equal(FirstRunErrorCode.STALE_EVIDENCE, FirstRunErrorContract.FromHandoff(assessed));
    }

    [Fact]
    public void EvidenceContradictionIsNotAcceptedAsValidHandoff()
    {
        var route = Route();
        var baseline = Baseline(route);
        var task = Spec(TaskId.New(), "Contradicted head", "src/contradiction", 1);
        var plan = Assert.IsType<StructuredManagerPlan>(new StructuredManagerPlanParser().Parse(PlanJson(route, 10m, "CONTINUE", task)).Plan);
        var expected = Assert.Single(plan.Tasks);
        var parsed = new WorkerHandoffParser().Parse(Handoff(expected.Task.Id, new WorkerSlotId(1), "2222222222222222222222222222222222222222"));
        var assessed = new WorkerHandoffQualityGate().Validate(parsed, expected, new WorkerSlotId(1), route, baseline);
        Assert.Equal(HandoffQuality.ContradictedByLiveEvidence, assessed.Quality);
        Assert.Contains(assessed.Findings, x => x.Code == "HEAD_NOT_LIVE");
        Assert.Equal(FirstRunErrorCode.STALE_EVIDENCE, FirstRunErrorContract.FromHandoff(assessed));
    }

    [Fact]
    public async Task SubmittedUnknownRequiresReconciliationAndNeverBlindlyDuplicates()
    {
        var provider = FakeAgentProvider.Uncertain();
        var idempotency = new MemoryDispatchStore();
        var coordinator = new DispatchCoordinator(provider, FakeSessionGuard.Valid(), idempotency, FakeReconciliation.Unresolved());
        var run = ProjectRunId.New();
        var wave = WaveId.New();
        var task = TaskId.New();
        var agent = LogicalAgentId.New();
        var conversation = ConversationId.New();

        var prepared = await coordinator.PrepareDispatchAsync(run, wave, task, agent, conversation, "uncertain-content");
        var uncertain = await coordinator.SubmitDispatchAsync(prepared.Dispatch, "uncertain-content");
        Assert.Equal(DispatchState.SUBMITTED_UNKNOWN, uncertain.State);
        Assert.Equal(FirstRunErrorCode.SUBMITTED_UNKNOWN, FirstRunErrorContract.FromDispatch(uncertain));

        await idempotency.ReplaceAsync(uncertain);
        var duplicatePreparation = await coordinator.PrepareDispatchAsync(run, wave, task, agent, conversation, "uncertain-content");
        Assert.True(duplicatePreparation.ExistingReservation);
        Assert.True(duplicatePreparation.RequiresReconciliation);
        Assert.Equal(uncertain.Id, duplicatePreparation.Dispatch.Id);

        var stillUnknown = await coordinator.SubmitDispatchAsync(duplicatePreparation.Dispatch, "uncertain-content");
        Assert.Equal(DispatchState.SUBMITTED_UNKNOWN, stillUnknown.State);
        Assert.Equal(1, provider.SendCount);
    }

    [Fact]
    public void GlobalPausePreventsAllNewDispatchAssignments()
    {
        var route = Route();
        var plan = Assert.IsType<StructuredManagerPlan>(new StructuredManagerPlanParser().Parse(
            PlanJson(route, 10m, "CONTINUE", Spec(TaskId.New(), "Paused", "src/paused", 1))).Plan);
        var batch = new SafeDispatchPlanner().Schedule(
            plan,
            new Dictionary<TaskId, TaskState>(),
            new HashSet<WorkerSlotId>(),
            new RuntimeHealthSnapshot(true, true, TimeSpan.FromSeconds(30), "RATE_LIMITED"));
        Assert.Empty(batch.Assignments);
        Assert.Single(batch.Deferred);
        Assert.StartsWith("GLOBAL_RUNTIME_PAUSE", batch.Deferred[0].Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void LoginRequiredCreatesAttentionAndMachineReadableError()
    {
        var classification = new AttentionClassifier().Classify(OperationalCondition.LOGIN_REQUIRED);
        Assert.True(classification.RequiresAttention);
        Assert.Equal(AttentionCategory.LOGIN_REQUIRED, classification.Category);
        Assert.Equal(AutopilotState.ATTENTION_REQUIRED, classification.SuggestedState);
        Assert.Equal(FirstRunErrorCode.LOGIN_REQUIRED, FirstRunErrorContract.FromAttention(classification));
    }

    [Fact]
    public async Task ResolvedLoginAutoResolvesAttentionWhenProviderIsAuthenticatedReady()
    {
        var store = new MemoryAttentionStore();
        var lifecycle = new AttentionLifecycleCoordinator(store);
        var run = ProjectRunId.New();
        var item = await lifecycle.ObserveAsync(new AttentionObservation(
            run,
            AttentionCategory.LOGIN_REQUIRED,
            "Login expired",
            "Sign in",
            "Manager",
            null,
            LogicalAgentId.New(),
            null,
            "owned-session",
            false,
            Now));
        Assert.Equal(AttentionLifecycleState.OPEN, item.State);

        var resolved = await lifecycle.AutoResolveLoginAsync(item, new ProviderHealth(true, true, false, "READY", "authenticated"), Now.AddMinutes(1));
        Assert.Equal(AttentionLifecycleState.AUTO_RESOLVED, resolved.State);
        Assert.NotNull(resolved.ResolvedAt);
        Assert.Single(store.Items);
    }

    [Fact]
    public void NinetyNinePercentVerifiedEntersClosureMode()
    {
        var controller = new CompletionGateController();
        var result = controller.Evaluate(
            new ManagerEstimate(100m),
            new[]
            {
                Gate(CompletionGateFamily.IMPLEMENTATION, GateState.Pass, true, 99m),
                Gate(CompletionGateFamily.UI, GateState.Pending, false, 1m)
            },
            Array.Empty<PolicyBlocker>());
        Assert.Equal(99m, result.VerifiedCompletion.Percent);
        Assert.Equal(ProjectCompletionMode.ClosureMode, result.Mode);
        Assert.NotEqual(ProjectCompletionMode.VerifiedComplete, result.Mode);
    }

    [Fact]
    public void ManagerSayingDoneCannotCreateFalseOneHundredPercent()
    {
        var route = Route();
        var baseline = Baseline(route);
        var parsed = new StructuredManagerPlanParser().Parse(PlanJson(route, 100m, "DONE"));
        var plan = Assert.IsType<StructuredManagerPlan>(parsed.Plan);
        var validation = new ManagerWaveValidator().Validate(plan, route, baseline, new EmptyCompletedTaskIndex(), ProjectCompletionMode.Active);
        var completion = new CompletionGateController().Evaluate(
            plan.ManagerEstimate,
            new[]
            {
                Gate(CompletionGateFamily.IMPLEMENTATION, GateState.Pass, true, 50m),
                Gate(CompletionGateFamily.TESTS, GateState.Fail, true, 50m)
            },
            Array.Empty<PolicyBlocker>());
        Assert.Equal(100m, completion.ManagerEstimate.Percent);
        Assert.True(completion.VerifiedCompletion.Percent < 100m);
        Assert.NotEqual(ProjectCompletionMode.VerifiedComplete, completion.Mode);

        var sanity = new ManagerSanityChecker().Check(plan, validation, baseline, completion.Mode, completion.VerifiedCompletion, NormalLoop());
        Assert.Contains(sanity, x => x.Code == "UNSUPPORTED_COMPLETION");
    }

    [Fact]
    public async Task RecoverySnapshotReconstructsRunWaveAssignmentsDispatchAndManagerReview()
    {
        var route = Route();
        var baseline = Baseline(route);
        var store = new MemoryOrchestrationStore();
        var coordinator = new ProjectRunCoordinator(store);
        var snapshot = await coordinator.InitializeAsync(ProjectId.New());
        snapshot = await coordinator.EnterManagerPlanningAsync(snapshot);

        var spec = Spec(TaskId.New(), "Recoverable task", "src/recovery-contract", 1);
        var plan = Assert.IsType<StructuredManagerPlan>(new StructuredManagerPlanParser().Parse(PlanJson(route, 40m, "CONTINUE", spec)).Plan);
        var validation = new ManagerWaveValidator().Validate(plan, route, baseline, new EmptyCompletedTaskIndex(), ProjectCompletionMode.Active);
        var assignment = new WorkerSlotId(1);
        snapshot = await coordinator.AcceptWaveAsync(snapshot, plan, validation, new Dictionary<TaskId, WorkerSlotId> { [spec.Id] = assignment });

        var dispatch = new Dispatch(
            DispatchId.New(), snapshot.ProjectRun.Id, snapshot.CurrentWave!.Id, spec.Id, LogicalAgentId.New(), ConversationId.New(),
            "hash", Now, DispatchState.ACKNOWLEDGED, Now, Now, null, null, "ack");
        var expected = Assert.Single(plan.Tasks);
        var assessed = new WorkerHandoffQualityGate().Validate(
            new WorkerHandoffParser().Parse(Handoff(spec.Id, assignment, Head)), expected, assignment, route, baseline);
        var review = new ManagerReviewPacketBuilder().Build(
            Project, snapshot.CurrentWave.Id, new[] { (expected, assignment, assessed) }, baseline,
            Array.Empty<EvidenceEnvelope>(), Array.Empty<CompletionGate>(), NormalLoop(), Array.Empty<AttentionRequest>(), OrchestrationDecision.Continue);
        var persisted = snapshot with
        {
            Dispatches = new[] { dispatch },
            ManagerReview = review,
            Phase = OrchestrationPhase.ManagerReview,
            SavedAt = Now
        };
        await store.SaveAsync(persisted);

        var restored = await coordinator.RestoreAsync(snapshot.ProjectRun.Id);
        Assert.NotNull(restored);
        Assert.Equal(snapshot.ProjectRun.Id, restored!.ProjectRun.Id);
        Assert.Equal(snapshot.CurrentWave.Id, restored.CurrentWave!.Id);
        Assert.Equal(spec.Id, Assert.Single(restored.Tasks).Id);
        Assert.Equal(assignment, restored.Assignments[spec.Id]);
        Assert.Equal(dispatch.Id, Assert.Single(restored.Dispatches).Id);
        Assert.Equal(spec.Id, Assert.Single(restored.ManagerReview!.TaskResults).TaskId);
    }

    [Fact]
    public void EmptyFirstRunStateIsSafeAndExceptionFree()
    {
        var state = FirstRunApplicationState.Empty;
        Assert.True(state.IsSafeIdle);
        Assert.False(state.ProjectSelected);
        Assert.False(state.HasActiveWave);
        Assert.Equal(0, state.ActiveWorkerTasks);
        Assert.False(state.BrowserConnected);
        Assert.Equal(0, state.AttentionCount);
        Assert.False(state.UpdateAvailable);
    }

    [Fact]
    public void WorkerSlotIsReusableOnlyAfterAcceptedCompletion()
    {
        var task = TaskId.New();
        var agent = LogicalAgentId.New();
        var active = new WorkerSlot(new WorkerSlotId(1), agent, task, true);
        var policy = new WorkerSlotReusePolicy();

        var notReleased = policy.ReleaseIfAccepted(active, TaskState.Running, HandoffQuality.Valid);
        Assert.True(notReleased.IsActive);
        var released = policy.ReleaseIfAccepted(active, TaskState.Completed, HandoffQuality.Valid);
        Assert.False(released.IsActive);
        Assert.Null(released.CurrentTaskId);
        Assert.Equal(agent, released.LogicalAgentId);
    }

    [Fact]
    public async Task WrongSessionRejectionPropagatesWithoutCallingProvider()
    {
        var provider = FakeAgentProvider.Acknowledged();
        var coordinator = new DispatchCoordinator(provider, FakeSessionGuard.Invalid("WRONG_SESSION"), new MemoryDispatchStore(), FakeReconciliation.Unresolved());
        var prepared = await coordinator.PrepareDispatchAsync(ProjectRunId.New(), WaveId.New(), TaskId.New(), LogicalAgentId.New(), ConversationId.New(), "wrong-session");
        var result = await coordinator.SubmitDispatchAsync(prepared.Dispatch, "wrong-session");
        Assert.Equal(DispatchState.FAILED, result.State);
        Assert.StartsWith("SESSION_INVALID:WRONG_SESSION", result.ReconciliationEvidence, StringComparison.Ordinal);
        Assert.Equal(0, provider.SendCount);
    }

    [Fact]
    public void RecoverableFailureDoesNotCreateHumanAttention()
    {
        var tempError = new AttentionClassifier().Classify(OperationalCondition.TEMP_ERROR);
        Assert.False(tempError.RequiresAttention);
        Assert.Equal(AutopilotState.RECOVERING, tempError.SuggestedState);
        var recovery = new SafeRecoveryPolicy().Decide(RecoveryCondition.TEMP_ERROR);
        Assert.False(recovery.CreatesAttention);
        Assert.Equal(RecoveryAction.RETRY_SAFE, recovery.Action);
    }

    [Fact]
    public void OneHundredPercentRequiresAllMandatoryGatesToPass()
    {
        var controller = new CompletionGateController();
        var passed = controller.Evaluate(
            new ManagerEstimate(70m),
            new[]
            {
                Gate(CompletionGateFamily.IMPLEMENTATION, GateState.Pass, true, 50m),
                Gate(CompletionGateFamily.TESTS, GateState.Pass, true, 50m)
            },
            Array.Empty<PolicyBlocker>());
        Assert.Equal(100m, passed.VerifiedCompletion.Percent);
        Assert.Equal(ProjectCompletionMode.VerifiedComplete, passed.Mode);

        var pending = controller.Evaluate(
            new ManagerEstimate(100m),
            new[]
            {
                Gate(CompletionGateFamily.IMPLEMENTATION, GateState.Pass, true, 50m),
                Gate(CompletionGateFamily.TESTS, GateState.Pending, true, 50m)
            },
            Array.Empty<PolicyBlocker>());
        Assert.True(pending.VerifiedCompletion.Percent < 100m);
        Assert.NotEqual(ProjectCompletionMode.VerifiedComplete, pending.Mode);
    }

    [Fact]
    public void MachineReadableErrorContractCoversFirstTestFailures()
    {
        Assert.Equal(FirstRunErrorCode.PROJECT_NOT_FOUND, FirstRunErrorContract.FromProjectResolution(ProjectResolutionStatus.ProjectNotFound));
        Assert.Equal(FirstRunErrorCode.ROUTING_NOT_READY, FirstRunErrorContract.FromProjectResolution(ProjectResolutionStatus.RoutingNotReady));

        var invalidPlan = new StructuredManagerPlanParser().Parse("not-json");
        Assert.Equal(FirstRunErrorCode.INVALID_MANAGER_PLAN, FirstRunErrorContract.FromManagerPlan(invalidPlan));

        var dependencyFinding = new OrchestrationWaveValidation(false, false, new[] { new ManagerPlanFinding("DEPENDENCY_CYCLE", "cycle", PlanFindingSeverity.Block) });
        Assert.Equal(FirstRunErrorCode.DEPENDENCY_CYCLE, FirstRunErrorContract.FromWaveValidation(dependencyFinding));
        var overlapFinding = new OrchestrationWaveValidation(true, true, new[] { new ManagerPlanFinding("OVERLAPPING_SCOPE", "overlap", PlanFindingSeverity.Sequentialize) });
        Assert.Equal(FirstRunErrorCode.OVERLAP, FirstRunErrorContract.FromWaveValidation(overlapFinding));

        var partial = new HandoffAssessment(HandoffQuality.Partial, null, new[] { new HandoffFinding("FIELD_MISSING", "missing") });
        Assert.Equal(FirstRunErrorCode.HANDOFF_INVALID, FirstRunErrorContract.FromHandoff(partial));
        var stale = new HandoffAssessment(HandoffQuality.Stale, null, Array.Empty<HandoffFinding>());
        Assert.Equal(FirstRunErrorCode.STALE_EVIDENCE, FirstRunErrorContract.FromHandoff(stale));

        var unknownDispatch = new Dispatch(
            DispatchId.New(), ProjectRunId.New(), WaveId.New(), TaskId.New(), LogicalAgentId.New(), ConversationId.New(), "hash", Now,
            DispatchState.SUBMITTED_UNKNOWN, Now, null, null, null, "unknown");
        Assert.Equal(FirstRunErrorCode.SUBMITTED_UNKNOWN, FirstRunErrorContract.FromDispatch(unknownDispatch));

        var login = new AttentionClassifier().Classify(OperationalCondition.LOGIN_REQUIRED);
        Assert.Equal(FirstRunErrorCode.LOGIN_REQUIRED, FirstRunErrorContract.FromAttention(login));
        var external = new PolicyBlocker("external", BlockerCategory.EXTERNAL_SERVICE, ClosurePriority.P0_VERIFICATION_BLOCKER, "external unavailable", false);
        Assert.Equal(FirstRunErrorCode.BLOCKED_EXTERNAL, FirstRunErrorContract.FromBlocker(external));

        var names = Enum.GetNames<FirstRunErrorCode>();
        Assert.Contains("PROJECT_NOT_FOUND", names);
        Assert.Contains("ROUTING_NOT_READY", names);
        Assert.Contains("INVALID_MANAGER_PLAN", names);
        Assert.Contains("WORKER_LIMIT", names);
        Assert.Contains("DEPENDENCY_CYCLE", names);
        Assert.Contains("OVERLAP", names);
        Assert.Contains("STALE_EVIDENCE", names);
        Assert.Contains("HANDOFF_INVALID", names);
        Assert.Contains("SUBMITTED_UNKNOWN", names);
        Assert.Contains("LOGIN_REQUIRED", names);
        Assert.Contains("BLOCKED_EXTERNAL", names);
    }

    private static RuntimeHealthSnapshot HealthyRuntime() => new(false, true, TimeSpan.FromSeconds(10), null);
    private static LoopAssessment NormalLoop() => new(LoopGuardLevel.Normal, Array.Empty<LoopSignal>());

    private static PolicyCompletionGate Gate(CompletionGateFamily family, GateState state, bool mandatory, decimal weight) =>
        new(family, new CompletionGate(family.ToString(), mandatory, weight, state, "acceptance-evidence"), AcceptableEvidence(), ClosurePriority.P0_VERIFICATION_BLOCKER);

    private static EvidenceQualityAssessment AcceptableEvidence() =>
        new(EvidenceQuality.ACCEPTABLE, new[] { "deterministic acceptance evidence" });

    private static TaskSpec Spec(TaskId id, string objective, string path, int priority, IReadOnlyList<TaskId>? dependencies = null) =>
        new(id, objective, path, priority, dependencies ?? Array.Empty<TaskId>());

    private static string PlanJson(ProjectRoutingSnapshot route, decimal estimate, string decision, params TaskSpec[] tasks) =>
        JsonSerializer.Serialize(new
        {
            managerEstimate = estimate,
            expectedHead = Head,
            expectedRoutingIdentity = route.RoutingIdentity,
            projectDecision = decision,
            knownBlockers = Array.Empty<string>(),
            tasks = tasks.Select(x => new
            {
                taskId = x.Id.Value,
                objective = x.Objective,
                repository = Repository,
                paths = new[] { x.Path },
                components = Array.Empty<string>(),
                exclusiveResources = Array.Empty<string>(),
                dependencies = x.Dependencies.Select(d => d.Value).ToArray(),
                acceptanceCriteria = new[] { "deterministic acceptance passes" },
                evidenceExpected = new[] { "exact task association" },
                priority = x.Priority,
                suggestedWorkerSlot = (int?)null,
                reason = "first-run acceptance",
                knownBlockers = Array.Empty<string>(),
                requiredPreviousTasks = Array.Empty<Guid>(),
                recommendedExecutionMode = "AutomaticStaged",
                targetScope = "Project",
                targetVariant = (string?)null,
                expectedHead = Head,
                relatedPullRequest = (int?)null,
                expectedPullRequestState = (string?)null,
                targetBranch = (string?)null,
                featureExpansion = false
            }).ToArray()
        });

    private static string Handoff(TaskId taskId, WorkerSlotId slot, string head, int? pullRequest = null) =>
        string.Join('\n',
            $"TASK: {taskId.Value}",
            $"WORKER_SLOT: Worker {slot.Value}",
            $"PROJECT: {Project}",
            $"REPOSITORY: {Repository}",
            "STATUS: DONE",
            $"HEAD: {head}",
            "BRANCH: task/pcc-executive-t0001-v1",
            $"PR: {(pullRequest is null ? "N/A" : $"#{pullRequest.Value}")}",
            $"CHANGED: tests/acceptance-{slot.Value}.cs",
            "TESTS: PASS",
            "BUILD: PASS",
            "BLOCKER: N/A",
            "NEXT_ACTION: manager-review");

    private static ProjectRoutingSnapshot Route()
    {
        var provenance = new ProjectControlProvenance(
            "walidatiyaai2025-gif/project-control-center",
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "1.6.0",
            "v1",
            Now,
            EvidenceFreshness.Current);
        return new ProjectRoutingSnapshot(
            Project,
            "PCC Executive",
            Repository,
            ProjectModel.Standalone,
            ProjectScopeKind.Project,
            null,
            null,
            null,
            "READY",
            "READY",
            new[] { "PCC Executive" },
            Array.Empty<CanonicalTaskSnapshot>(),
            null,
            provenance);
    }

    private static ProjectBaselineSnapshot Baseline(ProjectRoutingSnapshot route, string head = Head) =>
        new(
            route.ProjectControlId,
            route.DisplayName,
            route.Repository,
            route.ProjectModel,
            route.Scope,
            route.VariantId,
            route.ImplementationLocation,
            route.Provenance.SourceSha,
            route.RoutingIdentity,
            "main",
            head,
            route.CanonicalTasks,
            Array.Empty<GitHubPullRequestSnapshot>(),
            new GitHubCheckSummary(Repository, head, "success", new[] { new GitHubCheckSnapshot("acceptance", "completed", "success", null) }),
            route.DesiredState,
            null,
            Array.Empty<string>(),
            Now,
            EvidenceFreshness.Current);

    private sealed record TaskSpec(TaskId Id, string Objective, string Path, int Priority, IReadOnlyList<TaskId> Dependencies);

    private sealed class EmptyCompletedTaskIndex : ICompletedTaskIndex
    {
        public bool IsCompleted(TaskId taskId) => false;
        public bool ContainsFingerprint(string fingerprint) => false;
    }

    private sealed class FakeProjectControlResolver : IProjectControlResolver
    {
        private readonly ProjectRoutingSnapshot _route;
        public FakeProjectControlResolver(ProjectRoutingSnapshot route) => _route = route;

        public Task<ProjectResolution> ResolveProjectAsync(string nameOrAlias, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Equals(nameOrAlias, _route.ProjectControlId, StringComparison.OrdinalIgnoreCase)
                ? new ProjectResolution(ProjectResolutionStatus.Success, _route, null)
                : new ProjectResolution(ProjectResolutionStatus.ProjectNotFound, null, "PROJECT_NOT_FOUND"));

        public Task<ProjectResolution> GetProjectAsync(string projectControlId, string? variantId = null, ProjectScopeKind? scope = null, CancellationToken cancellationToken = default) =>
            ResolveProjectAsync(projectControlId, cancellationToken);

        public Task<ExternalResult<ProjectRoutingSnapshot>> GetRoutingSnapshotAsync(string projectControlId, string? variantId = null, ProjectScopeKind? scope = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExternalResult<ProjectRoutingSnapshot>(ExternalReadStatus.Success, _route, Now));

        public Task<ExternalResult<IReadOnlyList<CanonicalTaskSnapshot>>> GetCanonicalTasksAsync(string projectControlId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExternalResult<IReadOnlyList<CanonicalTaskSnapshot>>(ExternalReadStatus.Success, _route.CanonicalTasks, Now));

        public Task<ExternalResult<DesiredStateSnapshot>> GetDesiredStateAsync(string projectControlId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExternalResult<DesiredStateSnapshot>(ExternalReadStatus.NotFound, null, Now));
    }

    private sealed class FakeBaselineBuilder : IProjectBaselineBuilder
    {
        private readonly ProjectBaselineSnapshot _baseline;
        public FakeBaselineBuilder(ProjectBaselineSnapshot baseline) => _baseline = baseline;
        public Task<ExternalResult<ProjectBaselineSnapshot>> BuildAsync(string nameOrAlias, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExternalResult<ProjectBaselineSnapshot>(ExternalReadStatus.Success, _baseline, _baseline.CapturedAt));
    }

    private sealed class MemoryOrchestrationStore : IOrchestrationStateStore
    {
        private readonly Dictionary<ProjectRunId, OrchestrationRecoverySnapshot> _items = new();
        public Task SaveAsync(OrchestrationRecoverySnapshot snapshot, CancellationToken cancellationToken = default)
        {
            _items[snapshot.ProjectRun.Id] = snapshot;
            return Task.CompletedTask;
        }

        public Task<OrchestrationRecoverySnapshot?> LoadAsync(ProjectRunId projectRunId, CancellationToken cancellationToken = default)
        {
            _items.TryGetValue(projectRunId, out var snapshot);
            return Task.FromResult(snapshot);
        }
    }

    private sealed class MemoryAttentionStore : IAttentionLifecycleStore
    {
        public List<AttentionLifecycleItem> Items { get; } = new();

        public Task<AttentionLifecycleItem?> FindActiveAsync(ProjectRunId projectRunId, string fingerprint, CancellationToken cancellationToken = default)
        {
            var item = Items.FirstOrDefault(x =>
                x.ProjectRunId == projectRunId &&
                string.Equals(x.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase) &&
                x.State is not (AttentionLifecycleState.RESOLVED or AttentionLifecycleState.AUTO_RESOLVED or AttentionLifecycleState.SUPERSEDED));
            return Task.FromResult(item);
        }

        public Task UpsertAsync(AttentionLifecycleItem item, CancellationToken cancellationToken = default)
        {
            var index = Items.FindIndex(x => x.Id == item.Id);
            if (index >= 0) Items[index] = item;
            else Items.Add(item);
            return Task.CompletedTask;
        }
    }

    private sealed class MemoryDispatchStore : IDispatchIdempotencyStore
    {
        private readonly List<Dispatch> _items = new();

        public Task<Dispatch?> FindEquivalentAsync(ProjectRunId projectRunId, LogicalAgentId logicalAgentId, string contentHash, CancellationToken cancellationToken = default) =>
            Task.FromResult(_items.LastOrDefault(x => x.ProjectRunId == projectRunId && x.LogicalAgentId == logicalAgentId && string.Equals(x.ContentHash, contentHash, StringComparison.OrdinalIgnoreCase)));

        public Task ReserveAsync(Dispatch dispatch, CancellationToken cancellationToken = default)
        {
            _items.Add(dispatch);
            return Task.CompletedTask;
        }

        public Task ReplaceAsync(Dispatch dispatch)
        {
            var index = _items.FindIndex(x => x.Id == dispatch.Id);
            if (index >= 0) _items[index] = dispatch;
            else _items.Add(dispatch);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAgentProvider : IAgentProvider
    {
        private readonly Func<AgentRequest, AgentResult> _result;
        private FakeAgentProvider(Func<AgentRequest, AgentResult> result) => _result = result;
        public AgentProviderKind Kind => AgentProviderKind.BrowserChat;
        public int SendCount { get; private set; }

        public static FakeAgentProvider Acknowledged() => new(request => new AgentResult(request.DispatchId, true, false, false, false, null, "ACKNOWLEDGED", null));
        public static FakeAgentProvider Uncertain() => new(request => new AgentResult(request.DispatchId, true, false, false, true, null, "SUBMITTED_UNKNOWN", "SUBMITTED_UNKNOWN"));

        public Task<ProviderHealth> ProbeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderHealth(true, true, false, "READY", "fake-provider"));

        public Task<AgentResult> SendAsync(AgentRequest request, CancellationToken cancellationToken = default)
        {
            SendCount++;
            return Task.FromResult(_result(request));
        }
    }

    private sealed class FakeSessionGuard : IAgentSessionGuard
    {
        private readonly SessionValidationResult _result;
        private FakeSessionGuard(SessionValidationResult result) => _result = result;
        public static FakeSessionGuard Valid() => new(new SessionValidationResult(true, "MATCH", null));
        public static FakeSessionGuard Invalid(string code) => new(new SessionValidationResult(false, "MISMATCH", code));

        public Task<SessionValidationResult> ValidateAsync(ProjectRunId runId, LogicalAgentId agentId, ConversationId conversationId, TaskId taskId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_result);
    }

    private sealed class FakeReconciliation : IDispatchReconciliationService
    {
        private readonly DispatchReconciliationResult _result;
        private FakeReconciliation(DispatchReconciliationResult result) => _result = result;
        public static FakeReconciliation Unresolved() => new(new DispatchReconciliationResult(DispatchState.SUBMITTED_UNKNOWN, "RECONCILIATION_REQUIRED", false));
        public Task<DispatchReconciliationResult> ReconcileAsync(Dispatch dispatch, CancellationToken cancellationToken = default) => Task.FromResult(_result);
    }
}
