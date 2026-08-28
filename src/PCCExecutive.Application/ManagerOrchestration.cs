using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PCCExecutive.Domain;

namespace PCCExecutive.Application;

public enum ManagerExecutionMode { AutomaticStaged, Sequential, Manual }
public enum PlanFindingSeverity { Info, Sequentialize, Block }
public enum HandoffQuality { Valid, Partial, Invalid, Stale, ContradictedByLiveEvidence }
public enum OrchestrationDecision { Continue, Replan, Sequentialize, ReassignOnce, ClosureRepair, StalledAutoStopped, AttentionRequired }
public enum OrchestrationPhase { Initializing, ManagerPlanning, WaveValidation, Dispatching, WaveRunning, Reconciling, ManagerReview, ClosureMode, VerifiedComplete, BlockedExternal, StalledAutoStopped, StoppedByOperator }

public sealed record ManagerTaskProposal(
    WorkerTask Task,
    IReadOnlyList<string> EvidenceExpected,
    int Priority,
    WorkerSlotId? SuggestedWorkerSlot,
    string Reason,
    IReadOnlyList<string> KnownBlockers,
    IReadOnlySet<TaskId> RequiredPreviousTasks,
    ManagerExecutionMode RecommendedExecutionMode,
    ProjectScopeKind TargetScope,
    string? TargetVariant,
    string? ExpectedHead,
    int? RelatedPullRequest,
    string? ExpectedPullRequestState,
    string? TargetBranch,
    bool FeatureExpansion);

public sealed record StructuredManagerPlan(
    ManagerEstimate ManagerEstimate,
    IReadOnlyList<ManagerTaskProposal> Tasks,
    string? ExpectedHead,
    string? ExpectedRoutingIdentity,
    string? ProjectDecision,
    IReadOnlyList<string> KnownBlockers);

public sealed record ManagerPlanFinding(string Code, string Message, PlanFindingSeverity Severity, TaskId? TaskId = null, TaskId? OtherTaskId = null);
public sealed record ManagerPlanParseResult(bool IsValid, StructuredManagerPlan? Plan, IReadOnlyList<ManagerPlanFinding> Findings);
public sealed record OrchestrationWaveValidation(bool IsValid, bool RequiresSequentialization, IReadOnlyList<ManagerPlanFinding> Findings);

public sealed class StructuredManagerPlanParser
{
    private readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

    public ManagerPlanParseResult Parse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return Invalid("MANAGER_PLAN_EMPTY", "Manager plan is empty.");

        WirePlan? wire;
        try
        {
            wire = JsonSerializer.Deserialize<WirePlan>(content, _json);
        }
        catch (JsonException ex)
        {
            return Invalid("MANAGER_PLAN_NOT_STRUCTURED", $"Manager output is not valid structured JSON: {ex.Message}");
        }

        if (wire is null || wire.Tasks is null)
            return Invalid("MANAGER_PLAN_NOT_STRUCTURED", "Manager output must contain a structured tasks array.");

        var findings = new List<ManagerPlanFinding>();
        if (wire.Tasks.Count > WorkerSlotPolicy.MaximumActiveWorkers)
            findings.Add(new("WORKER_LIMIT", "Manager proposed more than five Worker tasks.", PlanFindingSeverity.Block));
        if (wire.ManagerEstimate is < 0 or > 100)
            findings.Add(new("MANAGER_ESTIMATE_RANGE", "ManagerEstimate must be between 0 and 100.", PlanFindingSeverity.Block));

        var tasks = new List<ManagerTaskProposal>();
        var ids = new HashSet<TaskId>();
        foreach (var item in wire.Tasks)
        {
            if (!TryTaskId(item.TaskId, out var id))
            {
                findings.Add(new("TASK_ID_INVALID", "TaskId must be a non-empty stable GUID.", PlanFindingSeverity.Block));
                continue;
            }

            if (!ids.Add(id))
                findings.Add(new("DUPLICATE_TASK_ID", "TaskId appears more than once.", PlanFindingSeverity.Block, id));

            if (string.IsNullOrWhiteSpace(item.Objective))
                findings.Add(new("OBJECTIVE_REQUIRED", "Objective is required.", PlanFindingSeverity.Block, id));
            if (string.IsNullOrWhiteSpace(item.Repository))
                findings.Add(new("REPOSITORY_REQUIRED", "Repository is required.", PlanFindingSeverity.Block, id));
            if (item.AcceptanceCriteria is null || item.AcceptanceCriteria.Count == 0)
                findings.Add(new("ACCEPTANCE_REQUIRED", "At least one acceptance criterion is required.", PlanFindingSeverity.Block, id));
            if (item.EvidenceExpected is null || item.EvidenceExpected.Count == 0)
                findings.Add(new("EVIDENCE_EXPECTED_REQUIRED", "EvidenceExpected is required.", PlanFindingSeverity.Block, id));
            if (string.IsNullOrWhiteSpace(item.Reason))
                findings.Add(new("REASON_REQUIRED", "Reason is required.", PlanFindingSeverity.Block, id));

            var dependencies = ParseIds(item.Dependencies, id, "DEPENDENCY_ID_INVALID", findings);
            var requiredPrevious = ParseIds(item.RequiredPreviousTasks, id, "PREVIOUS_TASK_ID_INVALID", findings);
            var effectiveDependencies = new HashSet<TaskId>(dependencies);
            effectiveDependencies.UnionWith(requiredPrevious);
            var scope = TaskScope.Create(item.Repository ?? string.Empty, item.Paths, item.Components, item.ExclusiveResources);
            var fingerprint = TaskFingerprint.Create(item.Objective ?? string.Empty, scope, effectiveDependencies);
            var state = TaskState.Proposed;
            var workerTask = new WorkerTask(
                id,
                item.Objective ?? string.Empty,
                scope,
                effectiveDependencies,
                item.AcceptanceCriteria ?? [],
                state,
                fingerprint);

            WorkerSlotId? slot = null;
            if (item.SuggestedWorkerSlot is not null)
            {
                try { slot = new WorkerSlotId(item.SuggestedWorkerSlot.Value); }
                catch (ArgumentOutOfRangeException)
                {
                    findings.Add(new("WORKER_SLOT_INVALID", "SuggestedWorkerSlot must be between 1 and 5.", PlanFindingSeverity.Block, id));
                }
            }

            if (!Enum.TryParse<ProjectScopeKind>(item.TargetScope ?? "Project", true, out var targetScope))
            {
                targetScope = ProjectScopeKind.Project;
                findings.Add(new("TARGET_SCOPE_INVALID", "TargetScope must be Project, Core, or Variant.", PlanFindingSeverity.Block, id));
            }

            if (!Enum.TryParse<ManagerExecutionMode>(item.RecommendedExecutionMode ?? "AutomaticStaged", true, out var executionMode))
            {
                executionMode = ManagerExecutionMode.AutomaticStaged;
                findings.Add(new("EXECUTION_MODE_INVALID", "RecommendedExecutionMode is invalid.", PlanFindingSeverity.Block, id));
            }

            tasks.Add(new(
                workerTask,
                item.EvidenceExpected ?? [],
                item.Priority,
                slot,
                item.Reason ?? string.Empty,
                item.KnownBlockers ?? [],
                requiredPrevious,
                executionMode,
                targetScope,
                item.TargetVariant,
                item.ExpectedHead,
                item.RelatedPullRequest,
                item.ExpectedPullRequestState,
                item.TargetBranch,
                item.FeatureExpansion));
        }

