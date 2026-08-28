using System.Text.Json;
using PCCExecutive.Application;
using PCCExecutive.Domain;
using Xunit;

namespace PCCExecutive.Application.Tests;

public sealed class ManagerOrchestrationTests
{
    [Fact]
    public void Five_independent_tasks_use_five_parallel_slots()
    {
        var plan = Plan(Enumerable.Range(1, 5).Select(i => Proposal(path: $"src/p{i}", priority: i)).ToArray());
        var batch = new DependencyAwareWaveScheduler().Schedule(plan, new Dictionary<TaskId, TaskState>(), new HashSet<WorkerSlotId>(), Health());

        Assert.Equal(5, batch.Assignments.Count);
        Assert.Equal(5, batch.Assignments.Select(x => x.SlotId).Distinct().Count());
        Assert.Empty(batch.Deferred);
    }

    [Fact]
    public void Manager_proposing_six_tasks_is_rejected()
    {
        var tasks = Enumerable.Range(1, 6).Select(_ => WireTask()).ToArray();
        var json = JsonSerializer.Serialize(new { ManagerEstimate = 50m, Tasks = tasks });

        var parsed = new StructuredManagerPlanParser().Parse(json);

        Assert.False(parsed.IsValid);
        Assert.Contains(parsed.Findings, x => x.Code == "WORKER_LIMIT");
    }

    [Fact]
    public void Overlap_is_marked_for_sequentialization_and_not_parallel_dispatch()
    {
        var first = Proposal(path: "src/core");
        var second = Proposal(path: "src/core/sub");
        var plan = Plan(first, second);
        var validation = Validate(plan);
        var batch = new DependencyAwareWaveScheduler().Schedule(plan, new Dictionary<TaskId, TaskState>(), new HashSet<WorkerSlotId>(), Health());

        Assert.True(validation.IsValid);
        Assert.True(validation.RequiresSequentialization);
        Assert.Single(batch.Assignments);
        Assert.Contains(batch.Deferred, x => x.Reason == "SEQUENTIALIZED_SCOPE_COLLISION");
    }

    [Fact]
    public void Dependencies_gate_later_tasks_until_prerequisite_completes()
    {
        var first = Proposal(path: "src/a");
        var second = Proposal(path: "src/b", dependencies: new HashSet<TaskId> { first.Task.Id });
        var plan = Plan(first, second);

        var firstBatch = new DependencyAwareWaveScheduler().Schedule(plan, new Dictionary<TaskId, TaskState>(), new HashSet<WorkerSlotId>(), Health());
        Assert.Contains(firstBatch.Assignments, x => x.TaskId == first.Task.Id);
        Assert.Contains(firstBatch.Deferred, x => x.TaskId == second.Task.Id && x.Reason.StartsWith("WAITING_DEPENDENCY", StringComparison.Ordinal));

        var states = new Dictionary<TaskId, TaskState> { [first.Task.Id] = TaskState.Completed };
        var secondBatch = new DependencyAwareWaveScheduler().Schedule(plan, states, new HashSet<WorkerSlotId>(), Health());
        Assert.Contains(secondBatch.Assignments, x => x.TaskId == second.Task.Id);
    }

