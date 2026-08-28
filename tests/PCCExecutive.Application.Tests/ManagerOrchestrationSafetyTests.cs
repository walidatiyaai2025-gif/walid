using PCCExecutive.Application;
using PCCExecutive.Domain;
using Xunit;

namespace PCCExecutive.Application.Tests;

public sealed class ManagerOrchestrationSafetyTests
{
    [Fact]
    public void Active_task_is_not_dispatched_twice_while_other_workers_continue()
    {
        var active = Proposal("src/active");
        var readyA = Proposal("src/a");
        var readyB = Proposal("src/b");
        var plan = new StructuredManagerPlan(new ManagerEstimate(40), [active, readyA, readyB], null, null, null, []);
        var states = new Dictionary<TaskId, TaskState> { [active.Task.Id] = TaskState.Running };

        var batch = new SafeDispatchPlanner().Schedule(
            plan,
            states,
            new HashSet<WorkerSlotId> { new(1) },
            new RuntimeHealthSnapshot(false, true, TimeSpan.FromSeconds(10), null));

        Assert.DoesNotContain(batch.Assignments, x => x.TaskId == active.Task.Id);
        Assert.Contains(batch.Deferred, x => x.TaskId == active.Task.Id && x.Reason == "TASK_ALREADY_ACTIVE:Running");
        Assert.Equal(2, batch.Assignments.Count);
    }

    [Fact]
    public async Task Live_reconciliation_detects_changed_head_and_worker_pr_head_contradiction()
    {
        var routing = Routing();
        var persisted = Baseline(routing, "old-main", "old-pr-head");
        var live = Baseline(routing, "new-main", "new-pr-head");
        var task = Proposal("src/a");
        var parsed = new HandoffAssessment(
            HandoffQuality.Valid,
            new StrictWorkerHandoff(
                task.Task.Id,
                new WorkerSlotId(1),
                routing.ProjectControlId,
                routing.Repository,
                "PR OPEN",
                "old-pr-head",
                "worker/test",
                4,
                ["src/a.cs"],
                "WRITTEN",
                "NOT RUN",
                null,
                "reconcile",
                new Dictionary<string, string>()),
            []);

        var service = new LiveWaveEvidenceReconciler(
            new FakeBaselineBuilder(live),
            new FakeProjectResolver(routing));
        var result = await service.ReconcileAsync(
            "pcc executive",
            persisted,
            [(task, new WorkerSlotId(1), parsed)]);

        Assert.Equal(ExternalReadStatus.Success, result.Status);
        Assert.NotNull(result.Value);
        Assert.Contains(result.Value!.BaselineReconciliation.Differences, x => x.Kind == ReconciliationDifferenceKind.HeadChanged);
        Assert.Equal(HandoffQuality.ContradictedByLiveEvidence, result.Value.Handoffs.Single().Quality);
        Assert.True(result.Value.HasContradiction);
    }

    [Fact]
    public async Task Stale_live_snapshot_remains_explicitly_stale()
    {
        var routing = Routing() with
        {
            Provenance = Routing().Provenance with { Freshness = EvidenceFreshness.Stale }
        };
        var baseline = Baseline(routing, "head", "pr-head") with { Freshness = EvidenceFreshness.Stale };
        var service = new LiveWaveEvidenceReconciler(
            new FakeBaselineBuilder(baseline, ExternalReadStatus.StaleCache, true),
            new FakeProjectResolver(routing, ProjectResolutionStatus.StaleCache));

        var result = await service.ReconcileAsync("pcc executive", baseline, []);

        Assert.Equal(ExternalReadStatus.StaleCache, result.Status);
        Assert.True(result.IsStale);
    }

    private static ManagerTaskProposal Proposal(string path)
    {
        var scope = TaskScope.Create("owner/repo", [path]);
        var task = new WorkerTask(
            TaskId.New(),
            "work",
            scope,
            new HashSet<TaskId>(),
            ["accepted"],
            TaskState.Proposed,
            TaskFingerprint.Create("work", scope));
        return new(
            task,
            ["head"],
            1,
            null,
            "needed",
            [],
            new HashSet<TaskId>(),
            ManagerExecutionMode.AutomaticStaged,
            ProjectScopeKind.Project,
            null,
            null,
            null,
            null,
            null,
            false);
    }

    private static ProjectRoutingSnapshot Routing()
    {
        var provenance = new ProjectControlProvenance(
            "owner/pcc",
            "pcc-sha",
            "v1.6.0",
            "1.2.0",
            Now,
            EvidenceFreshness.Current);
        return new(
            "PCCEXECUTIVE",
            "PCC Executive",
            "owner/repo",
            ProjectModel.Standalone,
            ProjectScopeKind.Project,
            null,
            null,
            ".",
            "READY",
            "READY",
            ["pcc executive"],
            [new CanonicalTaskSnapshot(
                "PCCEXECUTIVE-T0001",
                "PCCEXECUTIVE",
                "ISSUE-1",
                "v1",
                "IN_PROGRESS",
                "P0",
                "task/pcc-executive-t0001-v1",
                "main",
                "base",
                null,
                "0.1.0",
                ["project"],
                [],
                ["accepted"],
                [],
                [])],
            null,
            provenance);
    }

    private static ProjectBaselineSnapshot Baseline(ProjectRoutingSnapshot routing, string mainHead, string prHead)
    {
        var pr = new GitHubPullRequestSnapshot(
            routing.Repository,
            4,
            "PCCEXECUTIVE-T0001 worker",
            "open",
            false,
            "worker/test",
            prHead,
            "task/pcc-executive-t0001-v1",
            "base",
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
            new GitHubCheckSummary(routing.Repository, mainHead, "success", []),
            routing.DesiredState,
            null,
            [],
            Now,
            routing.Provenance.Freshness);
    }

    private sealed class FakeBaselineBuilder(
        ProjectBaselineSnapshot snapshot,
        ExternalReadStatus status = ExternalReadStatus.Success,
        bool stale = false) : IProjectBaselineBuilder
    {
        public Task<ExternalResult<ProjectBaselineSnapshot>> BuildAsync(string nameOrAlias, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExternalResult<ProjectBaselineSnapshot>(status, snapshot, Now, stale));
    }

    private sealed class FakeProjectResolver(
        ProjectRoutingSnapshot routing,
        ProjectResolutionStatus status = ProjectResolutionStatus.Success) : IProjectControlResolver
    {
        public Task<ProjectResolution> ResolveProjectAsync(string nameOrAlias, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProjectResolution(status, routing, null));

        public Task<ProjectResolution> GetProjectAsync(string projectControlId, string? variantId = null, ProjectScopeKind? scope = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProjectResolution(status, routing, null));

        public Task<ExternalResult<ProjectRoutingSnapshot>> GetRoutingSnapshotAsync(string projectControlId, string? variantId = null, ProjectScopeKind? scope = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExternalResult<ProjectRoutingSnapshot>(ExternalReadStatus.Success, routing, Now));

        public Task<ExternalResult<IReadOnlyList<CanonicalTaskSnapshot>>> GetCanonicalTasksAsync(string projectControlId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExternalResult<IReadOnlyList<CanonicalTaskSnapshot>>(ExternalReadStatus.Success, routing.CanonicalTasks, Now));

        public Task<ExternalResult<DesiredStateSnapshot>> GetDesiredStateAsync(string projectControlId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExternalResult<DesiredStateSnapshot>(ExternalReadStatus.NotFound, null, Now));
    }

    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-28T00:45:00Z");
}