        var plan = new StructuredManagerPlan(
            new ManagerEstimate(Math.Clamp(wire.ManagerEstimate, 0m, 100m)),
            tasks,
            wire.ExpectedHead,
            wire.ExpectedRoutingIdentity,
            wire.ProjectDecision,
            wire.KnownBlockers ?? []);

        return new(findings.All(x => x.Severity != PlanFindingSeverity.Block), plan, findings);
    }

    private static IReadOnlySet<TaskId> ParseIds(
        IReadOnlyList<string>? values,
        TaskId owner,
        string code,
        List<ManagerPlanFinding> findings)
    {
        var result = new HashSet<TaskId>();
        foreach (var value in values ?? [])
        {
            if (!TryTaskId(value, out var id))
                findings.Add(new(code, $"Invalid task identity '{value}'.", PlanFindingSeverity.Block, owner));
            else
                result.Add(id);
        }
        return result;
    }

    private static bool TryTaskId(string? value, out TaskId id)
    {
        if (Guid.TryParse(value, out var guid) && guid != Guid.Empty)
        {
            id = new TaskId(guid);
            return true;
        }
        id = default;
        return false;
    }

    private static ManagerPlanParseResult Invalid(string code, string message) =>
        new(false, null, [new(code, message, PlanFindingSeverity.Block)]);

    private sealed class WirePlan
    {
        public decimal ManagerEstimate { get; set; }
        public List<WireTask>? Tasks { get; set; }
        public string? ExpectedHead { get; set; }
        public string? ExpectedRoutingIdentity { get; set; }
        public string? ProjectDecision { get; set; }
        public List<string>? KnownBlockers { get; set; }
    }

    private sealed class WireTask
    {
        public string? TaskId { get; set; }
        public string? Objective { get; set; }
        public string? Repository { get; set; }
        public List<string>? Paths { get; set; }
        public List<string>? Components { get; set; }
        public List<string>? ExclusiveResources { get; set; }
        public List<string>? Dependencies { get; set; }
        public List<string>? AcceptanceCriteria { get; set; }
        public List<string>? EvidenceExpected { get; set; }
        public int Priority { get; set; }
        public int? SuggestedWorkerSlot { get; set; }
        public string? Reason { get; set; }
        public List<string>? KnownBlockers { get; set; }
        public List<string>? RequiredPreviousTasks { get; set; }
        public string? RecommendedExecutionMode { get; set; }
        public string? TargetScope { get; set; }
        public string? TargetVariant { get; set; }
        public string? ExpectedHead { get; set; }
        public int? RelatedPullRequest { get; set; }
        public string? ExpectedPullRequestState { get; set; }
        public string? TargetBranch { get; set; }
        public bool FeatureExpansion { get; set; }
    }
}

public sealed class ManagerWaveValidator
{
    private readonly WaveValidator _core;
    private readonly ScopeOverlapDetector _overlap;

    public ManagerWaveValidator(WaveValidator? core = null, ScopeOverlapDetector? overlap = null)
    {
        _core = core ?? new WaveValidator();
        _overlap = overlap ?? new ScopeOverlapDetector();
    }

