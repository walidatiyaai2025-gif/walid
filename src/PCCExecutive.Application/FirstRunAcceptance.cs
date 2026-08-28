namespace PCCExecutive.Application;

public enum FirstRunErrorCode
{
    NONE,
    PROJECT_NOT_FOUND,
    ROUTING_NOT_READY,
    INVALID_MANAGER_PLAN,
    WORKER_LIMIT,
    DEPENDENCY_CYCLE,
    OVERLAP,
    STALE_EVIDENCE,
    HANDOFF_INVALID,
    SUBMITTED_UNKNOWN,
    LOGIN_REQUIRED,
    BLOCKED_EXTERNAL
}

public sealed record FirstRunApplicationState(
    bool ProjectSelected,
    bool HasActiveWave,
    int ActiveWorkerTasks,
    bool BrowserConnected,
    int AttentionCount,
    bool UpdateAvailable)
{
    public static FirstRunApplicationState Empty { get; } = new(false, false, 0, false, 0, false);

    public bool IsSafeIdle =>
        !ProjectSelected &&
        !HasActiveWave &&
        ActiveWorkerTasks == 0 &&
        !BrowserConnected &&
        AttentionCount == 0 &&
        !UpdateAvailable;
}

public static class FirstRunErrorContract
{
    public static FirstRunErrorCode FromProjectResolution(ProjectResolutionStatus status) => status switch
    {
        ProjectResolutionStatus.ProjectNotFound => FirstRunErrorCode.PROJECT_NOT_FOUND,
        ProjectResolutionStatus.RoutingNotReady or
        ProjectResolutionStatus.RoutingConflict or
        ProjectResolutionStatus.VariantRequired => FirstRunErrorCode.ROUTING_NOT_READY,
        _ => FirstRunErrorCode.NONE
    };

    public static FirstRunErrorCode FromManagerPlan(ManagerPlanParseResult parse) =>
        parse.Findings.Any(x => string.Equals(x.Code, "WORKER_LIMIT", StringComparison.OrdinalIgnoreCase))
            ? FirstRunErrorCode.WORKER_LIMIT
            : parse.IsValid
                ? FirstRunErrorCode.NONE
                : FirstRunErrorCode.INVALID_MANAGER_PLAN;

    public static FirstRunErrorCode FromWaveValidation(OrchestrationWaveValidation validation)
    {
        if (validation.Findings.Any(x => string.Equals(x.Code, "DEPENDENCY_CYCLE", StringComparison.OrdinalIgnoreCase)))
            return FirstRunErrorCode.DEPENDENCY_CYCLE;
        if (validation.Findings.Any(x => string.Equals(x.Code, "WORKER_LIMIT", StringComparison.OrdinalIgnoreCase)))
            return FirstRunErrorCode.WORKER_LIMIT;
        if (validation.RequiresSequentialization || validation.Findings.Any(x => string.Equals(x.Code, "OVERLAPPING_SCOPE", StringComparison.OrdinalIgnoreCase)))
            return FirstRunErrorCode.OVERLAP;
        return FirstRunErrorCode.NONE;
    }

    public static FirstRunErrorCode FromHandoff(HandoffAssessment assessment) => assessment.Quality switch
    {
        HandoffQuality.Stale or HandoffQuality.ContradictedByLiveEvidence => FirstRunErrorCode.STALE_EVIDENCE,
        HandoffQuality.Invalid or HandoffQuality.Partial => FirstRunErrorCode.HANDOFF_INVALID,
        _ => FirstRunErrorCode.NONE
    };

    public static FirstRunErrorCode FromDispatch(PCCExecutive.Domain.Dispatch dispatch) =>
        dispatch.State == PCCExecutive.Domain.DispatchState.SUBMITTED_UNKNOWN
            ? FirstRunErrorCode.SUBMITTED_UNKNOWN
            : FirstRunErrorCode.NONE;

    public static FirstRunErrorCode FromAttention(AttentionClassification classification) =>
        classification.RequiresAttention && classification.Category == AttentionCategory.LOGIN_REQUIRED
            ? FirstRunErrorCode.LOGIN_REQUIRED
            : FirstRunErrorCode.NONE;

    public static FirstRunErrorCode FromBlocker(PolicyBlocker blocker) =>
        !blocker.IsResolved && blocker.Category is BlockerCategory.EXTERNAL_SERVICE or BlockerCategory.EXTERNAL_AUTHORITY
            ? FirstRunErrorCode.BLOCKED_EXTERNAL
            : FirstRunErrorCode.NONE;
}