    [Fact]
    public void Duplicate_task_fingerprint_is_blocked()
    {
        var left = Proposal(objective: "same", path: "src/x");
        var right = Proposal(objective: "same", path: "src/x");
        var plan = Plan(left, right);

        var validation = Validate(plan);

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Findings, x => x.Code == "DUPLICATE_TASK_FINGERPRINT");
    }

    [Fact]
    public void Already_completed_work_is_blocked()
    {
        var task = Proposal();
        var completed = new CompletedIndex(fingerprints: new[] { task.Task.Fingerprint });

        var validation = Validate(Plan(task), completed: completed);

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Findings, x => x.Code == "ALREADY_COMPLETED");
    }

    [Fact]
    public void Wrong_project_or_variant_is_blocked()
    {
        var routing = Routing(ProjectScopeKind.Variant, ProjectModel.ProductFamily, "LARAVEL_AIWMWEB");
        var baseline = Baseline(routing);
        var proposal = Proposal(repository: "owner/wrong", targetScope: ProjectScopeKind.Variant, targetVariant: "OTHER");

        var validation = new ManagerWaveValidator().Validate(Plan(proposal), routing, baseline, new CompletedIndex(), ProjectCompletionMode.Active);

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Findings, x => x.Code == "WRONG_REPOSITORY");
        Assert.Contains(validation.Findings, x => x.Code == "WRONG_PCC_VARIANT");
    }

    [Fact]
    public void Stale_manager_head_is_blocked()
    {
        var routing = Routing();
        var baseline = Baseline(routing, mainHead: "live-head");
        var plan = Plan(new[] { Proposal() }, expectedHead: "old-head");

        var validation = new ManagerWaveValidator().Validate(plan, routing, baseline, new CompletedIndex(), ProjectCompletionMode.Active);

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Findings, x => x.Code == "STALE_HEAD");
    }

    [Fact]
    public void Valid_worker_handoff_passes_quality_gate()
    {
        var proposal = Proposal();
        var routing = Routing();
        var baseline = Baseline(routing, checks: GreenChecks());
        var parsed = new WorkerHandoffParser().Parse(HandoffText(proposal.Task.Id, "pr-head", "PR OPEN", "PASS"));

        var result = new WorkerHandoffQualityGate().Validate(parsed, proposal, new WorkerSlotId(1), routing, baseline);

        Assert.Equal(HandoffQuality.Valid, result.Quality);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void Truncated_worker_handoff_is_partial()
    {
        var task = TaskId.New();
        var text = HandoffText(task, "pr-head", "PR OPEN", "PASS").Replace("NEXT_ACTION: integrate", string.Empty);

        var parsed = new WorkerHandoffParser().Parse(text);

        Assert.Equal(HandoffQuality.Partial, parsed.Quality);
        Assert.Contains(parsed.Findings, x => x.Code == "FIELD_MISSING");
    }

    [Fact]
    public void Worker_handoff_contradicted_by_live_pr_head_is_rejected_as_contradicted()
    {
        var proposal = Proposal();
        var routing = Routing();
        var baseline = Baseline(routing, checks: GreenChecks());
        var parsed = new WorkerHandoffParser().Parse(HandoffText(proposal.Task.Id, "wrong-head", "PR OPEN", "PASS"));

        var result = new WorkerHandoffQualityGate().Validate(parsed, proposal, new WorkerSlotId(1), routing, baseline);

        Assert.Equal(HandoffQuality.ContradictedByLiveEvidence, result.Quality);
        Assert.Contains(result.Findings, x => x.Code == "PR_HEAD_CONTRADICTION");
    }

    [Fact]
    public void Manager_review_packet_is_consolidated_structured_state()
    {
        var first = Proposal();
        var second = Proposal(path: "src/two");
        var routing = Routing();
        var baseline = Baseline(routing, checks: GreenChecks());
        var parser = new WorkerHandoffParser();
        var gate = new WorkerHandoffQualityGate();
        var a1 = gate.Validate(parser.Parse(HandoffText(first.Task.Id, "pr-head", "PR OPEN", "PASS")), first, new WorkerSlotId(1), routing, baseline);
        var a2 = gate.Validate(parser.Parse(HandoffText(second.Task.Id, "pr-head", "PR OPEN", "PASS")), second, new WorkerSlotId(2), routing, baseline);
        var handoffs = new List<(ManagerTaskProposal Expected, WorkerSlotId Slot, HandoffAssessment Assessment)>
        {
            (first, new WorkerSlotId(1), a1),
            (second, new WorkerSlotId(2), a2)
        };

        var packet = new ManagerReviewPacketBuilder().Build(
            "PCCEXECUTIVE",
            WaveId.New(),
            handoffs,
            baseline,
            [],
            [],
            new LoopAssessment(LoopGuardLevel.Normal, []),
            [],
            OrchestrationDecision.Continue);

        Assert.Equal(2, packet.TaskResults.Count);
        Assert.Equal("live-head", packet.LiveHead);
        Assert.Equal("success", packet.CiState);
    }

    [Fact]
    public async Task Submitted_unknown_dispatch_is_reconciled_before_any_retry()
    {
        var existing = DispatchRecord(DispatchState.SUBMITTED_UNKNOWN);
        var provider = new FakeProvider();
        var idempotency = new FakeIdempotency(existing);
        var reconciliation = new FakeReconciliation(new DispatchReconciliationResult(DispatchState.SUBMITTED_UNKNOWN, "cannot determine", false));
        var coordinator = new DispatchCoordinator(provider, new FakeSessionGuard(), idempotency, reconciliation);

        var prepared = await coordinator.PrepareDispatchAsync(existing.ProjectRunId, existing.WaveId, existing.TaskId, existing.LogicalAgentId, existing.ConversationId, "content");
        var submitted = await coordinator.SubmitDispatchAsync(prepared.Dispatch, "content");

        Assert.True(prepared.ExistingReservation);
        Assert.True(prepared.RequiresReconciliation);
        Assert.Equal(existing.Id, submitted.Id);
        Assert.Equal(0, provider.SendCount);
        Assert.Equal(1, reconciliation.CallCount);
    }

    [Fact]
    public void One_slow_worker_does_not_consume_other_available_slots()
    {
        var remaining = Plan(
            Proposal(path: "src/2"),
            Proposal(path: "src/3"),
            Proposal(path: "src/4"),
            Proposal(path: "src/5"));
        var occupied = new HashSet<WorkerSlotId> { new(1) };

        var batch = new DependencyAwareWaveScheduler().Schedule(remaining, new Dictionary<TaskId, TaskState>(), occupied, Health());

        Assert.Equal(4, batch.Assignments.Count);
        Assert.DoesNotContain(batch.Assignments, x => x.SlotId.Value == 1);
    }

    [Fact]
    public void Global_runtime_pause_blocks_new_dispatch()
    {
        var plan = Plan(Proposal(), Proposal(path: "src/b"));

        var batch = new DependencyAwareWaveScheduler().Schedule(
            plan,
            new Dictionary<TaskId, TaskState>(),
            new HashSet<WorkerSlotId>(),
            new RuntimeHealthSnapshot(true, true, TimeSpan.FromSeconds(30), "rate-limit"));

        Assert.Empty(batch.Assignments);
        Assert.Equal(2, batch.Deferred.Count);
        Assert.All(batch.Deferred, x => Assert.StartsWith("GLOBAL_RUNTIME_PAUSE", x.Reason));
    }

    [Fact]
    public void Loop_guard_stops_repeated_no_progress_work()
    {
        var snapshots = Enumerable.Range(0, 3)
            .Select(_ => new LoopSnapshot(
                WaveId.New(),
                new HashSet<string> { "task-fingerprint" },
                new HashSet<string> { "blocker" },
                new HashSet<string> { "same-head" },
                new HashSet<string> { "same-test" },
                new HashSet<string> { "same-assignment" },
                new VerifiedCompletion(50)))
            .ToArray();

        var assessment = new LoopGuardService().Analyze(snapshots);
        var decision = new LoopDecisionEngine().Decide(assessment, ProjectCompletionMode.Active, false);

        Assert.Equal(LoopGuardLevel.LoopDetected, assessment.Level);
        Assert.Equal(OrchestrationDecision.StalledAutoStopped, decision);
    }

    [Fact]
    public void Ninety_nine_percent_enters_closure_mode_not_done()
    {
        var result = new CompletionEngine().Evaluate(
        [
            new CompletionGate("implementation", true, 99m, GateState.Pass, "head"),
            new CompletionGate("release", false, 1m, GateState.Unknown, null)
        ], []);

        Assert.Equal(99m, result.Verified.Percent);
        Assert.Equal(ProjectCompletionMode.ClosureMode, result.Mode);
    }

    [Fact]
    public void Closure_mode_rejects_unrelated_feature_expansion()
    {
        var expanded = Proposal(featureExpansion: true);
        var validation = Validate(Plan(expanded), completionMode: ProjectCompletionMode.ClosureMode);

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Findings, x => x.Code == "CLOSURE_FEATURE_EXPANSION");
    }

    [Fact]
    public async Task Restart_contract_restores_run_wave_tasks_assignments_dispatch_and_review_state()
    {
        var store = new FakeStateStore();
        var coordinator = new ProjectRunCoordinator(store);
        var run = new ProjectRun(ProjectRunId.New(), ProjectId.New(), ProjectRunState.ManagerReview, DateTimeOffset.UtcNow, new ManagerEstimate(60), new VerifiedCompletion(55), ProjectCompletionMode.Active);
        var proposal = Proposal();
        var wave = new Wave(WaveId.New(), run.Id, 2, WaveState.Reconciling, [proposal.Task.Id], DateTimeOffset.UtcNow);
        var dispatch = DispatchRecord(DispatchState.COMPLETED, run.Id, wave.Id, proposal.Task.Id);
        var snapshot = new OrchestrationRecoverySnapshot(
            run,
            wave,
            [proposal.Task],
            new Dictionary<TaskId, WorkerSlotId> { [proposal.Task.Id] = new(3) },
            [dispatch],
            null,
            OrchestrationPhase.ManagerReview,
            DateTimeOffset.UtcNow);
        await store.SaveAsync(snapshot);

        var restored = await coordinator.RestoreAsync(run.Id);

        Assert.NotNull(restored);
        Assert.Equal(wave.Id, restored!.CurrentWave!.Id);
        Assert.Equal(new WorkerSlotId(3), restored.Assignments[proposal.Task.Id]);
        Assert.Equal(dispatch.Id, restored.Dispatches.Single().Id);
        Assert.Equal(OrchestrationPhase.ManagerReview, restored.Phase);
    }

    [Fact]
    public void Zero_task_manager_wave_is_legal()
    {
        var parsed = new StructuredManagerPlanParser().Parse("""{"ManagerEstimate":25,"Tasks":[]}""");
        Assert.True(parsed.IsValid);

        var validation = Validate(parsed.Plan!);
        Assert.True(validation.IsValid);
    }

    [Fact]
    public void Free_form_manager_prose_is_not_dispatchable()
    {
        var parsed = new StructuredManagerPlanParser().Parse("Worker 1 should fix it and then we are done.");
        Assert.False(parsed.IsValid);
        Assert.Contains(parsed.Findings, x => x.Code == "MANAGER_PLAN_NOT_STRUCTURED");
    }

    [Fact]
    public void Manager_done_claim_without_verified_completion_is_blocked()
    {
        var plan = Plan(new[] { Proposal() }, projectDecision: "DONE");
        var validation = Validate(plan);
        var findings = new ManagerSanityChecker().Check(
            plan,
            validation,
            Baseline(Routing()),
            ProjectCompletionMode.Active,
            new VerifiedCompletion(90),
            new LoopAssessment(LoopGuardLevel.Normal, []));

        Assert.Contains(findings, x => x.Code == "UNSUPPORTED_COMPLETION");
    }

    [Fact]
    public void Human_attention_policy_excludes_routine_runtime_recovery()
    {
        Assert.True(AttentionPolicy.RequiresHumanAttention("LOGIN"));
        Assert.True(AttentionPolicy.RequiresHumanAttention("DESTRUCTIVE_APPROVAL"));
        Assert.False(AttentionPolicy.RequiresHumanAttention("TEMP_ERROR"));
        Assert.False(AttentionPolicy.RequiresHumanAttention("SLOW_RESPONSE"));
    }

    private static OrchestrationWaveValidation Validate(
        StructuredManagerPlan plan,
        ICompletedTaskIndex? completed = null,
        ProjectCompletionMode completionMode = ProjectCompletionMode.Active)
    {
        var routing = Routing();
        return new ManagerWaveValidator().Validate(plan, routing, Baseline(routing), completed ?? new CompletedIndex(), completionMode);
    }

    private static StructuredManagerPlan Plan(params ManagerTaskProposal[] tasks) =>
        Plan(tasks, null, null);

    private static StructuredManagerPlan Plan(
        IReadOnlyList<ManagerTaskProposal> tasks,
        string? expectedHead = null,
        string? projectDecision = null) =>
        new(new ManagerEstimate(50), tasks, expectedHead, null, projectDecision, []);

    private static ManagerTaskProposal Proposal(
        string objective = "work",
        string repository = "owner/repo",
        string path = "src/a",
        int priority = 1,
        IReadOnlySet<TaskId>? dependencies = null,
        ProjectScopeKind targetScope = ProjectScopeKind.Project,
        string? targetVariant = null,
        bool featureExpansion = false)
    {
        var scope = TaskScope.Create(repository, [path]);
        var deps = dependencies ?? new HashSet<TaskId>();
        var task = new WorkerTask(
            TaskId.New(),
            objective,
            scope,
            deps,
            ["accepted"],
            TaskState.Proposed,
            TaskFingerprint.Create(objective, scope, deps));
        return new(
            task,
            ["exact-head", "tests"],
            priority,
            null,
            "needed",
            [],
            new HashSet<TaskId>(),
            ManagerExecutionMode.AutomaticStaged,
            targetScope,
            targetVariant,
            null,
            null,
            null,
            null,
            featureExpansion);
    }

    private static ProjectRoutingSnapshot Routing(
        ProjectScopeKind scope = ProjectScopeKind.Project,
        ProjectModel model = ProjectModel.Standalone,
        string? variant = null)
    {
        var provenance = new ProjectControlProvenance("owner/pcc", "pcc-sha", "v1.6.0", "1.2.0", Now, EvidenceFreshness.Current);
        var route = new ProjectRoutingSnapshot(
            "PCCEXECUTIVE",
            "PCC Executive",
            "owner/repo",
            model,
            scope,
            variant,
            variant,
            variant is null ? "." : $"variants/{variant.ToLowerInvariant()}",
            "READY",
            "READY",
            ["pcc executive"],
            [CanonicalTask()],
            null,
            provenance);
        return route;
    }

    private static ProjectBaselineSnapshot Baseline(
        ProjectRoutingSnapshot routing,
        string mainHead = "live-head",
        GitHubCheckSummary? checks = null)
    {
        var pr = new GitHubPullRequestSnapshot(
            routing.Repository,
            4,
            "PCCEXECUTIVE-T0001 worker",
            "open",
            false,
            "worker/test",
            "pr-head",
            "task/pcc-executive-t0001-v1",
            "base-head",
            ["src/a.cs"],
            Now,
            null);
        return new(
            routing.ProjectControlId,
            routing.DisplayName,
            routing.Repository,
            routing.ProjectModel,
            routing.Scope,
            routing.VariantId,
            routing.ImplementationLocation,
            routing.Provenance.SourceSha,
            routing.RoutingIdentity,
            "main",
            mainHead,
            routing.CanonicalTasks,
            [pr],
            checks ?? GreenChecks(),
            routing.DesiredState,
            new GitHubReleaseSnapshot("0.1.0", "dev", false, false, Now, "main", null),
            [],
            Now,
            EvidenceFreshness.Current);
    }

    private static CanonicalTaskSnapshot CanonicalTask() =>
        new(
            "PCCEXECUTIVE-T0001",
            "PCCEXECUTIVE",
            "ISSUE-1",
            "v1",
            "IN_PROGRESS",
            "P0",
            "task/pcc-executive-t0001-v1",
            "main",
            "base-head",
            null,
            "0.1.0",
            ["project"],
            [],
            ["accepted"],
            [],
            []);

    private static GitHubCheckSummary GreenChecks() =>
        new("owner/repo", "live-head", "success", [new GitHubCheckSnapshot("tests", "completed", "success", null)]);

    private static RuntimeHealthSnapshot Health() =>
        new(false, true, TimeSpan.FromSeconds(10), null);

    private static object WireTask() => new
    {
        TaskId = TaskId.New().ToString(),
        Objective = "work",
        Repository = "owner/repo",
        Paths = new[] { "src/a" },
        Components = Array.Empty<string>(),
        Dependencies = Array.Empty<string>(),
        AcceptanceCriteria = new[] { "accepted" },
        EvidenceExpected = new[] { "head" },
        Priority = 1,
        Reason = "needed",
        TargetScope = "Project"
    };

    private static string HandoffText(TaskId taskId, string head, string status, string build) =>
        $"""
        TASK: {taskId}
        WORKER_SLOT: Worker 1
        PROJECT: PCCEXECUTIVE
        REPOSITORY: owner/repo
        STATUS: {status}
        HEAD: {head}
        BRANCH: worker/test
        PR: #4
        CHANGED: src/a.cs
        TESTS: WRITTEN
        BUILD: {build}
        BLOCKER: NONE
        NEXT_ACTION: integrate
        """;

    private static Dispatch DispatchRecord(
        DispatchState state,
        ProjectRunId? runId = null,
        WaveId? waveId = null,
        TaskId? taskId = null)
    {
        var run = runId ?? ProjectRunId.New();
        var wave = waveId ?? WaveId.New();
        var task = taskId ?? TaskId.New();
        return new(
            DispatchId.New(),
            run,
            wave,
            task,
            LogicalAgentId.New(),
            ConversationId.New(),
            "hash",
            Now,
            state,
            state == DispatchState.PREPARED ? null : Now,
            null,
            state == DispatchState.COMPLETED ? Now : null,
            null,
            null);
    }

    private sealed class CompletedIndex : ICompletedTaskIndex
    {
        private readonly HashSet<TaskId> _ids;
        private readonly HashSet<string> _fingerprints;

        public CompletedIndex(IEnumerable<TaskId>? ids = null, IEnumerable<string>? fingerprints = null)
        {
            _ids = new HashSet<TaskId>(ids ?? Array.Empty<TaskId>());
            _fingerprints = new HashSet<string>(fingerprints ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        }

        public bool IsCompleted(TaskId taskId) => _ids.Contains(taskId);
        public bool ContainsFingerprint(string fingerprint) => _fingerprints.Contains(fingerprint);
    }

    private sealed class FakeProvider : IAgentProvider
    {
        public AgentProviderKind Kind => AgentProviderKind.BrowserChat;
        public int SendCount { get; private set; }

        public Task<ProviderHealth> ProbeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderHealth(true, true, false, "READY", null));

        public Task<AgentResult> SendAsync(AgentRequest request, CancellationToken cancellationToken = default)
        {
            SendCount++;
            return Task.FromResult(new AgentResult(request.DispatchId, true, false, false, false, null, "sent", null));
        }
    }

    private sealed class FakeSessionGuard : IAgentSessionGuard
    {
        public Task<SessionValidationResult> ValidateAsync(ProjectRunId runId, LogicalAgentId agentId, ConversationId conversationId, TaskId taskId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SessionValidationResult(true, "match", null));
    }

    private sealed class FakeIdempotency(Dispatch existing) : IDispatchIdempotencyStore
    {
        public Task<Dispatch?> FindEquivalentAsync(ProjectRunId projectRunId, LogicalAgentId logicalAgentId, string contentHash, CancellationToken cancellationToken = default) =>
            Task.FromResult<Dispatch?>(existing);

        public Task ReserveAsync(Dispatch dispatch, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeReconciliation(DispatchReconciliationResult result) : IDispatchReconciliationService
    {
        public int CallCount { get; private set; }

        public Task<DispatchReconciliationResult> ReconcileAsync(Dispatch dispatch, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(result);
        }
    }

    private sealed class FakeStateStore : IOrchestrationStateStore
    {
        private readonly Dictionary<ProjectRunId, OrchestrationRecoverySnapshot> _snapshots = new();

        public Task SaveAsync(OrchestrationRecoverySnapshot snapshot, CancellationToken cancellationToken = default)
        {
            _snapshots[snapshot.ProjectRun.Id] = snapshot;
            return Task.CompletedTask;
        }

        public Task<OrchestrationRecoverySnapshot?> LoadAsync(ProjectRunId projectRunId, CancellationToken cancellationToken = default)
        {
            _snapshots.TryGetValue(projectRunId, out var snapshot);
            return Task.FromResult(snapshot);
        }
    }

    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-28T00:30:00Z");
}