    public OrchestrationWaveValidation Validate(
        StructuredManagerPlan plan,
        ProjectRoutingSnapshot routing,
        ProjectBaselineSnapshot baseline,
        ICompletedTaskIndex completed,
        ProjectCompletionMode completionMode)
    {
        var findings = new List<ManagerPlanFinding>();
        var wave = new WavePlan(WaveId.New(), plan.ManagerEstimate, plan.Tasks.Select(x => x.Task).ToArray(), []);
        var core = _core.Validate(wave, completed);

        foreach (var issue in core.Issues)
        {
            var severity = issue.Code == "OVERLAPPING_SCOPE" ? PlanFindingSeverity.Sequentialize : PlanFindingSeverity.Block;
            findings.Add(new(issue.Code, issue.Message, severity, issue.TaskId, issue.OtherTaskId));
        }

        if (!string.IsNullOrWhiteSpace(plan.ExpectedHead) &&
            !string.Equals(plan.ExpectedHead, baseline.DefaultHeadSha, StringComparison.OrdinalIgnoreCase))
            findings.Add(new("STALE_HEAD", $"Manager expected HEAD {plan.ExpectedHead} but live HEAD is {baseline.DefaultHeadSha}.", PlanFindingSeverity.Block));

        if (!string.IsNullOrWhiteSpace(plan.ExpectedRoutingIdentity) &&
            !string.Equals(plan.ExpectedRoutingIdentity, routing.RoutingIdentity, StringComparison.Ordinal))
            findings.Add(new("ROUTING_CHANGED", "Manager plan was built against a different PCC routing identity.", PlanFindingSeverity.Block));

        foreach (var proposal in plan.Tasks)
        {
            var task = proposal.Task;
            if (!string.Equals(task.Scope.Repository, routing.Repository, StringComparison.OrdinalIgnoreCase))
                findings.Add(new("WRONG_REPOSITORY", $"Task targets {task.Scope.Repository}; PCC routes project to {routing.Repository}.", PlanFindingSeverity.Block, task.Id));

            if (proposal.TargetScope != routing.Scope)
                findings.Add(new("WRONG_PCC_SCOPE", $"Task scope {proposal.TargetScope} conflicts with routed scope {routing.Scope}.", PlanFindingSeverity.Block, task.Id));

            if (routing.Scope == ProjectScopeKind.Variant &&
                !string.Equals(proposal.TargetVariant, routing.VariantId, StringComparison.OrdinalIgnoreCase))
                findings.Add(new("WRONG_PCC_VARIANT", $"Task variant '{proposal.TargetVariant}' conflicts with routed variant '{routing.VariantId}'.", PlanFindingSeverity.Block, task.Id));

            var expectedHead = proposal.ExpectedHead;
            if (!string.IsNullOrWhiteSpace(expectedHead) &&
                !string.Equals(expectedHead, baseline.DefaultHeadSha, StringComparison.OrdinalIgnoreCase) &&
                !baseline.RelevantPullRequests.Any(pr => string.Equals(pr.HeadSha, expectedHead, StringComparison.OrdinalIgnoreCase)))
                findings.Add(new("STALE_HEAD", $"Task assumes stale/unrecognized HEAD {expectedHead}.", PlanFindingSeverity.Block, task.Id));

            ValidatePullRequestAssumption(proposal, baseline, findings);
            ValidateBranchAssumption(proposal, baseline, findings);

            if (completionMode == ProjectCompletionMode.ClosureMode && proposal.FeatureExpansion)
                findings.Add(new("CLOSURE_FEATURE_EXPANSION", "Closure Mode rejects unrelated feature expansion.", PlanFindingSeverity.Block, task.Id));
        }

        for (var i = 0; i < plan.Tasks.Count; i++)
        for (var j = i + 1; j < plan.Tasks.Count; j++)
        {
            var left = plan.Tasks[i];
            var right = plan.Tasks[j];
            if (_overlap.Overlaps(left.Task.Scope, right.Task.Scope) &&
                !findings.Any(x => x.Code == "OVERLAPPING_SCOPE" &&
                                   ((x.TaskId == left.Task.Id && x.OtherTaskId == right.Task.Id) ||
                                    (x.TaskId == right.Task.Id && x.OtherTaskId == left.Task.Id))))
                findings.Add(new("OVERLAPPING_SCOPE", "Worker scopes overlap; scheduler must serialize them.", PlanFindingSeverity.Sequentialize, left.Task.Id, right.Task.Id));
        }

        return new(
            findings.All(x => x.Severity != PlanFindingSeverity.Block),
            findings.Any(x => x.Severity == PlanFindingSeverity.Sequentialize),
            findings);
    }

    private static void ValidatePullRequestAssumption(
        ManagerTaskProposal proposal,
        ProjectBaselineSnapshot baseline,
        List<ManagerPlanFinding> findings)
    {
        if (proposal.RelatedPullRequest is null) return;
        var pr = baseline.RelevantPullRequests.FirstOrDefault(x => x.Number == proposal.RelatedPullRequest.Value);
        if (pr is null)
        {
            findings.Add(new("PR_ASSUMPTION_NOT_FOUND", $"Referenced PR #{proposal.RelatedPullRequest} is not present in live relevant evidence.", PlanFindingSeverity.Block, proposal.Task.Id));
            return;
        }

        if (!string.IsNullOrWhiteSpace(proposal.ExpectedPullRequestState) &&
            !string.Equals(proposal.ExpectedPullRequestState, pr.State, StringComparison.OrdinalIgnoreCase))
            findings.Add(new(
                pr.Merged ? "PR_ALREADY_MERGED" : "PR_STATE_CHANGED",
                $"Manager expected PR #{pr.Number} state {proposal.ExpectedPullRequestState}; live state is {pr.State}, merged={pr.Merged}.",
                PlanFindingSeverity.Block,
                proposal.Task.Id));
    }

    private static void ValidateBranchAssumption(
        ManagerTaskProposal proposal,
        ProjectBaselineSnapshot baseline,
        List<ManagerPlanFinding> findings)
    {
        if (string.IsNullOrWhiteSpace(proposal.TargetBranch)) return;
        var known = baseline.CanonicalTasks
            .Select(x => x.CanonicalBranch)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Concat(baseline.RelevantPullRequests.Select(x => x.HeadBranch))
            .Any(x => string.Equals(x, proposal.TargetBranch, StringComparison.OrdinalIgnoreCase));
        if (!known)
            findings.Add(new("TASK_BRANCH_UNVERIFIED", $"Target branch '{proposal.TargetBranch}' is not verified by canonical task/PR evidence.", PlanFindingSeverity.Block, proposal.Task.Id));
    }
}

