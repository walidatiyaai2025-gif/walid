using System.Text.Json;
using PCCExecutive.Application;
using PCCExecutive.Browser;
using PCCExecutive.Domain;
using PCCExecutive.Infrastructure;
using Xunit;

namespace PCCExecutive.E2E;

public sealed class Final32StageE2ETests : IAsyncLifetime
{
    private const string Project = "PCCEXECUTIVE";
    private const string Repository = "walidatiyaai2025-gif/walid";
    private const string Head = "1111111111111111111111111111111111111111";
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 10, 30, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pcc-final-e2e", Guid.NewGuid().ToString("N"));

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_root, true); } catch { }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Final_32_stage_path_uses_production_orchestration_recovery_and_completion_services()
    {
        var proven = new SortedSet<int>();
        var db = Path.Combine(_root, "final32.db");
        Assert.False(File.Exists(db));
        Mark(1);

        await using var store = new SqliteStateStore(db);
        await store.InitializeAsync();
        Assert.True(File.Exists(db));
        Assert.Equal(1, await store.GetSchemaVersionAsync());
        Mark(2);

        var route = Route();
        var baseline = Baseline(route);
        var pcc = new ControlledProjectControl(route);
        var resolution = await pcc.ResolveProjectAsync(Project);
        Assert.True(resolution.IsSuccess);
        Assert.Equal(Repository, resolution.Project!.Repository);
        Mark(3);

        var orchestrationStore = new SqliteOrchestrationStateStore(store);
        var coordinator = new ProjectRunCoordinator(orchestrationStore);
        var snapshot = await coordinator.InitializeAsync(ProjectId.New());
        Assert.Equal(ProjectRunState.Initializing, snapshot.ProjectRun.State);
        Mark(4);

        var lockIdentity = route.RoutingIdentity + "|" + Guid.NewGuid().ToString("N");
        using (var owner = ProjectRunLock.TryAcquire(lockIdentity))
        using (var rejected = ProjectRunLock.TryAcquire(lockIdentity))
        {
            Assert.True(owner.IsOwned);
            Assert.False(rejected.IsOwned);
        }
        Mark(5);

        snapshot = await coordinator.EnterManagerPlanningAsync(snapshot);
        var managerAgent = LogicalAgentId.New();
        var managerConversation = ConversationId.New();
        await store.SaveLogicalAgentAsync(new LogicalAgentSession(managerAgent, snapshot.ProjectRun.Id, AgentRole.Manager, null, null, managerConversation, LogicalSessionState.Active));
        Mark(6);

        var workerAgents = Enumerable.Range(1, 5).Select(_ => LogicalAgentId.New()).ToArray();
        var workerConversations = Enumerable.Range(1, 5).Select(_ => ConversationId.New()).ToArray();
        for (var i = 0; i < 5; i++)
            await store.SaveLogicalAgentAsync(new LogicalAgentSession(workerAgents[i], snapshot.ProjectRun.Id, AgentRole.Worker, new WorkerSlotId(i + 1), null, workerConversations[i], LogicalSessionState.Active));
        var recoveredAgents = (await new FullDurabilityRecoveryService(store, orchestrationStore).ReconstructAsync(snapshot.ProjectRun.Id))?.LogicalSessions ?? [];
        Assert.Equal(6, recoveredAgents.Count);
        Mark(7);

        var baselineBuilder = new ControlledBaselineBuilder(baseline);
        var managerContext = await baselineBuilder.BuildAsync(Project);
        Assert.True(managerContext.IsSuccess);
        Assert.Equal(route.RoutingIdentity, managerContext.Value!.RoutingIdentity);
        Mark(8);

        var managerProvider = ControlledAgentProvider.Completed();
        var dispatchCoordinator = new DispatchCoordinator(managerProvider, new ValidSessionGuard(), new MemoryDispatchStore(), new NeverRetryReconciliation());
        var managerTask = TaskId.New();
        var preparedManager = await dispatchCoordinator.PrepareDispatchAsync(snapshot.ProjectRun.Id, WaveId.New(), managerTask, managerAgent, managerConversation, "manager-plan-request");
        var managerResult = await dispatchCoordinator.SubmitDispatchAsync(preparedManager.Dispatch, "manager-plan-request");
        Assert.Equal(1, managerProvider.SendCount);
        Mark(9);
        Assert.Equal(PCCExecutive.Domain.DispatchState.COMPLETED, managerResult.State);
        Mark(10);

        var specs = Enumerable.Range(1, 5).Select(i => Spec(TaskId.New(), $"Independent worker task {i}", $"tests/final32/work-{i}", i)).ToArray();
        var parsed = new StructuredManagerPlanParser().Parse(PlanJson(route, 70m, "CONTINUE", specs));
        Assert.True(parsed.IsValid);
        var plan = Assert.IsType<StructuredManagerPlan>(parsed.Plan);
        Mark(11);

        var validation = new ManagerWaveValidator().Validate(plan, route, baseline, EmptyCompletedTaskIndex.Instance, ProjectCompletionMode.Active);
        Assert.True(validation.IsValid);
        var assignments = new SafeDispatchPlanner().Schedule(plan, new Dictionary<TaskId, TaskState>(), new HashSet<WorkerSlotId>(), HealthyRuntime());
        Assert.Equal(5, assignments.Assignments.Count);
        snapshot = await coordinator.AcceptWaveAsync(snapshot, plan, validation, assignments.Assignments.ToDictionary(x => x.TaskId, x => x.SlotId));
        Assert.Equal(1, snapshot.CurrentWave!.Sequence);
        Mark(12);
        Assert.DoesNotContain(validation.Findings, x => x.Code.StartsWith("DEPENDENCY", StringComparison.Ordinal));
        Mark(13);
        Assert.False(validation.RequiresSequentialization);
        Mark(14);
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, assignments.Assignments.Select(x => x.SlotId.Value).OrderBy(x => x).ToArray());
        Mark(15);

        var workerProvider = ControlledAgentProvider.Completed();
        var workerOrchestrator = new ManagerWorkerOrchestrator(workerProvider, baseDispatchInterval: TimeSpan.Zero);
        var bindings = assignments.Assignments.OrderBy(x => x.SlotId.Value)
            .Select(x => new WorkerExecutionBinding(x.SlotId, workerAgents[x.SlotId.Value - 1], workerConversations[x.SlotId.Value - 1]))
            .ToArray();
        var wavePlan = new WavePlan(snapshot.CurrentWave.Id, plan.ManagerEstimate, plan.Tasks.Select(x => x.Task).ToArray(), []);
        var dispatched = await workerOrchestrator.DispatchWaveAsync(snapshot.ProjectRun.Id, wavePlan, bindings, EmptyCompletedTaskIndex.Instance);
        Assert.True(dispatched.IsAccepted);
        Assert.Equal(5, dispatched.Dispatches.Count);
        Mark(16);

        var scheduler = new BrowserDispatchScheduler();
        var options = new DispatchSchedulerOptions(DispatchMode.AutomaticStaged, TimeSpan.FromSeconds(10), true, 5);
        Assert.True(scheduler.Evaluate(Now, null, 0, options, new GlobalBrowserSendGate().Snapshot).MayDispatch);
        Assert.False(scheduler.Evaluate(Now.AddSeconds(9), Now, 1, options, new GlobalBrowserSendGate().Snapshot).MayDispatch);
        Assert.True(scheduler.Evaluate(Now.AddSeconds(10), Now, 1, options, new GlobalBrowserSendGate().Snapshot).MayDispatch);
        Mark(17);

        Assert.Equal(5, workerProvider.Requests.Count);
        Assert.Equal(plan.Tasks.Select(x => x.Task.Id).OrderBy(x => x.Value), workerProvider.Requests.Select(x => x.TaskId!.Value).OrderBy(x => x.Value));
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, workerProvider.Requests.Select(x => x.WorkerSlotId!.Value).OrderBy(x => x).ToArray());
        Mark(18);

        var wrongChat = new WrongChatGuard().Evaluate(
            Runtime(snapshot.ProjectRun.Id, workerAgents[0], plan.Tasks[0].Task.Id, workerConversations[0], "1"),
            new BrowserDispatchExpectation(snapshot.ProjectRun.Id.ToString(), workerAgents[0].ToString(), plan.Tasks[0].Task.Id.ToString(), workerConversations[0].ToString(), "provider-other", "1"),
            Semantic(ConversationMatch.Mismatch));
        Assert.False(wrongChat.MaySend);
        Mark(19);

        Assert.All(dispatched.Dispatches, item => Assert.True(item.Result.IsComplete));
        Mark(20);
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(plan.Tasks[i].Task.Id, dispatched.Dispatches[i].Task.Id);
            Assert.Equal(new WorkerSlotId(i + 1), dispatched.Dispatches[i].Binding.SlotId);
        }
        Mark(21);

        var domainHandoffs = plan.Tasks.Select(x => new WorkerHandoff(x.Task.Id, "DONE", Head, [x.Task.Scope.Paths.Single()], ["PASS"], null, "manager-review", Now)).ToArray();
        var handoffValidator = new WorkerHandoffValidator();
        for (var i = 0; i < 5; i++) Assert.True(handoffValidator.Validate(plan.Tasks[i].Task, domainHandoffs[i]).IsValid);
        Mark(22);

        var strict = new List<(ManagerTaskProposal Expected, WorkerSlotId Slot, HandoffAssessment Parsed)>();
        var qualityGate = new WorkerHandoffQualityGate();
        for (var i = 0; i < 5; i++)
        {
            var slot = new WorkerSlotId(i + 1);
            var parsedHandoff = new WorkerHandoffParser().Parse(Handoff(plan.Tasks[i].Task.Id, slot, Head));
            var assessed = qualityGate.Validate(parsedHandoff, plan.Tasks[i], slot, route, baseline);
            Assert.Equal(HandoffQuality.Valid, assessed.Quality);
            strict.Add((plan.Tasks[i], slot, parsedHandoff));
        }
        var live = await new LiveWaveEvidenceReconciler(baselineBuilder, pcc).ReconcileAsync(Project, baseline, strict);
        Assert.True(live.IsSuccess);
        Assert.False(live.Value!.HasContradiction);
        Mark(23);

        var completion = new CompletionGateController().Evaluate(
            new ManagerEstimate(100),
            [Gate(CompletionGateFamily.IMPLEMENTATION, GateState.Pass, 50), Gate(CompletionGateFamily.TESTS, GateState.Pass, 50)],
            []);
        Assert.Equal(100m, completion.VerifiedCompletion.Percent);
        Assert.Equal(ProjectCompletionMode.VerifiedComplete, completion.Mode);
        Mark(24);

        var evidence = plan.Tasks.Select(x => new EvidenceRecord(EvidenceId.New(), snapshot.ProjectRun.Id, x.Task.Id, "test", "deterministic-e2e", x.Task.Fingerprint, Head, Now)).ToArray();
        var waveReview = workerOrchestrator.Reconcile(wavePlan, domainHandoffs, evidence);
        Assert.Contains("5/5 handoffs accepted", waveReview.ConsolidatedSummary, StringComparison.Ordinal);
        Mark(25);

        var assessedForReview = strict.Select(x => (x.Expected, x.Slot, qualityGate.Validate(x.Parsed, x.Expected, x.Slot, route, baseline))).ToArray();
        var managerReview = new ManagerReviewPacketBuilder().Build(Project, snapshot.CurrentWave.Id, assessedForReview, baseline, [], [], NormalLoop(), [], OrchestrationDecision.Continue);
        Assert.Equal(5, managerReview.TaskResults.Count);
        Assert.All(managerReview.TaskResults, x => Assert.Equal(HandoffQuality.Valid, x.Quality));
        Mark(26);

        snapshot = await coordinator.SetPhaseAsync(snapshot, OrchestrationPhase.ManagerReview, ProjectRunState.ManagerReview);
        snapshot = await coordinator.EnterManagerPlanningAsync(snapshot);
        var secondSpec = Spec(TaskId.New(), "Second wave after verified release", "tests/final32/second-wave", 1);
        var secondPlan = Assert.IsType<StructuredManagerPlan>(new StructuredManagerPlanParser().Parse(PlanJson(route, 85, "CONTINUE", secondSpec)).Plan);
        var secondValidation = new ManagerWaveValidator().Validate(secondPlan, route, baseline, EmptyCompletedTaskIndex.Instance, ProjectCompletionMode.Active);
        var secondAssignment = Assert.Single(new SafeDispatchPlanner().Schedule(secondPlan, new Dictionary<TaskId, TaskState>(), new HashSet<WorkerSlotId>(), HealthyRuntime()).Assignments);
        snapshot = await coordinator.AcceptWaveAsync(snapshot, secondPlan, secondValidation, new Dictionary<TaskId, WorkerSlotId> { [secondSpec.Id] = secondAssignment.SlotId });
        Assert.Equal(2, snapshot.CurrentWave!.Sequence);
        Mark(27);

        var reusable = new WorkerSlotReusePolicy().ReleaseIfAccepted(new WorkerSlot(new WorkerSlotId(1), workerAgents[0], plan.Tasks[0].Task.Id, true), TaskState.Completed, HandoffQuality.Valid);
        Assert.False(reusable.IsActive);
        Assert.Null(reusable.CurrentTaskId);
        Assert.Equal(workerAgents[0], reusable.LogicalAgentId);
        Mark(28);

        var pausePort = new TestPausePort();
        var startup = new DurableStartupRecoveryService(store, orchestrationStore);
        await new SafeShutdownCoordinator(pausePort, new RecoveryCheckpointService(store), startup, orchestrationStore, store).ShutdownAsync(snapshot, "0.1.0");
        Assert.True(pausePort.Paused);
        Assert.Equal(RecoveryStartupKind.CLEAN_SHUTDOWN, await startup.BeginStartupAsync(snapshot.ProjectRun.Id));
        var reconstructed = await startup.ReconstructAsync(snapshot.ProjectRun.Id);
        Assert.NotNull(reconstructed);
        await pausePort.ResumeNewSendsAsync("reconstructed");
        Assert.False(pausePort.Paused);
        Mark(29);

        var uncertain = new PCCExecutive.Domain.Dispatch(DispatchId.New(), snapshot.ProjectRun.Id, snapshot.CurrentWave.Id, secondSpec.Id, workerAgents[0], workerConversations[0], "uncertain-hash", Now, PCCExecutive.Domain.DispatchState.PREPARED, null, null, null, null, null);
        var uncertainSnapshot = reconstructed! with { Dispatches = reconstructed.Dispatches.Concat([uncertain]).ToArray(), Phase = OrchestrationPhase.Dispatching, SavedAt = Now };
        await orchestrationStore.SaveAsync(uncertainSnapshot);
        await store.ReserveAsync(uncertain.Id.ToString(), uncertain.ContentHash);
        await store.UpdateAsync(uncertain.Id.ToString(), PCCExecutive.Browser.DispatchState.Submitting, "enter-before-crash");
        var recoveredUnknown = await startup.ReconstructAsync(snapshot.ProjectRun.Id);
        Assert.Equal(PCCExecutive.Domain.DispatchState.SUBMITTED_UNKNOWN, recoveredUnknown!.Dispatches.Single(x => x.Id == uncertain.Id).State);
        var rate = new ChatGptResilienceClassifier().Classify(Semantic(ConversationMatch.Match, PageHealth.RateLimited), TimeSpan.Zero);
        Assert.Equal(ChatGptResilienceState.RateLimited, rate.State);
        Assert.True(rate.PauseUnsafeNewSends);
        Assert.Equal(FaultScope.Global, rate.Scope);
        Mark(30);

        var rolloverAgent = LogicalAgentId.New();
        await CreateAndCommitRolloverAsync(store, orchestrationStore, snapshot.ProjectRun.Id, rolloverAgent, AgentRole.Worker, new WorkerSlotId(1));
        Assert.True(await new ConversationInvariantService(new FullDurabilityRecoveryService(store, orchestrationStore)).ExactlyOneActiveAsync(snapshot.ProjectRun.Id, rolloverAgent));
        Mark(31);

        var closure = new CompletionGateController().Evaluate(
            new ManagerEstimate(100),
            [Gate(CompletionGateFamily.IMPLEMENTATION, GateState.Pass, 99), Gate(CompletionGateFamily.UI, GateState.Pending, 1)],
            []);
        Assert.Equal(99m, closure.VerifiedCompletion.Percent);
        Assert.Equal(ProjectCompletionMode.ClosureMode, closure.Mode);
        var verified = new CompletionGateController().Evaluate(
            new ManagerEstimate(100),
            [Gate(CompletionGateFamily.IMPLEMENTATION, GateState.Pass, 99), Gate(CompletionGateFamily.UI, GateState.Pass, 1)],
            []);
        Assert.Equal(100m, verified.VerifiedCompletion.Percent);
        Assert.Equal(ProjectCompletionMode.VerifiedComplete, verified.Mode);
        Mark(32);

        Assert.Equal(Enumerable.Range(1, 32), proven);
        void Mark(int stage) => Assert.True(proven.Add(stage), $"Stage {stage} was recorded more than once.");
    }

    [Fact]
    public async Task Mandatory_security_and_recovery_negative_cases_are_fail_safe()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorkerSlotId(6));

        var route = Route();
        var baseline = Baseline(route);
        var duplicateId = TaskId.New();
        var duplicate = new StructuredManagerPlanParser().Parse(PlanJson(route, 10, "CONTINUE", Spec(duplicateId, "one", "tests/one", 1), Spec(duplicateId, "two", "tests/two", 2)));
        Assert.False(duplicate.IsValid);
        Assert.Contains(duplicate.Findings, x => x.Code == "DUPLICATE_TASK_ID");

        var missingDep = MakeTask(TaskId.New(), "missing dependency", "tests/dependency", new HashSet<TaskId> { TaskId.New() });
        var dependencyValidation = new WaveValidator().Validate(new WavePlan(WaveId.New(), new ManagerEstimate(1), [missingDep], []), EmptyCompletedTaskIndex.Instance);
        Assert.False(dependencyValidation.IsValid);
        Assert.Contains(dependencyValidation.Issues, x => x.Code == "MISSING_DEPENDENCY");

        var overlapA = MakeTask(TaskId.New(), "overlap a", "tests/shared", new HashSet<TaskId>());
        var overlapB = MakeTask(TaskId.New(), "overlap b", "tests/shared/child", new HashSet<TaskId>());
        var overlapValidation = new WaveValidator().Validate(new WavePlan(WaveId.New(), new ManagerEstimate(1), [overlapA, overlapB], []), EmptyCompletedTaskIndex.Instance);
        Assert.False(overlapValidation.IsValid);
        Assert.Contains(overlapValidation.Issues, x => x.Code == "OVERLAPPING_SCOPE");

        var malformedManager = new StructuredManagerPlanParser().Parse("not-json");
        Assert.False(malformedManager.IsValid);
        Assert.Contains(malformedManager.Findings, x => x.Code == "MANAGER_PLAN_NOT_STRUCTURED");

        var malformedWorker = new WorkerHandoffParser().Parse("TASK: not-a-guid\nWORKER_SLOT: Worker 1");
        Assert.NotEqual(HandoffQuality.Valid, malformedWorker.Quality);

        var staleCompletion = new CompletionGateController().Evaluate(
            new ManagerEstimate(100),
            [new PolicyCompletionGate(CompletionGateFamily.IMPLEMENTATION, new CompletionGate("implementation", true, 100, GateState.Pass, "old"), new EvidenceQualityAssessment(EvidenceQuality.STALE, ["expired"]), ClosurePriority.P0_VERIFICATION_BLOCKER)],
            []);
        Assert.True(staleCompletion.VerifiedCompletion.Percent < 100m);
        Assert.NotEqual(ProjectCompletionMode.VerifiedComplete, staleCompletion.Mode);

        var classifier = new ChatGptResilienceClassifier();
        var login = classifier.Classify(Semantic(ConversationMatch.Match, PageHealth.Healthy, AuthState.LoginRequired), TimeSpan.Zero);
        var challenge = classifier.Classify(Semantic(ConversationMatch.Match, PageHealth.Healthy, AuthState.Challenge), TimeSpan.Zero);
        var offline = classifier.Classify(Semantic(ConversationMatch.Match, PageHealth.Offline), TimeSpan.Zero);
        var rate = classifier.Classify(Semantic(ConversationMatch.Match, PageHealth.RateLimited), TimeSpan.Zero);
        Assert.All(new[] { login, challenge, offline, rate }, x => { Assert.Equal(FaultScope.Global, x.Scope); Assert.True(x.PauseUnsafeNewSends); });
        Assert.True(login.RequiresHumanAction);
        Assert.True(challenge.RequiresHumanAction);
        Assert.False(offline.RequiresHumanAction);
        Assert.False(rate.RequiresHumanAction);

        var db = Path.Combine(_root, "durable-global-recovery.db");
        var runId = ProjectRunId.New();
        await using (var store = new SqliteStateStore(db))
        {
            await store.InitializeAsync();
            await store.SaveCheckpointAsync(new DurableCheckpoint($"runtime-health:{runId}", runId.ToString(), "runtime-health-v1", JsonSerializer.Serialize(new { active = true, state = "OFFLINE", reason = "NETWORK_OFFLINE" }), Now));
            await store.SaveCheckpointAsync(new DurableCheckpoint($"runtime-health-rate:{runId}", runId.ToString(), "runtime-health-v1", JsonSerializer.Serialize(new { active = true, state = "RATE_LIMITED", reason = "RATE_LIMITED" }), Now));
        }
        await using (var reopened = new SqliteStateStore(db))
        {
            await reopened.InitializeAsync();
            Assert.Contains("OFFLINE", (await reopened.LoadCheckpointAsync($"runtime-health:{runId}"))!.Payload, StringComparison.Ordinal);
            Assert.Contains("RATE_LIMITED", (await reopened.LoadCheckpointAsync($"runtime-health-rate:{runId}"))!.Payload, StringComparison.Ordinal);
        }

        var loopSnapshots = Enumerable.Range(0, 3).Select(_ => new LoopSnapshot(
            WaveId.New(),
            new HashSet<string> { "same-task" },
            new HashSet<string> { "same-error" },
            new HashSet<string> { "same-source" },
            new HashSet<string> { "same-error" },
            new HashSet<string> { "same-reassignment" },
            new VerifiedCompletion(50m))).ToArray();
        var loop = new LoopGuardService().Analyze(loopSnapshots);
        Assert.Equal(LoopGuardLevel.LoopDetected, loop.Level);
        Assert.Equal(OrchestrationDecision.StalledAutoStopped, new LoopDecisionEngine().Decide(loop, ProjectCompletionMode.Active, false));

        var profileRoot = Path.Combine(_root, "pcc-profiles");
        Directory.CreateDirectory(profileRoot);
        var personalRuntime = Runtime(ProjectRunId.New(), LogicalAgentId.New(), TaskId.New(), ConversationId.New(), "1") with
        {
            ProfilePath = Path.Combine(_root, "personal-chrome"),
            CreatedByPcc = false,
            AdoptedExplicitly = false
        };
        var personalProof = await new OwnershipProofService(profileRoot, new NullMarkerStore(), new DeadProcessInspector()).ProveAsync(personalRuntime);
        Assert.False(personalProof.IsProven);
        Assert.Equal("NO_PCC_OWNERSHIP_FLAG", personalProof.Reason);

        await AssertRolloverCrashKeepsOneActiveAsync(AgentRole.Manager, null);
        await AssertRolloverCrashKeepsOneActiveAsync(AgentRole.Worker, new WorkerSlotId(1));
    }

    private async Task AssertRolloverCrashKeepsOneActiveAsync(AgentRole role, WorkerSlotId? slot)
    {
        var path = Path.Combine(_root, $"rollover-crash-{role}-{Guid.NewGuid():N}.db");
        await using var store = new SqliteStateStore(path);
        await store.InitializeAsync();
        var runId = ProjectRunId.New();
        var agentId = LogicalAgentId.New();
        var oldId = ConversationId.New();
        var newId = ConversationId.New();
        var orchestration = new SqliteOrchestrationStateStore(store);
        var run = new ProjectRun(runId, ProjectId.New(), ProjectRunState.WaveRunning, Now, new ManagerEstimate(50), new VerifiedCompletion(50), ProjectCompletionMode.Active);
        await orchestration.SaveAsync(new OrchestrationRecoverySnapshot(run, null, [], new Dictionary<TaskId, WorkerSlotId>(), [], null, OrchestrationPhase.WaveRunning, Now));
        await store.SaveLogicalAgentAsync(new LogicalAgentSession(agentId, runId, role, slot, null, oldId, LogicalSessionState.Active));
        await store.SaveConversationAsync(new PCCExecutive.Domain.Conversation(oldId, agentId, 1, AgentProviderKind.BrowserChat, "old", "old", ConversationState.Active, Now, null, null, null, 1, 1, null, null), runId);
        var lifecycle = new DurableConversationLifecycleStore(store);
        var candidate = new ConversationRecord { ConversationId = newId.ToString(), LogicalAgentId = agentId.ToString(), ProjectRunId = runId.ToString(), Sequence = 2, UrlOrProviderIdentity = "new", CreatedAt = Now, PredecessorConversationId = oldId.ToString(), State = ConversationLifecycleState.Candidate };
        await lifecycle.SaveCandidateAsync(candidate, CheckpointId.New().ToString());
        Assert.True(await new ConversationInvariantService(new FullDurabilityRecoveryService(store, orchestration)).ExactlyOneActiveAsync(runId, agentId));
        Assert.Equal(oldId, (await store.LoadLogicalAgentAsync(agentId))!.CurrentConversationId);
    }

    private async Task CreateAndCommitRolloverAsync(SqliteStateStore store, IOrchestrationStateStore orchestration, ProjectRunId runId, LogicalAgentId agentId, AgentRole role, WorkerSlotId? slot)
    {
        var oldId = ConversationId.New();
        var newId = ConversationId.New();
        await store.SaveLogicalAgentAsync(new LogicalAgentSession(agentId, runId, role, slot, null, oldId, LogicalSessionState.Active));
        await store.SaveConversationAsync(new PCCExecutive.Domain.Conversation(oldId, agentId, 1, AgentProviderKind.BrowserChat, "old", "old", ConversationState.Active, Now, null, null, null, 1, 1, null, null), runId);
        var predecessor = new ConversationRecord { ConversationId = oldId.ToString(), LogicalAgentId = agentId.ToString(), ProjectRunId = runId.ToString(), Sequence = 1, UrlOrProviderIdentity = "old", CreatedAt = Now, State = ConversationLifecycleState.Active };
        var successor = new ConversationRecord { ConversationId = newId.ToString(), LogicalAgentId = agentId.ToString(), ProjectRunId = runId.ToString(), Sequence = 2, UrlOrProviderIdentity = "new", CreatedAt = Now, PredecessorConversationId = oldId.ToString(), State = ConversationLifecycleState.Candidate };
        var lifecycle = new DurableConversationLifecycleStore(store);
        var checkpoint = CheckpointId.New().ToString();
        await lifecycle.SaveCandidateAsync(successor, checkpoint);
        await lifecycle.CommitRolloverAsync(predecessor with { State = ConversationLifecycleState.Archived, SuccessorConversationId = newId.ToString(), RetiredAt = Now }, successor with { State = ConversationLifecycleState.Active }, checkpoint);
        var recovered = await new FullDurabilityRecoveryService(store, orchestration).ReconstructAsync(runId);
        Assert.Contains(recovered!.Conversations, x => x.Id == newId && x.State == ConversationState.Active);
    }

    private static RuntimeHealthSnapshot HealthyRuntime() => new(false, true, TimeSpan.FromSeconds(10), null);
    private static LoopAssessment NormalLoop() => new(LoopGuardLevel.Normal, []);
    private static EvidenceQualityAssessment AcceptableEvidence() => new(EvidenceQuality.ACCEPTABLE, ["fresh deterministic exact-head evidence"]);
    private static PolicyCompletionGate Gate(CompletionGateFamily family, GateState state, decimal weight) =>
        new(family, new CompletionGate(family.ToString(), true, weight, state, "fresh-e2e"), AcceptableEvidence(), ClosurePriority.P0_VERIFICATION_BLOCKER);

    private static WorkerTask MakeTask(TaskId id, string objective, string path, IReadOnlySet<TaskId> dependencies)
    {
        var scope = TaskScope.Create(Repository, [path]);
        return new WorkerTask(id, objective, scope, dependencies, ["deterministic acceptance"], TaskState.Proposed, TaskFingerprint.Create(objective, scope, dependencies));
    }

    private static TaskSpec Spec(TaskId id, string objective, string path, int priority, IReadOnlyList<TaskId>? dependencies = null) =>
        new(id, objective, path, priority, dependencies ?? []);

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
                reason = "final 32-stage acceptance",
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

    private static string Handoff(TaskId taskId, WorkerSlotId slot, string head) =>
        string.Join('\n',
            $"TASK: {taskId.Value}",
            $"WORKER_SLOT: Worker {slot.Value}",
            $"PROJECT: {Project}",
            $"REPOSITORY: {Repository}",
            "STATUS: DONE",
            $"HEAD: {head}",
            "BRANCH: worker/pcc-final-e2e-release-acceptance",
            "PR: N/A",
            $"CHANGED: tests/final32/work-{slot.Value}",
            "TESTS: PASS",
            "BUILD: PASS",
            "BLOCKER: N/A",
            "NEXT_ACTION: manager-review");

    private static ProjectRoutingSnapshot Route()
    {
        var provenance = new ProjectControlProvenance("walidatiyaai2025-gif/project-control-center", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "1.6.0", "v1", Now, EvidenceFreshness.Current);
        return new ProjectRoutingSnapshot(Project, "PCC Executive", Repository, ProjectModel.Standalone, ProjectScopeKind.Project, null, null, null, "READY", "READY", ["PCC Executive"], [], null, provenance);
    }

    private static ProjectBaselineSnapshot Baseline(ProjectRoutingSnapshot route) =>
        new(route.ProjectControlId, route.DisplayName, route.Repository, route.ProjectModel, route.Scope, route.VariantId, route.ImplementationLocation, route.Provenance.SourceSha, route.RoutingIdentity, "main", Head, route.CanonicalTasks, [], new GitHubCheckSummary(Repository, Head, "success", [new GitHubCheckSnapshot("e2e", "completed", "success", null)]), route.DesiredState, null, [], Now, EvidenceFreshness.Current);

    private BrowserRuntimeRecord Runtime(ProjectRunId run, LogicalAgentId agent, TaskId task, ConversationId conversation, string slot) => new()
    {
        RuntimeId = "runtime-" + Guid.NewGuid().ToString("N"), ProjectRunId = run.ToString(), LogicalAgentId = agent.ToString(), WorkerSlotId = slot, TaskId = task.ToString(),
        ProcessId = 1001, ProcessStartIdentity = "pid:1001:start:1", ContextIdentity = "ctx", ProfilePath = Path.Combine(_root, "profile"), CreatedByPcc = true,
        ConversationIdentity = conversation.ToString(), ProviderConversationIdentity = "provider", Visibility = BrowserVisibility.Hidden, State = BrowserSessionState.Hidden,
        LastHeartbeatAt = Now, LastActivityAt = Now, OwnershipNonce = "nonce"
    };

    private static ChatGptSemanticSnapshot Semantic(ConversationMatch conversation, PageHealth health = PageHealth.Healthy, AuthState auth = AuthState.Authenticated) =>
        new(
            SemanticDetection<InputState>.Create(InputState.Ready, .99, "e2e", "ready"),
            SemanticDetection<GenerationState>.Create(GenerationState.Idle, .99, "e2e", "idle"),
            SemanticDetection<AuthState>.Create(auth, .99, "e2e", auth.ToString()),
            SemanticDetection<ConversationMatch>.Create(conversation, .99, "e2e", conversation.ToString()),
            SemanticDetection<PageHealth>.Create(health, .99, "e2e", health.ToString()),
            ResponseCompleteness.None, 0, null, Now, "e2e");

    private sealed record TaskSpec(TaskId Id, string Objective, string Path, int Priority, IReadOnlyList<TaskId> Dependencies);

    private sealed class EmptyCompletedTaskIndex : ICompletedTaskIndex
    {
        public static EmptyCompletedTaskIndex Instance { get; } = new();
        public bool IsCompleted(TaskId taskId) => false;
        public bool ContainsFingerprint(string fingerprint) => false;
    }

    private sealed class ControlledProjectControl(ProjectRoutingSnapshot route) : IProjectControlResolver
    {
        public Task<ProjectResolution> ResolveProjectAsync(string nameOrAlias, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Equals(nameOrAlias, route.ProjectControlId, StringComparison.OrdinalIgnoreCase) ? new ProjectResolution(ProjectResolutionStatus.Success, route, null) : new ProjectResolution(ProjectResolutionStatus.ProjectNotFound, null, "PROJECT_NOT_FOUND"));
        public Task<ProjectResolution> GetProjectAsync(string projectControlId, string? variantId = null, ProjectScopeKind? scope = null, CancellationToken cancellationToken = default) => ResolveProjectAsync(projectControlId, cancellationToken);
        public Task<ExternalResult<ProjectRoutingSnapshot>> GetRoutingSnapshotAsync(string projectControlId, string? variantId = null, ProjectScopeKind? scope = null, CancellationToken cancellationToken = default) => Task.FromResult(new ExternalResult<ProjectRoutingSnapshot>(ExternalReadStatus.Success, route, Now));
        public Task<ExternalResult<IReadOnlyList<CanonicalTaskSnapshot>>> GetCanonicalTasksAsync(string projectControlId, CancellationToken cancellationToken = default) => Task.FromResult(new ExternalResult<IReadOnlyList<CanonicalTaskSnapshot>>(ExternalReadStatus.Success, route.CanonicalTasks, Now));
        public Task<ExternalResult<DesiredStateSnapshot>> GetDesiredStateAsync(string projectControlId, CancellationToken cancellationToken = default) => Task.FromResult(new ExternalResult<DesiredStateSnapshot>(ExternalReadStatus.NotFound, null, Now));
    }

    private sealed class ControlledBaselineBuilder(ProjectBaselineSnapshot baseline) : IProjectBaselineBuilder
    {
        public Task<ExternalResult<ProjectBaselineSnapshot>> BuildAsync(string nameOrAlias, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExternalResult<ProjectBaselineSnapshot>(ExternalReadStatus.Success, baseline, baseline.CapturedAt));
    }

    private sealed class ControlledAgentProvider(Func<AgentRequest, AgentResult> resultFactory) : IAgentProvider
    {
        public AgentProviderKind Kind => AgentProviderKind.BrowserChat;
        public int SendCount { get; private set; }
        public List<AgentRequest> Requests { get; } = [];
        public static ControlledAgentProvider Completed() => new(request => new AgentResult(request.DispatchId, true, false, true, false, "complete", "RESPONSE_COMPLETE", null));
        public Task<ProviderHealth> ProbeAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ProviderHealth(true, true, false, "READY", "controlled"));
        public Task<AgentResult> SendAsync(AgentRequest request, CancellationToken cancellationToken = default)
        {
            SendCount++;
            Requests.Add(request);
            return Task.FromResult(resultFactory(request));
        }
    }

    private sealed class MemoryDispatchStore : IDispatchIdempotencyStore
    {
        private readonly List<PCCExecutive.Domain.Dispatch> _items = [];
        public Task<PCCExecutive.Domain.Dispatch?> FindEquivalentAsync(ProjectRunId projectRunId, LogicalAgentId logicalAgentId, string contentHash, CancellationToken cancellationToken = default) =>
            Task.FromResult(_items.LastOrDefault(x => x.ProjectRunId == projectRunId && x.LogicalAgentId == logicalAgentId && string.Equals(x.ContentHash, contentHash, StringComparison.OrdinalIgnoreCase)));
        public Task ReserveAsync(PCCExecutive.Domain.Dispatch dispatch, CancellationToken cancellationToken = default) { _items.Add(dispatch); return Task.CompletedTask; }
    }

    private sealed class ValidSessionGuard : IAgentSessionGuard
    {
        public Task<SessionValidationResult> ValidateAsync(ProjectRunId runId, LogicalAgentId agentId, ConversationId conversationId, TaskId taskId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SessionValidationResult(true, "MATCH", null));
    }

    private sealed class NeverRetryReconciliation : IDispatchReconciliationService
    {
        public Task<DispatchReconciliationResult> ReconcileAsync(PCCExecutive.Domain.Dispatch dispatch, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DispatchReconciliationResult(PCCExecutive.Domain.DispatchState.SUBMITTED_UNKNOWN, "RECONCILIATION_REQUIRED", false));
    }

    private sealed class TestPausePort : INewSendPausePort
    {
        public bool Paused { get; private set; }
        public Task PauseNewSendsAsync(string reason, CancellationToken cancellationToken = default) { Paused = true; return Task.CompletedTask; }
        public Task ResumeNewSendsAsync(string reason, CancellationToken cancellationToken = default) { Paused = false; return Task.CompletedTask; }
    }

    private sealed class NullMarkerStore : IOwnershipMarkerStore
    {
        public Task WriteAsync(OwnershipMarker marker, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<OwnershipMarker?> ReadAsync(string profilePath, CancellationToken cancellationToken = default) => Task.FromResult<OwnershipMarker?>(null);
    }

    private sealed class DeadProcessInspector : IProcessInspector
    {
        public bool IsAlive(int processId) => false;
        public string? GetStartIdentity(int processId) => null;
    }
}
