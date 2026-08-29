using PCCExecutive.Domain;

namespace PCCExecutive.Application;

public sealed record LiveWaveReconciliation(
    ProjectBaselineSnapshot Persisted,
    ProjectBaselineSnapshot Live,
    ReconciliationSnapshot BaselineReconciliation,
    ProjectRoutingSnapshot Routing,
    IReadOnlyList<HandoffAssessment> Handoffs,
    DateTimeOffset CapturedAt)
{
    public bool HasContradiction =>
        BaselineReconciliation.HasContradiction ||
        Handoffs.Any(x => x.Quality is HandoffQuality.Stale or HandoffQuality.ContradictedByLiveEvidence or HandoffQuality.Invalid);
}

public sealed class LiveWaveEvidenceReconciler
{
    private readonly IProjectBaselineBuilder _baselineBuilder;
    private readonly IProjectControlResolver _projectControl;
    private readonly WorkerHandoffQualityGate _handoffGate;

    public LiveWaveEvidenceReconciler(
        IProjectBaselineBuilder baselineBuilder,
        IProjectControlResolver projectControl,
        WorkerHandoffQualityGate? handoffGate = null)
    {
        _baselineBuilder = baselineBuilder;
        _projectControl = projectControl;
        _handoffGate = handoffGate ?? new WorkerHandoffQualityGate();
    }

    public async Task<ExternalResult<LiveWaveReconciliation>> ReconcileAsync(
        string projectNameOrAlias,
        ProjectBaselineSnapshot persisted,
        IReadOnlyList<(ManagerTaskProposal Expected, WorkerSlotId Slot, HandoffAssessment Parsed)> handoffs,
        CancellationToken cancellationToken = default)
    {
        var liveResult = await _baselineBuilder.BuildAsync(projectNameOrAlias, cancellationToken);
        if (!liveResult.IsSuccess || liveResult.Value is null)
            return new(liveResult.Status, null, liveResult.CapturedAt, liveResult.IsStale, liveResult.ErrorCode);

        var routingResult = await _projectControl.ResolveProjectAsync(projectNameOrAlias, cancellationToken);
        if (!routingResult.IsSuccess || routingResult.Project is null)
            return new(MapRoutingFailure(routingResult.Status), null, DateTimeOffset.UtcNow, routingResult.Status == ProjectResolutionStatus.StaleCache, routingResult.Message);

        var live = liveResult.Value;
        var routing = routingResult.Project;
        var baselineReconciliation = new SnapshotReconciler().Compare(persisted, live);
        var validated = handoffs
            .Select(x => _handoffGate.Validate(x.Parsed, x.Expected, x.Slot, routing, live))
            .ToArray();
        var stale = liveResult.IsStale || routing.Provenance.Freshness == EvidenceFreshness.Stale;
        var capturedAt = new[] { live.CapturedAt, routing.Provenance.CapturedAt }.Max();
        var result = new LiveWaveReconciliation(
            persisted,
            live,
            baselineReconciliation,
            routing,
            validated,
            capturedAt);
        return new(stale ? ExternalReadStatus.StaleCache : ExternalReadStatus.Success, result, capturedAt, stale);
    }

    private static ExternalReadStatus MapRoutingFailure(ProjectResolutionStatus status) => status switch
    {
        ProjectResolutionStatus.ProjectNotFound => ExternalReadStatus.NotFound,
        ProjectResolutionStatus.Unauthorized => ExternalReadStatus.Unauthorized,
        ProjectResolutionStatus.RateLimited => ExternalReadStatus.RateLimited,
        ProjectResolutionStatus.Offline => ExternalReadStatus.Offline,
        ProjectResolutionStatus.StaleCache => ExternalReadStatus.StaleCache,
        ProjectResolutionStatus.RoutingConflict or ProjectResolutionStatus.RoutingNotReady or ProjectResolutionStatus.VariantRequired => ExternalReadStatus.RoutingConflict,
        _ => ExternalReadStatus.TemporaryFailure
    };
}

public sealed class SafeDispatchPlanner
{
    private readonly DependencyAwareWaveScheduler _scheduler;

    public SafeDispatchPlanner(DependencyAwareWaveScheduler? scheduler = null) =>
        _scheduler = scheduler ?? new DependencyAwareWaveScheduler();

    public DispatchBatch Schedule(
        StructuredManagerPlan plan,
        IReadOnlyDictionary<TaskId, TaskState> taskStates,
        IReadOnlySet<WorkerSlotId> occupiedSlots,
        RuntimeHealthSnapshot health)
    {
        var deferred = new List<DeferredTask>();
        var eligible = new List<ManagerTaskProposal>();

        foreach (var proposal in plan.Tasks)
        {
            if (!taskStates.TryGetValue(proposal.Task.Id, out var state))
            {
                eligible.Add(proposal);
                continue;
            }

            if (state is TaskState.Completed or TaskState.Cancelled)
                continue;

            if (state is TaskState.Assigned or TaskState.Dispatched or TaskState.Running or TaskState.HandoffReceived or TaskState.Validating)
            {
                deferred.Add(new DeferredTask(proposal.Task.Id, $"TASK_ALREADY_ACTIVE:{state}"));
                continue;
            }

            eligible.Add(proposal);
        }

        var filtered = plan with { Tasks = eligible };
        var scheduled = _scheduler.Schedule(filtered, taskStates, occupiedSlots, health);
        return new DispatchBatch(
            scheduled.Assignments,
            deferred.Concat(scheduled.Deferred).ToArray(),
            scheduled.DelayBeforeNextDispatch);
    }
}