public sealed record RuntimeHealthSnapshot(bool GlobalPause, bool AdaptivePacing, TimeSpan SuggestedDelay, string? Reason);
public sealed record WorkerAssignment(TaskId TaskId, WorkerSlotId SlotId, int Priority);
public sealed record DeferredTask(TaskId TaskId, string Reason);
public sealed record DispatchBatch(IReadOnlyList<WorkerAssignment> Assignments, IReadOnlyList<DeferredTask> Deferred, TimeSpan DelayBeforeNextDispatch);

public sealed class DependencyAwareWaveScheduler
{
    private readonly ScopeOverlapDetector _overlap = new();

    public DispatchBatch Schedule(
        StructuredManagerPlan plan,
        IReadOnlyDictionary<TaskId, TaskState> taskStates,
        IReadOnlySet<WorkerSlotId> occupiedSlots,
        RuntimeHealthSnapshot health)
    {
        if (health.GlobalPause)
            return new([], plan.Tasks.Select(x => new DeferredTask(x.Task.Id, $"GLOBAL_RUNTIME_PAUSE:{health.Reason ?? "runtime"}")).ToArray(), health.SuggestedDelay);

        var free = Enumerable.Range(1, WorkerSlotPolicy.MaximumActiveWorkers)
            .Select(x => new WorkerSlotId(x))
            .Where(x => !occupiedSlots.Contains(x))
            .ToList();

        var selected = new List<ManagerTaskProposal>();
        var assignments = new List<WorkerAssignment>();
        var deferred = new List<DeferredTask>();

        foreach (var proposal in plan.Tasks
                     .OrderBy(x => x.Priority)
                     .ThenBy(x => x.Task.Id.ToString(), StringComparer.Ordinal))
        {
            var task = proposal.Task;
            if (taskStates.TryGetValue(task.Id, out var state) && state is TaskState.Completed or TaskState.Cancelled)
                continue;

            var unmet = task.Dependencies
                .Where(dep => !taskStates.TryGetValue(dep, out var depState) || depState != TaskState.Completed)
                .ToArray();
            if (unmet.Length > 0)
            {
                deferred.Add(new(task.Id, $"WAITING_DEPENDENCY:{string.Join(",", unmet)}"));
                continue;
            }

            if (selected.Any(x => _overlap.Overlaps(x.Task.Scope, task.Scope)))
            {
                deferred.Add(new(task.Id, "SEQUENTIALIZED_SCOPE_COLLISION"));
                continue;
            }

            if (free.Count == 0)
            {
                deferred.Add(new(task.Id, "NO_FREE_WORKER_SLOT"));
                continue;
            }

            var preferred = proposal.SuggestedWorkerSlot;
            WorkerSlotId slot;
            if (preferred is not null && free.Contains(preferred.Value))
            {
                slot = preferred.Value;
                free.Remove(slot);
            }
            else
            {
                slot = free[0];
                free.RemoveAt(0);
            }

            selected.Add(proposal);
            assignments.Add(new(task.Id, slot, proposal.Priority));
        }

        var delay = health.AdaptivePacing
            ? (health.SuggestedDelay > TimeSpan.Zero ? health.SuggestedDelay : TimeSpan.FromSeconds(10))
            : TimeSpan.FromSeconds(10);
        return new(assignments, deferred, delay);
    }
}

public sealed record SessionValidationResult(bool IsValid, string? Evidence, string? ErrorCode);
public sealed record DispatchObservation(DispatchId DispatchId, DispatchState State, string? Evidence);
public sealed record DispatchPreparation(Dispatch Dispatch, bool ExistingReservation, bool RequiresReconciliation);
public interface IAgentSessionGuard
{
    Task<SessionValidationResult> ValidateAsync(ProjectRunId runId, LogicalAgentId agentId, ConversationId conversationId, TaskId taskId, CancellationToken cancellationToken = default);
}
public interface IWorkerResultCollector
{
    Task<string?> CollectAsync(Dispatch dispatch, CancellationToken cancellationToken = default);
}
public interface IDispatchObservationPort
{
    Task<DispatchObservation> ObserveAsync(Dispatch dispatch, CancellationToken cancellationToken = default);
}
public sealed class DispatchExecutionFacade
{
    private readonly IDispatchObservationPort _observer;
    private readonly IWorkerResultCollector _collector;

    public DispatchExecutionFacade(IDispatchObservationPort observer, IWorkerResultCollector collector)
    {
        _observer = observer;
        _collector = collector;
    }

    public Task<DispatchObservation> ObserveDispatchAsync(Dispatch dispatch, CancellationToken cancellationToken = default) =>
        _observer.ObserveAsync(dispatch, cancellationToken);

    public Task<string?> CollectWorkerResultAsync(Dispatch dispatch, CancellationToken cancellationToken = default) =>
        _collector.CollectAsync(dispatch, cancellationToken);
}

public sealed class DispatchCoordinator
{
    private readonly IAgentProvider _provider;
    private readonly IAgentSessionGuard _sessionGuard;
    private readonly IDispatchIdempotencyStore _idempotency;
    private readonly IDispatchReconciliationService _reconciliation;

    public DispatchCoordinator(
        IAgentProvider provider,
        IAgentSessionGuard sessionGuard,
        IDispatchIdempotencyStore idempotency,
        IDispatchReconciliationService reconciliation)
    {
        _provider = provider;
        _sessionGuard = sessionGuard;
        _idempotency = idempotency;
        _reconciliation = reconciliation;
    }

    public async Task<DispatchPreparation> PrepareDispatchAsync(
        ProjectRunId runId,
        WaveId waveId,
        TaskId taskId,
        LogicalAgentId agentId,
        ConversationId conversationId,
        string content,
        CancellationToken cancellationToken = default)
    {
        var contentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
        var existing = await _idempotency.FindEquivalentAsync(runId, agentId, contentHash, cancellationToken);
        if (existing is not null && existing.State != DispatchState.FAILED)
            return new(existing, true, existing.State == DispatchState.SUBMITTED_UNKNOWN);

        var dispatch = new Dispatch(
            DispatchId.New(),
            runId,
            waveId,
            taskId,
            agentId,
            conversationId,
            contentHash,
            DateTimeOffset.UtcNow,
            DispatchState.PREPARED,
            null,
            null,
            null,
            existing?.Id,
            null);
        await _idempotency.ReserveAsync(dispatch, cancellationToken);
        return new(dispatch, false, false);
    }

    public async Task<Dispatch> SubmitDispatchAsync(Dispatch dispatch, string content, CancellationToken cancellationToken = default)
    {
        if (dispatch.State == DispatchState.SUBMITTED_UNKNOWN)
        {
            var reconciled = await _reconciliation.ReconcileAsync(dispatch, cancellationToken);
            if (reconciled.ResolvedState == DispatchState.ACKNOWLEDGED)
                return dispatch with { State = DispatchState.ACKNOWLEDGED, ReconciliationEvidence = reconciled.Evidence };
            if (!reconciled.SafeToCreateRetry)
                return dispatch with { ReconciliationEvidence = reconciled.Evidence };
        }
        else if (dispatch.State != DispatchState.PREPARED)
        {
            return dispatch;
        }

        var session = await _sessionGuard.ValidateAsync(
            dispatch.ProjectRunId,
            dispatch.LogicalAgentId,
            dispatch.ConversationId,
            dispatch.TaskId,
            cancellationToken);
        if (!session.IsValid)
            return dispatch with { State = DispatchState.FAILED, ReconciliationEvidence = $"SESSION_INVALID:{session.ErrorCode}:{session.Evidence}" };

        var result = await _provider.SendAsync(
            new AgentRequest(dispatch.ProjectRunId, dispatch.LogicalAgentId, dispatch.ConversationId, dispatch.Id, content, dispatch.ContentHash),
            cancellationToken);

        var now = DateTimeOffset.UtcNow;
        if (result.IsUncertain)
            return dispatch with { State = DispatchState.SUBMITTED_UNKNOWN, SubmittedAt = now, ReconciliationEvidence = result.ProviderEvidence ?? result.ErrorCode };
        if (!result.Accepted)
            return dispatch with { State = DispatchState.FAILED, ReconciliationEvidence = result.ProviderEvidence ?? result.ErrorCode };
        if (result.IsComplete)
            return dispatch with { State = DispatchState.COMPLETED, SubmittedAt = now, AcknowledgedAt = now, CompletedAt = now, ReconciliationEvidence = result.ProviderEvidence };
        if (result.IsGenerating)
            return dispatch with { State = DispatchState.GENERATING, SubmittedAt = now, AcknowledgedAt = now, ReconciliationEvidence = result.ProviderEvidence };
        return dispatch with { State = DispatchState.ACKNOWLEDGED, SubmittedAt = now, AcknowledgedAt = now, ReconciliationEvidence = result.ProviderEvidence };
    }
}

public sealed record StrictWorkerHandoff(
    TaskId TaskId,
    WorkerSlotId WorkerSlot,
    string ProjectControlId,
    string Repository,
    string Status,
    string? Head,
    string? Branch,
    int? PullRequest,
    IReadOnlyList<string> Changed,
    string? Tests,
    string? Build,
    string? Blocker,
    string NextAction,
    IReadOnlyDictionary<string, string> Extensions);

public sealed record HandoffFinding(string Code, string Message);
public sealed record HandoffAssessment(HandoffQuality Quality, StrictWorkerHandoff? Handoff, IReadOnlyList<HandoffFinding> Findings);

public sealed class WorkerHandoffParser
{
    public HandoffAssessment Parse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return new(HandoffQuality.Partial, null, [new("HANDOFF_EMPTY", "Worker handoff is empty.")]);

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in content.Replace("\r", string.Empty).Split('\n'))
        {
            var index = line.IndexOf(':');
            if (index <= 0) continue;
            var key = line[..index].Trim();
            var value = line[(index + 1)..].Trim();
            if (!string.IsNullOrWhiteSpace(key)) fields[key] = value;
        }

        var required = new[] { "TASK", "WORKER_SLOT", "PROJECT", "REPOSITORY", "STATUS", "HEAD", "BRANCH", "PR", "CHANGED", "TESTS", "BUILD", "BLOCKER", "NEXT_ACTION" };
        var missing = required.Where(x => !fields.ContainsKey(x)).ToArray();
        if (missing.Length > 0)
            return new(HandoffQuality.Partial, null, missing.Select(x => new HandoffFinding("FIELD_MISSING", $"Required handoff field {x} is missing.")).ToArray());
        var blank = required.Where(x => string.IsNullOrWhiteSpace(fields[x])).ToArray();
        if (blank.Length > 0)
            return new(HandoffQuality.Partial, null, blank.Select(x => new HandoffFinding("FIELD_EMPTY", $"Required handoff field {x} is empty.")).ToArray());

        if (!Guid.TryParse(fields["TASK"], out var taskGuid) || taskGuid == Guid.Empty)
            return new(HandoffQuality.Invalid, null, [new("TASK_ID_INVALID", "TASK is not a stable TaskId.")]);
        if (!TryWorkerSlot(fields["WORKER_SLOT"], out var slot))
            return new(HandoffQuality.Invalid, null, [new("WORKER_SLOT_INVALID", "WORKER_SLOT must identify Worker 1..5.")]);

        int? pr = null;
        var prText = fields["PR"];
        if (IsApplicable(prText) && !TryPr(prText, out pr))
            return new(HandoffQuality.Invalid, null, [new("PR_INVALID", "PR must be numeric or explicitly N/A.")]);

        var extensions = fields
            .Where(x => !new HashSet<string>(required, StringComparer.OrdinalIgnoreCase).Contains(x.Key))
            .ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);

        var handoff = new StrictWorkerHandoff(
            new TaskId(taskGuid),
            slot,
            fields["PROJECT"],
            fields["REPOSITORY"],
            fields["STATUS"],
            IsApplicable(fields["HEAD"]) ? fields["HEAD"] : null,
            IsApplicable(fields["BRANCH"]) ? fields["BRANCH"] : null,
            pr,
            SplitList(fields["CHANGED"]),
            fields["TESTS"],
            fields["BUILD"],
            IsApplicable(fields["BLOCKER"]) ? fields["BLOCKER"] : null,
            fields["NEXT_ACTION"],
            extensions);
        return new(HandoffQuality.Valid, handoff, []);
    }

    private static IReadOnlyList<string> SplitList(string value) =>
        value.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool TryWorkerSlot(string value, out WorkerSlotId slot)
    {
        var digits = new string(value.Where(char.IsDigit).ToArray());
        if (int.TryParse(digits, out var number) && number is >= 1 and <= 5)
        {
            slot = new WorkerSlotId(number);
            return true;
        }
        slot = default;
        return false;
    }

    private static bool IsApplicable(string value) =>
        !string.Equals(value, "N/A", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(value, "NA", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(value, "NONE", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(value, "NULL", StringComparison.OrdinalIgnoreCase);

    private static bool TryPr(string value, out int? number)
    {
        var digits = new string(value.Where(char.IsDigit).ToArray());
        if (int.TryParse(digits, out var parsed) && parsed > 0)
        {
            number = parsed;
            return true;
        }
        number = null;
        return false;
    }
}

public sealed class WorkerHandoffQualityGate
{
    public HandoffAssessment Validate(
        HandoffAssessment parsed,
        ManagerTaskProposal expected,
        WorkerSlotId expectedSlot,
        ProjectRoutingSnapshot routing,
        ProjectBaselineSnapshot live)
    {
        if (parsed.Handoff is null) return parsed;
        var handoff = parsed.Handoff;
        var findings = new List<HandoffFinding>();

        if (handoff.TaskId != expected.Task.Id)
            findings.Add(new("TASK_MISMATCH", "Worker returned a different TaskId."));
        if (handoff.WorkerSlot != expectedSlot)
            findings.Add(new("WORKER_SLOT_MISMATCH", "WorkerSlot does not match the dispatch assignment."));
        if (!string.Equals(handoff.ProjectControlId, routing.ProjectControlId, StringComparison.OrdinalIgnoreCase))
            findings.Add(new("PROJECT_MISMATCH", "Worker project does not match PCC routing."));
        if (!string.Equals(handoff.Repository, routing.Repository, StringComparison.OrdinalIgnoreCase))
            findings.Add(new("REPOSITORY_MISMATCH", "Worker repository does not match PCC routing."));

        if (findings.Count > 0)
            return new(HandoffQuality.Invalid, handoff, findings);

        HandoffQuality quality = HandoffQuality.Valid;
        if (handoff.PullRequest is not null)
        {
            var pr = live.RelevantPullRequests.FirstOrDefault(x => x.Number == handoff.PullRequest.Value);
            if (pr is null)
            {
                quality = HandoffQuality.Stale;
                findings.Add(new("PR_NOT_IN_LIVE_EVIDENCE", $"PR #{handoff.PullRequest} is not present in live evidence."));
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(handoff.Head) &&
                    !string.Equals(handoff.Head, pr.HeadSha, StringComparison.OrdinalIgnoreCase))
                {
                    quality = HandoffQuality.ContradictedByLiveEvidence;
                    findings.Add(new("PR_HEAD_CONTRADICTION", $"Worker HEAD {handoff.Head} differs from live PR HEAD {pr.HeadSha}."));
                }
                if (handoff.Status.Contains("OPEN", StringComparison.OrdinalIgnoreCase) && pr.Merged)
                {
                    if (quality != HandoffQuality.ContradictedByLiveEvidence) quality = HandoffQuality.Stale;
                    findings.Add(new("PR_STATE_STALE", $"Worker says PR open but live PR #{pr.Number} is merged."));
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(handoff.Head) &&
            !string.Equals(handoff.Head, live.DefaultHeadSha, StringComparison.OrdinalIgnoreCase) &&
            !live.RelevantPullRequests.Any(x => string.Equals(x.HeadSha, handoff.Head, StringComparison.OrdinalIgnoreCase)))
        {
            quality = HandoffQuality.ContradictedByLiveEvidence;
            findings.Add(new("HEAD_NOT_LIVE", $"Worker HEAD {handoff.Head} is not present in current branch/PR evidence."));
        }

        if (ClaimsPassed(handoff.Build) && !IsGreen(live.Checks))
        {
            if (quality == HandoffQuality.Valid) quality = HandoffQuality.Partial;
            findings.Add(new("BUILD_CLAIM_UNSUPPORTED", "Worker says build passed but live CI/check evidence is not green."));
        }

        if (string.Equals(handoff.Status, "DONE", StringComparison.OrdinalIgnoreCase) &&
            (handoff.Changed.Count == 0 || string.IsNullOrWhiteSpace(handoff.Head)))
        {
            if (quality == HandoffQuality.Valid) quality = HandoffQuality.Partial;
            findings.Add(new("DONE_WITHOUT_EVIDENCE", "DONE is not sufficient without changed/head evidence."));
        }

        return new(quality, handoff, findings);
    }

    private static bool ClaimsPassed(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        (value.Contains("PASS", StringComparison.OrdinalIgnoreCase) ||
         value.Contains("GREEN", StringComparison.OrdinalIgnoreCase) ||
         value.Contains("SUCCESS", StringComparison.OrdinalIgnoreCase));

    private static bool IsGreen(GitHubCheckSummary? checks) =>
        checks is not null &&
        (string.Equals(checks.CombinedState, "success", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(checks.CombinedState, "green", StringComparison.OrdinalIgnoreCase));
}

public sealed record TaskReviewResult(TaskId TaskId, WorkerSlotId SlotId, HandoffQuality Quality, string Status, string? Head, int? PullRequest, IReadOnlyList<HandoffFinding> Findings);
public sealed record ConsolidatedManagerReviewPacket(
    string Project,
    WaveId WaveId,
    IReadOnlyList<TaskReviewResult> TaskResults,
    IReadOnlyList<GitHubPullRequestSnapshot> PullRequests,
    string LiveHead,
    string CiState,
    IReadOnlyList<EvidenceEnvelope> Evidence,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<CompletionGate> CompletionInputs,
    LoopAssessment LoopSignals,
    IReadOnlyList<AttentionRequest> AttentionItems,
    OrchestrationDecision RecommendedNextDecision,
    DateTimeOffset CapturedAt);

public sealed class ManagerReviewPacketBuilder
{
    public ConsolidatedManagerReviewPacket Build(
        string project,
        WaveId waveId,
        IReadOnlyList<(ManagerTaskProposal Expected, WorkerSlotId Slot, HandoffAssessment Assessment)> handoffs,
        ProjectBaselineSnapshot live,
        IReadOnlyList<EvidenceEnvelope> evidence,
        IReadOnlyList<CompletionGate> completionInputs,
        LoopAssessment loop,
        IReadOnlyList<AttentionRequest> attention,
        OrchestrationDecision recommendation)
    {
        var results = handoffs
            .Select(x => new TaskReviewResult(
                x.Assessment.Handoff?.TaskId ?? x.Expected.Task.Id,
                x.Slot,
                x.Assessment.Quality,
                x.Assessment.Handoff?.Status ?? "MISSING_OR_PARTIAL",
                x.Assessment.Handoff?.Head,
                x.Assessment.Handoff?.PullRequest,
                x.Assessment.Findings))
            .ToArray();

        return new(
            project,
            waveId,
            results,
            live.RelevantPullRequests,
            live.DefaultHeadSha,
            live.CiState,
            evidence,
            live.KnownBlockers,
            completionInputs,
            loop,
            attention,
            recommendation,
            live.CapturedAt);
    }
}

public sealed class ManagerSanityChecker
{
    public IReadOnlyList<ManagerPlanFinding> Check(
        StructuredManagerPlan plan,
        OrchestrationWaveValidation validation,
        ProjectBaselineSnapshot live,
        ProjectCompletionMode completionMode,
        VerifiedCompletion verified,
        LoopAssessment loop)
    {
        var findings = new List<ManagerPlanFinding>(validation.Findings);
        if (string.Equals(plan.ProjectDecision, "DONE", StringComparison.OrdinalIgnoreCase) &&
            (verified.Percent < 100m || live.KnownBlockers.Count > 0))
            findings.Add(new("UNSUPPORTED_COMPLETION", "Manager claims DONE without 100% verified completion and cleared blockers.", PlanFindingSeverity.Block));

        if (completionMode == ProjectCompletionMode.ClosureMode && plan.Tasks.Any(x => x.FeatureExpansion))
            findings.Add(new("CLOSURE_MODE_EXPANSION", "Manager proposed feature expansion while project is in Closure Mode.", PlanFindingSeverity.Block));

        foreach (var signal in loop.Signals)
        {
            if (signal.Type == LoopSignalType.RepeatedManagerReassignment)
                findings.Add(new("REPEATED_MANAGER_ASSIGNMENT", $"Manager reassignment repeated: {signal.Fingerprint}.", PlanFindingSeverity.Block));
            if (signal.Type == LoopSignalType.RepeatedBlocker)
                findings.Add(new("REPEATED_BLOCKER_IGNORED", $"Repeated blocker remains unresolved: {signal.Fingerprint}.", PlanFindingSeverity.Block));
            if (signal.Type == LoopSignalType.RepeatedTaskFingerprint)
                findings.Add(new("REPEATED_TASK", $"Manager repeated task fingerprint: {signal.Fingerprint}.", PlanFindingSeverity.Block));
        }

        return findings
            .GroupBy(x => (x.Code, x.TaskId, x.OtherTaskId))
            .Select(x => x.First())
            .ToArray();
    }
}

public sealed class LoopDecisionEngine
{
    public OrchestrationDecision Decide(
        LoopAssessment assessment,
        ProjectCompletionMode completionMode,
        bool externalBlocker,
        bool requiresSequentialization = false)
    {
        if (externalBlocker) return OrchestrationDecision.AttentionRequired;
        if (assessment.Level is LoopGuardLevel.LoopDetected or LoopGuardLevel.AutoStopped)
            return OrchestrationDecision.StalledAutoStopped;
        if (completionMode == ProjectCompletionMode.ClosureMode && assessment.Level != LoopGuardLevel.Normal)
            return OrchestrationDecision.ClosureRepair;
        if (requiresSequentialization) return OrchestrationDecision.Sequentialize;
        if (assessment.Level == LoopGuardLevel.Stagnating) return OrchestrationDecision.Replan;
        if (assessment.Level == LoopGuardLevel.Watch) return OrchestrationDecision.ReassignOnce;
        return OrchestrationDecision.Continue;
    }
}

public sealed record OrchestrationRecoverySnapshot(
    ProjectRun ProjectRun,
    Wave? CurrentWave,
    IReadOnlyList<WorkerTask> Tasks,
    IReadOnlyDictionary<TaskId, WorkerSlotId> Assignments,
    IReadOnlyList<Dispatch> Dispatches,
    ConsolidatedManagerReviewPacket? ManagerReview,
    OrchestrationPhase Phase,
    DateTimeOffset SavedAt);

public interface IOrchestrationStateStore
{
    Task SaveAsync(OrchestrationRecoverySnapshot snapshot, CancellationToken cancellationToken = default);
    Task<OrchestrationRecoverySnapshot?> LoadAsync(ProjectRunId projectRunId, CancellationToken cancellationToken = default);
}

public sealed class ProjectRunCoordinator
{
    private readonly ProjectRunStateMachine _states = new();
    private readonly IOrchestrationStateStore _store;

    public ProjectRunCoordinator(IOrchestrationStateStore store) => _store = store;

    public async Task<OrchestrationRecoverySnapshot> InitializeAsync(ProjectId projectId, CancellationToken cancellationToken = default)
    {
        var run = new ProjectRun(
            ProjectRunId.New(),
            projectId,
            ProjectRunState.Initializing,
            DateTimeOffset.UtcNow,
            new ManagerEstimate(0),
            new VerifiedCompletion(0),
            ProjectCompletionMode.Active);
        var snapshot = new OrchestrationRecoverySnapshot(run, null, [], new Dictionary<TaskId, WorkerSlotId>(), [], null, OrchestrationPhase.Initializing, DateTimeOffset.UtcNow);
        await _store.SaveAsync(snapshot, cancellationToken);
        return snapshot;
    }

    public async Task<OrchestrationRecoverySnapshot> EnterManagerPlanningAsync(OrchestrationRecoverySnapshot snapshot, CancellationToken cancellationToken = default)
    {
        var state = _states.Transition(snapshot.ProjectRun.State, ProjectRunState.ManagerPlanning);
        var updated = snapshot with
        {
            ProjectRun = snapshot.ProjectRun with { State = state },
            Phase = OrchestrationPhase.ManagerPlanning,
            SavedAt = DateTimeOffset.UtcNow
        };
        await _store.SaveAsync(updated, cancellationToken);
        return updated;
    }

    public async Task<OrchestrationRecoverySnapshot> AcceptWaveAsync(
        OrchestrationRecoverySnapshot snapshot,
        StructuredManagerPlan plan,
        OrchestrationWaveValidation validation,
        IReadOnlyDictionary<TaskId, WorkerSlotId> assignments,
        CancellationToken cancellationToken = default)
    {
        if (!validation.IsValid) throw new InvalidOperationException("Cannot accept an invalid Manager wave.");
        var wave = new Wave(WaveId.New(), snapshot.ProjectRun.Id, (snapshot.CurrentWave?.Sequence ?? 0) + 1, WaveState.Ready, plan.Tasks.Select(x => x.Task.Id).ToArray(), DateTimeOffset.UtcNow);
        var state = _states.Transition(snapshot.ProjectRun.State, ProjectRunState.WaveReady);
        var updated = snapshot with
        {
            ProjectRun = snapshot.ProjectRun with { State = state, ManagerEstimate = plan.ManagerEstimate },
            CurrentWave = wave,
            Tasks = plan.Tasks.Select(x => x.Task).ToArray(),
            Assignments = new Dictionary<TaskId, WorkerSlotId>(assignments),
            Phase = OrchestrationPhase.WaveValidation,
            SavedAt = DateTimeOffset.UtcNow
        };
        await _store.SaveAsync(updated, cancellationToken);
        return updated;
    }

    public async Task<OrchestrationRecoverySnapshot> SetPhaseAsync(
        OrchestrationRecoverySnapshot snapshot,
        OrchestrationPhase phase,
        ProjectRunState targetState,
        CancellationToken cancellationToken = default)
    {
        var state = _states.Transition(snapshot.ProjectRun.State, targetState);
        var updated = snapshot with
        {
            ProjectRun = snapshot.ProjectRun with { State = state },
            Phase = phase,
            SavedAt = DateTimeOffset.UtcNow
        };
        await _store.SaveAsync(updated, cancellationToken);
        return updated;
    }

    public async Task<OrchestrationRecoverySnapshot> ApplyCompletionAsync(
        OrchestrationRecoverySnapshot snapshot,
        CompletionEvaluation completion,
        CancellationToken cancellationToken = default)
    {
        var targetState = completion.Mode switch
        {
            ProjectCompletionMode.ClosureMode => ProjectRunState.ClosureMode,
            ProjectCompletionMode.VerifiedComplete => ProjectRunState.VerifiedComplete,
            ProjectCompletionMode.Blocked => ProjectRunState.BlockedExternal,
            _ => snapshot.ProjectRun.State
        };

        var state = targetState == snapshot.ProjectRun.State ? targetState : _states.Transition(snapshot.ProjectRun.State, targetState);
        var phase = completion.Mode switch
        {
            ProjectCompletionMode.ClosureMode => OrchestrationPhase.ClosureMode,
            ProjectCompletionMode.VerifiedComplete => OrchestrationPhase.VerifiedComplete,
            ProjectCompletionMode.Blocked => OrchestrationPhase.BlockedExternal,
            _ => snapshot.Phase
        };
        var updated = snapshot with
        {
            ProjectRun = snapshot.ProjectRun with { State = state, VerifiedCompletion = completion.Verified, CompletionMode = completion.Mode },
            Phase = phase,
            SavedAt = DateTimeOffset.UtcNow
        };
        await _store.SaveAsync(updated, cancellationToken);
        return updated;
    }

    public Task<OrchestrationRecoverySnapshot?> RestoreAsync(ProjectRunId projectRunId, CancellationToken cancellationToken = default) =>
        _store.LoadAsync(projectRunId, cancellationToken);
}

public static class AttentionPolicy
{
    private static readonly HashSet<string> HumanGateCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "LOGIN", "CAPTCHA", "CHALLENGE", "MISSING_EXTERNAL_AUTHORITY", "DESTRUCTIVE_APPROVAL", "BUSINESS_DECISION", "EXTERNAL_BLOCKER"
    };

    public static bool RequiresHumanAttention(string category) => HumanGateCategories.Contains(category);
}
