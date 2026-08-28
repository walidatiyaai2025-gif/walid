using System.Security.Cryptography;
using System.Text;
using PCCExecutive.Domain;

namespace PCCExecutive.Application;

public enum AutopilotState
{
    OFF,
    PAUSED,
    AUTOMATIC_STAGED,
    RECOVERING,
    WAITING_FOR_DEPENDENCY,
    WAITING_FOR_EVIDENCE,
    ATTENTION_REQUIRED,
    CLOSURE_MODE,
    STALLED_AUTO_STOPPED,
    VERIFIED_COMPLETE,
    BLOCKED_EXTERNAL
}

public sealed record AutopilotTransitionRecord(
    DateTimeOffset Timestamp,
    ProjectRunId ProjectRunId,
    AutopilotState From,
    AutopilotState To,
    string Reason,
    IReadOnlyList<string> Evidence);

public sealed class AutopilotStateMachine
{
    private static readonly IReadOnlyDictionary<AutopilotState, IReadOnlySet<AutopilotState>> Allowed =
        new Dictionary<AutopilotState, IReadOnlySet<AutopilotState>>
        {
            [AutopilotState.OFF] = Set(AutopilotState.PAUSED, AutopilotState.AUTOMATIC_STAGED),
            [AutopilotState.PAUSED] = Set(AutopilotState.OFF, AutopilotState.AUTOMATIC_STAGED, AutopilotState.RECOVERING, AutopilotState.ATTENTION_REQUIRED, AutopilotState.STALLED_AUTO_STOPPED, AutopilotState.BLOCKED_EXTERNAL),
            [AutopilotState.AUTOMATIC_STAGED] = Set(AutopilotState.OFF, AutopilotState.PAUSED, AutopilotState.RECOVERING, AutopilotState.WAITING_FOR_DEPENDENCY, AutopilotState.WAITING_FOR_EVIDENCE, AutopilotState.ATTENTION_REQUIRED, AutopilotState.CLOSURE_MODE, AutopilotState.STALLED_AUTO_STOPPED, AutopilotState.VERIFIED_COMPLETE, AutopilotState.BLOCKED_EXTERNAL),
            [AutopilotState.RECOVERING] = Set(AutopilotState.PAUSED, AutopilotState.AUTOMATIC_STAGED, AutopilotState.WAITING_FOR_DEPENDENCY, AutopilotState.WAITING_FOR_EVIDENCE, AutopilotState.ATTENTION_REQUIRED, AutopilotState.CLOSURE_MODE, AutopilotState.STALLED_AUTO_STOPPED, AutopilotState.BLOCKED_EXTERNAL),
            [AutopilotState.WAITING_FOR_DEPENDENCY] = Set(AutopilotState.PAUSED, AutopilotState.AUTOMATIC_STAGED, AutopilotState.RECOVERING, AutopilotState.ATTENTION_REQUIRED, AutopilotState.CLOSURE_MODE, AutopilotState.STALLED_AUTO_STOPPED, AutopilotState.BLOCKED_EXTERNAL),
            [AutopilotState.WAITING_FOR_EVIDENCE] = Set(AutopilotState.PAUSED, AutopilotState.AUTOMATIC_STAGED, AutopilotState.RECOVERING, AutopilotState.ATTENTION_REQUIRED, AutopilotState.CLOSURE_MODE, AutopilotState.STALLED_AUTO_STOPPED, AutopilotState.BLOCKED_EXTERNAL),
            [AutopilotState.ATTENTION_REQUIRED] = Set(AutopilotState.PAUSED, AutopilotState.AUTOMATIC_STAGED, AutopilotState.RECOVERING, AutopilotState.CLOSURE_MODE, AutopilotState.STALLED_AUTO_STOPPED, AutopilotState.BLOCKED_EXTERNAL),
            [AutopilotState.CLOSURE_MODE] = Set(AutopilotState.PAUSED, AutopilotState.RECOVERING, AutopilotState.WAITING_FOR_DEPENDENCY, AutopilotState.WAITING_FOR_EVIDENCE, AutopilotState.ATTENTION_REQUIRED, AutopilotState.STALLED_AUTO_STOPPED, AutopilotState.VERIFIED_COMPLETE, AutopilotState.BLOCKED_EXTERNAL),
            [AutopilotState.STALLED_AUTO_STOPPED] = Set(),
            [AutopilotState.VERIFIED_COMPLETE] = Set(),
            [AutopilotState.BLOCKED_EXTERNAL] = Set()
        };

    public AutopilotTransitionRecord Transition(
        ProjectRunId projectRunId,
        AutopilotState current,
        AutopilotState target,
        string reason,
        IEnumerable<string>? evidence = null,
        DateTimeOffset? now = null)
    {
        if (current != target && (!Allowed.TryGetValue(current, out var allowed) || !allowed.Contains(target)))
            throw new InvalidOperationException($"Illegal Autopilot transition: {current} -> {target}.");
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("Autopilot transition reason is required.", nameof(reason));

        return new(now ?? DateTimeOffset.UtcNow, projectRunId, current, target, reason.Trim(), (evidence ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray());
    }

    private static IReadOnlySet<AutopilotState> Set(params AutopilotState[] states) => states.ToHashSet();
}

public enum AttentionCategory
{
    LOGIN_REQUIRED,
    CAPTCHA_OR_CHALLENGE,
    MISSING_CREDENTIAL,
    DESTRUCTIVE_APPROVAL,
    EXTERNAL_AUTHORITY_REQUIRED,
    BUSINESS_DECISION_REQUIRED,
    UNRESOLVED_SECURITY_DECISION,
    EXTERNAL_BLOCKER
}

public enum AttentionLifecycleState
{
    OPEN,
    ACKNOWLEDGED,
    RESOLVED,
    SUPERSEDED,
    AUTO_RESOLVED
}

public enum OperationalCondition
{
    TEMP_ERROR,
    SLOW_SESSION,
    NETWORK_OFFLINE,
    RATE_LIMITED,
    WORKER_COMPLETED,
    WORKER_SLOT_REUSABLE,
    DEPENDENCY_RELEASED,
    EVIDENCE_STALE,
    CONVERSATION_ROLLOVER,
    APPLICATION_RESTART_RECOVERY,
    LOGIN_REQUIRED,
    CAPTCHA_OR_CHALLENGE,
    MISSING_CREDENTIAL,
    DESTRUCTIVE_APPROVAL,
    EXTERNAL_AUTHORITY_REQUIRED,
    BUSINESS_DECISION_REQUIRED,
    UNRESOLVED_SECURITY_DECISION,
    EXTERNAL_BLOCKER,
    SUBMITTED_UNKNOWN,
    BROWSER_ADAPTER_UNCERTAIN
}

public sealed record AttentionClassification(
    bool RequiresAttention,
    AttentionCategory? Category,
    string AutomaticAction,
    AutopilotState SuggestedState);

public sealed class AttentionClassifier
{
    public AttentionClassification Classify(OperationalCondition condition, bool prolongedOrNonRecoverable = false) => condition switch
    {
        OperationalCondition.LOGIN_REQUIRED => Attention(AttentionCategory.LOGIN_REQUIRED, "Open the owned session for sign-in."),
        OperationalCondition.CAPTCHA_OR_CHALLENGE => Attention(AttentionCategory.CAPTCHA_OR_CHALLENGE, "Open the owned session for the account challenge."),
        OperationalCondition.MISSING_CREDENTIAL => Attention(AttentionCategory.MISSING_CREDENTIAL, "Request the missing external credential."),
        OperationalCondition.DESTRUCTIVE_APPROVAL => Attention(AttentionCategory.DESTRUCTIVE_APPROVAL, "Request explicit destructive-action approval."),
        OperationalCondition.EXTERNAL_AUTHORITY_REQUIRED => Attention(AttentionCategory.EXTERNAL_AUTHORITY_REQUIRED, "Request the missing external authority."),
        OperationalCondition.BUSINESS_DECISION_REQUIRED => Attention(AttentionCategory.BUSINESS_DECISION_REQUIRED, "Request the product/business decision."),
        OperationalCondition.UNRESOLVED_SECURITY_DECISION => Attention(AttentionCategory.UNRESOLVED_SECURITY_DECISION, "Request an explicit security decision."),
        OperationalCondition.EXTERNAL_BLOCKER => Attention(AttentionCategory.EXTERNAL_BLOCKER, "Surface the external blocker and required owner action."),
        OperationalCondition.NETWORK_OFFLINE when prolongedOrNonRecoverable => Attention(AttentionCategory.EXTERNAL_BLOCKER, "Network remains unavailable beyond automatic recovery policy."),
        OperationalCondition.RATE_LIMITED => Automatic("Pause globally, honor cooldown, then resume gradually.", AutopilotState.PAUSED),
        OperationalCondition.SUBMITTED_UNKNOWN => Automatic("Reconcile the existing dispatch before any retry.", AutopilotState.WAITING_FOR_EVIDENCE),
        OperationalCondition.BROWSER_ADAPTER_UNCERTAIN when prolongedOrNonRecoverable => Attention(AttentionCategory.UNRESOLVED_SECURITY_DECISION, "Adapter uncertainty persists; do not send until explicitly resolved."),
        OperationalCondition.BROWSER_ADAPTER_UNCERTAIN => Automatic("Do not send; refresh/recover semantic browser evidence.", AutopilotState.RECOVERING),
        OperationalCondition.EVIDENCE_STALE => Automatic("Refresh live evidence before deciding.", AutopilotState.WAITING_FOR_EVIDENCE),
        OperationalCondition.NETWORK_OFFLINE => Automatic("Wait for network recovery and retry safely.", AutopilotState.RECOVERING),
        OperationalCondition.TEMP_ERROR => Automatic("Retry through bounded safe recovery policy.", AutopilotState.RECOVERING),
        OperationalCondition.SLOW_SESSION => Automatic("Continue monitoring without interrupting healthy Workers.", AutopilotState.AUTOMATIC_STAGED),
        OperationalCondition.CONVERSATION_ROLLOVER => Automatic("Checkpoint and rotate conversation transactionally.", AutopilotState.RECOVERING),
        OperationalCondition.APPLICATION_RESTART_RECOVERY => Automatic("Restore durable orchestration state and reconcile live state.", AutopilotState.RECOVERING),
        OperationalCondition.DEPENDENCY_RELEASED => Automatic("Release newly-ready dependent work.", AutopilotState.AUTOMATIC_STAGED),
        OperationalCondition.WORKER_COMPLETED => Automatic("Accept validated handoff and continue wave reconciliation.", AutopilotState.AUTOMATIC_STAGED),
        OperationalCondition.WORKER_SLOT_REUSABLE => Automatic("Release the logical Worker slot for future work.", AutopilotState.AUTOMATIC_STAGED),
        _ => Automatic("Continue deterministic autonomous operation.", AutopilotState.AUTOMATIC_STAGED)
    };

    private static AttentionClassification Attention(AttentionCategory category, string action) => new(true, category, action, AutopilotState.ATTENTION_REQUIRED);
    private static AttentionClassification Automatic(string action, AutopilotState state) => new(false, null, action, state);
}

public sealed record AttentionObservation(
    ProjectRunId ProjectRunId,
    AttentionCategory Category,
    string Reason,
    string RequiredAction,
    string? Resource,
    TaskId? TaskId,
    LogicalAgentId? LogicalSessionId,
    string? BlockerFingerprint,
    string? OpenTarget,
    bool RequiresIrreversibleApproval,
    DateTimeOffset ObservedAt);

public sealed record AttentionLifecycleItem(
    AttentionRequestId Id,
    ProjectRunId ProjectRunId,
    string Fingerprint,
    AttentionCategory Category,
    AttentionLifecycleState State,
    string Reason,
    string RequiredAction,
    string? Resource,
    TaskId? TaskId,
    LogicalAgentId? LogicalSessionId,
    string? BlockerFingerprint,
    string? OpenTarget,
    bool RequiresIrreversibleApproval,
    int ObservationCount,
    DateTimeOffset FirstObservedAt,
    DateTimeOffset LastObservedAt,
    DateTimeOffset? ResolvedAt);

public static class AttentionFingerprint
{
    public static string Create(AttentionObservation observation)
    {
        var material = string.Join("|",
            observation.ProjectRunId,
            observation.Category,
            Normalize(observation.Resource),
            observation.TaskId?.ToString() ?? string.Empty,
            observation.LogicalSessionId?.ToString() ?? string.Empty,
            Normalize(observation.BlockerFingerprint));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }

    private static string Normalize(string? value) => value?.Trim().ToLowerInvariant() ?? string.Empty;
}

public interface IAttentionLifecycleStore
{
    Task<AttentionLifecycleItem?> FindActiveAsync(ProjectRunId projectRunId, string fingerprint, CancellationToken cancellationToken = default);
    Task UpsertAsync(AttentionLifecycleItem item, CancellationToken cancellationToken = default);
}

public sealed class AttentionLifecycleCoordinator
{
    private readonly IAttentionLifecycleStore _store;

    public AttentionLifecycleCoordinator(IAttentionLifecycleStore store) => _store = store;

    public async Task<AttentionLifecycleItem> ObserveAsync(AttentionObservation observation, CancellationToken cancellationToken = default)
    {
        var fingerprint = AttentionFingerprint.Create(observation);
        var existing = await _store.FindActiveAsync(observation.ProjectRunId, fingerprint, cancellationToken);
        var item = existing is null
            ? new AttentionLifecycleItem(
                AttentionRequestId.New(), observation.ProjectRunId, fingerprint, observation.Category,
                AttentionLifecycleState.OPEN, observation.Reason, observation.RequiredAction, observation.Resource,
                observation.TaskId, observation.LogicalSessionId, observation.BlockerFingerprint, observation.OpenTarget,
                observation.RequiresIrreversibleApproval, 1, observation.ObservedAt, observation.ObservedAt, null)
            : existing with
            {
                Reason = observation.Reason,
                RequiredAction = observation.RequiredAction,
                OpenTarget = observation.OpenTarget,
                ObservationCount = existing.ObservationCount + 1,
                LastObservedAt = observation.ObservedAt
            };
        await _store.UpsertAsync(item, cancellationToken);
        return item;
    }

    public async Task<AttentionLifecycleItem> AcknowledgeAsync(AttentionLifecycleItem item, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        EnsureActive(item);
        var updated = item with { State = AttentionLifecycleState.ACKNOWLEDGED, LastObservedAt = now };
        await _store.UpsertAsync(updated, cancellationToken);
        return updated;
    }

    public async Task<AttentionLifecycleItem> ResolveAsync(AttentionLifecycleItem item, DateTimeOffset now, bool automatic, CancellationToken cancellationToken = default)
    {
        EnsureActive(item);
        var updated = item with
        {
            State = automatic ? AttentionLifecycleState.AUTO_RESOLVED : AttentionLifecycleState.RESOLVED,
            ResolvedAt = now,
            LastObservedAt = now
        };
        await _store.UpsertAsync(updated, cancellationToken);
        return updated;
    }

    public Task<AttentionLifecycleItem> AutoResolveLoginAsync(AttentionLifecycleItem item, ProviderHealth health, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        if (item.Category != AttentionCategory.LOGIN_REQUIRED)
            throw new InvalidOperationException("Only LOGIN_REQUIRED is eligible for login-health auto-resolution.");
        if (!health.IsAvailable || !health.IsAuthenticated || health.RequiresAttention)
            throw new InvalidOperationException("Provider health is not authenticated-ready.");
        return ResolveAsync(item, now, true, cancellationToken);
    }

    private static void EnsureActive(AttentionLifecycleItem item)
    {
        if (item.State is AttentionLifecycleState.RESOLVED or AttentionLifecycleState.AUTO_RESOLVED or AttentionLifecycleState.SUPERSEDED)
            throw new InvalidOperationException("Attention item is already terminal.");
    }
}

public enum EvidenceQuality
{
    STRONG,
    ACCEPTABLE,
    WEAK,
    STALE,
    CONTRADICTED,
    MISSING
}

public enum EvidenceCheckResult
{
    NOT_REQUIRED,
    NOT_EXECUTED,
    PASSED,
    FAILED
}

public sealed record EvidenceQualityInput(
    string? Source,
    string? ExactSourceSha,
    DateTimeOffset? CapturedAt,
    TimeSpan MaximumAge,
    EvidenceCheckResult Ci,
    EvidenceCheckResult Tests,
    EvidenceCheckResult RuntimeVerification,
    EvidenceCheckResult ArtifactProvenance,
    string? BranchIdentity,
    string? ExpectedBranchIdentity,
    string? PullRequestIdentity,
    string? ExpectedPullRequestIdentity,
    decimal Confidence,
    bool ExactSourceShaRequired,
    bool Required,
    IReadOnlyList<string> Contradictions);

public sealed record EvidenceQualityAssessment(EvidenceQuality Quality, IReadOnlyList<string> Reasons)
{
    public bool CanSatisfyGate => Quality is EvidenceQuality.STRONG or EvidenceQuality.ACCEPTABLE;
}

public sealed class EvidenceQualityEvaluator
{
    public EvidenceQualityAssessment Evaluate(EvidenceQualityInput input, DateTimeOffset now)
    {
        var reasons = new List<string>();
        if (input.Contradictions.Count > 0)
            return new(EvidenceQuality.CONTRADICTED, input.Contradictions.ToArray());
        if (string.IsNullOrWhiteSpace(input.Source))
            return new(EvidenceQuality.MISSING, ["Evidence source is missing."]);
        if (input.CapturedAt is null)
            return new(EvidenceQuality.MISSING, ["Evidence capture timestamp is missing."]);
        if (input.MaximumAge > TimeSpan.Zero && now - input.CapturedAt.Value > input.MaximumAge)
            return new(EvidenceQuality.STALE, ["Evidence is outside the allowed freshness window."]);
        if (input.ExactSourceShaRequired && !IsExactSha(input.ExactSourceSha))
            return new(EvidenceQuality.WEAK, ["Exact source SHA is required but not proven."]);
        if (!Matches(input.BranchIdentity, input.ExpectedBranchIdentity))
            return new(EvidenceQuality.CONTRADICTED, ["Branch identity contradicts expected evidence."]);
        if (!Matches(input.PullRequestIdentity, input.ExpectedPullRequestIdentity))
            return new(EvidenceQuality.CONTRADICTED, ["Pull request identity contradicts expected evidence."]);

        foreach (var (name, value) in new[]
        {
            ("CI", input.Ci), ("TESTS", input.Tests), ("RUNTIME", input.RuntimeVerification), ("ARTIFACT", input.ArtifactProvenance)
        })
        {
            if (value == EvidenceCheckResult.FAILED)
                return new(EvidenceQuality.CONTRADICTED, [$"{name} evidence reports failure."]);
            if (input.Required && value == EvidenceCheckResult.NOT_EXECUTED)
                reasons.Add($"{name} evidence is not executed.");
        }

        if (reasons.Count > 0)
            return new(EvidenceQuality.WEAK, reasons);
        if (input.Confidence >= 0.85m && IsExactSha(input.ExactSourceSha) && PositiveSignals(input) >= 2)
            return new(EvidenceQuality.STRONG, ["Fresh exact-head evidence has multiple positive verification signals."]);
        if (input.Confidence >= 0.60m && PositiveSignals(input) >= 1)
            return new(EvidenceQuality.ACCEPTABLE, ["Fresh evidence is sufficient for a non-strong gate decision."]);
        return new(EvidenceQuality.WEAK, ["Evidence confidence or corroboration is insufficient."]);
    }

    private static int PositiveSignals(EvidenceQualityInput input) =>
        new[] { input.Ci, input.Tests, input.RuntimeVerification, input.ArtifactProvenance }.Count(x => x == EvidenceCheckResult.PASSED);

    private static bool IsExactSha(string? value) =>
        value is { Length: 40 } && value.All(Uri.IsHexDigit);

    private static bool Matches(string? actual, string? expected) =>
        string.IsNullOrWhiteSpace(expected) || string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
}

public enum CompletionGateFamily
{
    IMPLEMENTATION,
    RUNTIME,
    TESTS,
    CI,
    UI,
    PERSISTENCE,
    BROWSER,
    ORCHESTRATION,
    RECOVERY,
    SECURITY,
    INSTALLER,
    UPDATE,
    E2E,
    PACKAGING,
    RELEASE
}

public enum ClosurePriority
{
    P0_VERIFICATION_BLOCKER,
    P1_RELEASE_BLOCKER,
    P2_POLISH
}

public sealed record PolicyCompletionGate(
    CompletionGateFamily Family,
    CompletionGate Gate,
    EvidenceQualityAssessment EvidenceQuality,
    ClosurePriority Priority);

public sealed record PolicyBlocker(
    string Fingerprint,
    BlockerCategory Category,
    ClosurePriority Priority,
    string Description,
    bool IsResolved);

public sealed record CompletionControlEvaluation(
    ManagerEstimate ManagerEstimate,
    VerifiedCompletion VerifiedCompletion,
    ProjectCompletionMode Mode,
    IReadOnlyList<string> BlockingGates,
    IReadOnlyList<PolicyBlocker> BlockingItems,
    IReadOnlyList<CompletionGateFamily> MissingRequiredFamilies);

public sealed class CompletionGateController
{
    private readonly CompletionEngine _engine;

    public CompletionGateController(CompletionEngine? engine = null) => _engine = engine ?? new CompletionEngine();

    public CompletionControlEvaluation Evaluate(
        ManagerEstimate managerEstimate,
        IReadOnlyList<PolicyCompletionGate> gates,
        IReadOnlyList<PolicyBlocker> blockers,
        IReadOnlySet<CompletionGateFamily>? requiredFamilies = null)
    {
        var required = requiredFamilies ?? gates.Where(x => x.Gate.Mandatory).Select(x => x.Family).ToHashSet();
        var missing = required.Where(family => !gates.Any(x => x.Family == family)).OrderBy(x => x).ToArray();
        var effectiveGates = new List<CompletionGate>();
        foreach (var gate in gates)
        {
            var state = gate.Gate.State;
            if (state == GateState.Pass && !gate.EvidenceQuality.CanSatisfyGate)
                state = GateState.Unknown;
            effectiveGates.Add(gate.Gate with { State = state });
        }
        foreach (var family in missing)
            effectiveGates.Add(new CompletionGate($"MISSING_{family}", true, 0m, GateState.Unknown, "Required completion family is missing."));

        var unresolved = blockers.Where(x => !x.IsResolved).ToArray();
        var domainBlockers = unresolved.Select(x => new Blocker(
            x.Fingerprint,
            x.Category.ToString(),
            x.Description,
            x.Category is BlockerCategory.EXTERNAL_AUTHORITY or BlockerCategory.EXTERNAL_SERVICE,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch)).ToArray();

        var evaluated = _engine.Evaluate(effectiveGates, domainBlockers);
        var verified = evaluated.Verified;
        var mode = evaluated.Mode;
        if (unresolved.Any(x => x.Priority is ClosurePriority.P0_VERIFICATION_BLOCKER or ClosurePriority.P1_RELEASE_BLOCKER) && verified.Percent >= 100m)
        {
            verified = new VerifiedCompletion(99.99m);
            mode = ProjectCompletionMode.ClosureMode;
        }
        if (missing.Length > 0 && verified.Percent >= 99m)
        {
            verified = new VerifiedCompletion(98.99m);
            mode = ProjectCompletionMode.Active;
        }

        return new(managerEstimate, verified, mode, evaluated.BlockingGateNames, unresolved, missing);
    }
}

public enum ClosureWorkKind
{
    FAILED_TEST,
    INTEGRATION_REPAIR,
    SECURITY_DEFECT,
    VISUAL_DEFECT,
    PACKAGING_DEFECT,
    INSTALLER_DEFECT,
    RELEASE_EVIDENCE,
    CRITICAL_ACCEPTANCE_BUG,
    NEW_FEATURE,
    REFACTOR_FOR_STYLE,
    OPTIONAL_ENHANCEMENT,
    SCOPE_EXPANSION
}

public sealed record ClosureWorkDecision(bool Allowed, ClosurePriority Priority, string Reason);

public sealed class ClosureWorkPolicy
{
    public ClosureWorkDecision Evaluate(ClosureWorkKind kind, ClosurePriority priority) => kind switch
    {
        ClosureWorkKind.FAILED_TEST or
        ClosureWorkKind.INTEGRATION_REPAIR or
        ClosureWorkKind.SECURITY_DEFECT or
        ClosureWorkKind.VISUAL_DEFECT or
        ClosureWorkKind.PACKAGING_DEFECT or
        ClosureWorkKind.INSTALLER_DEFECT or
        ClosureWorkKind.RELEASE_EVIDENCE or
        ClosureWorkKind.CRITICAL_ACCEPTANCE_BUG => new(true, priority, "Work directly closes a verified release/acceptance gap."),
        _ => new(false, priority, "Closure Mode rejects feature expansion, style-only refactors, optional enhancements and unrelated scope expansion.")
    };

    public IReadOnlyList<(ClosureWorkKind Kind, ClosurePriority Priority)> Prioritize(IEnumerable<(ClosureWorkKind Kind, ClosurePriority Priority)> work) =>
        work.OrderBy(x => x.Priority).ThenBy(x => x.Kind).ToArray();
}

public sealed record StagnationObservation(
    DateTimeOffset ObservedAt,
    string? SourceHead,
    IReadOnlySet<string> TaskFingerprints,
    IReadOnlySet<string> BlockerFingerprints,
    IReadOnlySet<string> FailedTestFingerprints,
    IReadOnlySet<string> PullRequestStates,
    IReadOnlySet<string> EvidenceFingerprints,
    IReadOnlySet<string> ManagerRecommendations,
    ManagerEstimate ManagerEstimate,
    VerifiedCompletion VerifiedCompletion);

public sealed record StagnationPolicyOptions(int ObservationWindow = 3, decimal NegligibleVerifiedDelta = 0.25m, int AutoStopSignalThreshold = 3);

public enum StagnationAction
{
    CONTINUE,
    RETRY_SAFE,
    REFRESH_EVIDENCE,
    REPLAN,
    SEQUENTIALIZE,
    REASSIGN_ONCE,
    CHANGE_STRATEGY,
    ENTER_CLOSURE_REPAIR,
    STALLED_AUTO_STOPPED,
    ATTENTION_REQUIRED
}

public sealed record StagnationAssessment(
    bool IsStagnating,
    IReadOnlyList<LoopSignal> Signals,
    StagnationAction Action,
    decimal VerifiedCompletionDelta,
    decimal ManagerEstimateDelta);

public sealed class StagnationEngine
{
    public StagnationAssessment Analyze(IReadOnlyList<StagnationObservation> observations, StagnationPolicyOptions? options = null)
    {
        var policy = options ?? new StagnationPolicyOptions();
        if (policy.ObservationWindow < 2) throw new ArgumentOutOfRangeException(nameof(options));
        if (observations.Count < policy.ObservationWindow)
            return new(false, [], StagnationAction.CONTINUE, 0m, 0m);

        var window = observations.TakeLast(policy.ObservationWindow).ToArray();
        var verifiedDelta = window[^1].VerifiedCompletion.Percent - window[0].VerifiedCompletion.Percent;
        var managerDelta = window[^1].ManagerEstimate.Percent - window[0].ManagerEstimate.Percent;
        if (verifiedDelta > policy.NegligibleVerifiedDelta)
            return new(false, [], StagnationAction.CONTINUE, verifiedDelta, managerDelta);

        var signals = new List<LoopSignal>();
        AddCommon(signals, LoopSignalType.RepeatedTaskFingerprint, window.Select(x => x.TaskFingerprints), policy.ObservationWindow);
        AddCommon(signals, LoopSignalType.RepeatedBlocker, window.Select(x => x.BlockerFingerprints), policy.ObservationWindow);
        AddCommon(signals, LoopSignalType.RepeatedFailedCheck, window.Select(x => x.FailedTestFingerprints), policy.ObservationWindow);
        AddCommon(signals, LoopSignalType.UnchangedSourceOrEvidence, window.Select(x => x.EvidenceFingerprints), policy.ObservationWindow);
        if (AllSame(window.Select(x => x.SourceHead)))
            signals.Add(new(LoopSignalType.UnchangedSourceOrEvidence, $"head:{window[^1].SourceHead}", policy.ObservationWindow));
        if (AllSameSet(window.Select(x => x.PullRequestStates)))
            signals.Add(new(LoopSignalType.UnchangedSourceOrEvidence, "pr-state", policy.ObservationWindow));
        if (AllSameSet(window.Select(x => x.ManagerRecommendations)))
            signals.Add(new(LoopSignalType.RepeatedManagerReassignment, "manager-recommendation", policy.ObservationWindow));
        signals.Add(new(LoopSignalType.NegligibleProgress, $"verified-delta:{verifiedDelta:0.##}", policy.ObservationWindow));

        var materialSignals = signals.Where(x => x.Type != LoopSignalType.NegligibleProgress).ToArray();
        var action = materialSignals.Length >= policy.AutoStopSignalThreshold
            ? StagnationAction.STALLED_AUTO_STOPPED
            : materialSignals.Any(x => x.Type == LoopSignalType.RepeatedManagerReassignment)
                ? StagnationAction.CHANGE_STRATEGY
                : materialSignals.Any(x => x.Type == LoopSignalType.RepeatedFailedCheck)
                    ? StagnationAction.CHANGE_STRATEGY
                    : materialSignals.Any(x => x.Type == LoopSignalType.RepeatedTaskFingerprint)
                        ? StagnationAction.REPLAN
                        : materialSignals.Any(x => x.Type == LoopSignalType.RepeatedBlocker)
                            ? StagnationAction.CHANGE_STRATEGY
                            : StagnationAction.REFRESH_EVIDENCE;
        return new(materialSignals.Length > 0, signals, action, verifiedDelta, managerDelta);
    }

    private static void AddCommon(List<LoopSignal> signals, LoopSignalType type, IEnumerable<IReadOnlySet<string>> sets, int count)
    {
        var values = sets.ToArray();
        if (values.Length == 0) return;
        var common = new HashSet<string>(values[0], StringComparer.OrdinalIgnoreCase);
        foreach (var set in values.Skip(1)) common.IntersectWith(set);
        foreach (var value in common.Where(x => !string.IsNullOrWhiteSpace(x))) signals.Add(new(type, value, count));
    }

    private static bool AllSame(IEnumerable<string?> values)
    {
        var array = values.ToArray();
        return array.Length > 1 && !string.IsNullOrWhiteSpace(array[0]) && array.All(x => string.Equals(x, array[0], StringComparison.OrdinalIgnoreCase));
    }

    private static bool AllSameSet(IEnumerable<IReadOnlySet<string>> sets)
    {
        var array = sets.ToArray();
        if (array.Length <= 1 || array[0].Count == 0) return false;
        return array.Skip(1).All(x => array[0].SetEquals(x));
    }
}

public sealed record ReassignmentAttempt(TaskId TaskId, string TaskFingerprint, string StrategyFingerprint, string EvidenceFingerprint, int PreviousAutomaticReassignments);
public sealed record ReassignmentDecision(bool Allowed, int NewAutomaticReassignmentCount, string Reason);

public sealed class ReassignmentPolicy
{
    public ReassignmentDecision Evaluate(ReassignmentAttempt attempt)
    {
        var hasNewStrategyOrEvidence = !string.IsNullOrWhiteSpace(attempt.StrategyFingerprint) || !string.IsNullOrWhiteSpace(attempt.EvidenceFingerprint);
        if (!hasNewStrategyOrEvidence)
            return new(false, attempt.PreviousAutomaticReassignments, "Identical task cannot be reassigned without new strategy or evidence.");
        if (attempt.PreviousAutomaticReassignments >= 1)
            return new(false, attempt.PreviousAutomaticReassignments, "Automatic reassignment is limited to one bounded strategy-changing attempt.");
        return new(true, attempt.PreviousAutomaticReassignments + 1, "One bounded reassignment is allowed because strategy/evidence changed.");
    }
}

public enum BlockerCategory
{
    INTERNAL_FIXABLE,
    DEPENDENCY_PENDING,
    CI_INFRA,
    AUTH_REQUIRED,
    EXTERNAL_SERVICE,
    EXTERNAL_AUTHORITY,
    PRODUCT_DECISION,
    UNVERIFIED
}

public enum BlockerRoutingAction
{
    ROUTE_TO_WORKER,
    WAIT_FOR_DEPENDENCY,
    RETRY_CI_INFRA,
    CREATE_ATTENTION,
    TERMINAL_BLOCKED_EXTERNAL,
    VERIFY_FIRST
}

public sealed record BlockerRoutingDecision(BlockerRoutingAction Action, AttentionCategory? AttentionCategory, string Reason);

public sealed class BlockerClassifier
{
    public BlockerRoutingDecision Route(PolicyBlocker blocker) => blocker.Category switch
    {
        BlockerCategory.INTERNAL_FIXABLE => new(BlockerRoutingAction.ROUTE_TO_WORKER, null, "Internal fixable blocker should create executable work, not user Attention."),
        BlockerCategory.DEPENDENCY_PENDING => new(BlockerRoutingAction.WAIT_FOR_DEPENDENCY, null, "Wait for the known dependency and re-evaluate deterministically."),
        BlockerCategory.CI_INFRA => new(BlockerRoutingAction.RETRY_CI_INFRA, null, "Retry or route CI infrastructure repair without user interruption when safe."),
        BlockerCategory.AUTH_REQUIRED => new(BlockerRoutingAction.CREATE_ATTENTION, Application.AttentionCategory.LOGIN_REQUIRED, "Authentication requires operator action."),
        BlockerCategory.EXTERNAL_SERVICE => new(BlockerRoutingAction.TERMINAL_BLOCKED_EXTERNAL, Application.AttentionCategory.EXTERNAL_BLOCKER, "External service blocker cannot be repaired internally."),
        BlockerCategory.EXTERNAL_AUTHORITY => new(BlockerRoutingAction.TERMINAL_BLOCKED_EXTERNAL, Application.AttentionCategory.EXTERNAL_AUTHORITY_REQUIRED, "External authority is required."),
        BlockerCategory.PRODUCT_DECISION => new(BlockerRoutingAction.CREATE_ATTENTION, Application.AttentionCategory.BUSINESS_DECISION_REQUIRED, "A real product/business decision is required."),
        _ => new(BlockerRoutingAction.VERIFY_FIRST, null, "Blocker is unverified; refresh evidence before escalating.")
    };
}

public enum RecoveryCondition
{
    TEMP_ERROR,
    OFFLINE,
    RATE_LIMITED,
    LOGIN_REQUIRED,
    CHALLENGE,
    SUBMITTED_UNKNOWN,
    BROWSER_ADAPTER_UNCERTAIN,
    SLOW,
    CONVERSATION_ROLLOVER,
    RESTART_RECOVERY
}

public enum RecoveryAction
{
    RETRY_SAFE,
    WAIT_AND_RETRY,
    GLOBAL_PAUSE_COOLDOWN,
    CREATE_ATTENTION,
    RECONCILE_BEFORE_RETRY,
    NO_SEND_RECOVER_ADAPTER,
    MONITOR,
    CHECKPOINT_AND_ROLLOVER,
    RESTORE_AND_RECONCILE
}

public sealed record RecoveryDecision(
    RecoveryAction Action,
    AutopilotState State,
    bool AllowSend,
    bool CreatesAttention,
    AttentionCategory? AttentionCategory,
    string Reason);

public sealed class SafeRecoveryPolicy
{
    public RecoveryDecision Decide(RecoveryCondition condition, bool persistentUncertainty = false) => condition switch
    {
        RecoveryCondition.TEMP_ERROR => Auto(RecoveryAction.RETRY_SAFE, AutopilotState.RECOVERING, false, "Bounded automatic recovery handles temporary errors."),
        RecoveryCondition.OFFLINE => Auto(RecoveryAction.WAIT_AND_RETRY, AutopilotState.RECOVERING, false, "Offline state waits and retries without failing Worker work."),
        RecoveryCondition.RATE_LIMITED => Auto(RecoveryAction.GLOBAL_PAUSE_COOLDOWN, AutopilotState.PAUSED, false, "Rate limit pauses all new sends and resumes only after cooldown."),
        RecoveryCondition.LOGIN_REQUIRED => Attention(AttentionCategory.LOGIN_REQUIRED, "Login requires operator action."),
        RecoveryCondition.CHALLENGE => Attention(AttentionCategory.CAPTCHA_OR_CHALLENGE, "Account challenge requires operator action."),
        RecoveryCondition.SUBMITTED_UNKNOWN => Auto(RecoveryAction.RECONCILE_BEFORE_RETRY, AutopilotState.WAITING_FOR_EVIDENCE, false, "Unknown submission must be reconciled before any retry."),
        RecoveryCondition.BROWSER_ADAPTER_UNCERTAIN when persistentUncertainty => Attention(AttentionCategory.UNRESOLVED_SECURITY_DECISION, "Persistent adapter uncertainty cannot be proven safe for sending."),
        RecoveryCondition.BROWSER_ADAPTER_UNCERTAIN => Auto(RecoveryAction.NO_SEND_RECOVER_ADAPTER, AutopilotState.RECOVERING, false, "Adapter uncertainty is fail-safe: no send while semantic target is uncertain."),
        RecoveryCondition.SLOW => Auto(RecoveryAction.MONITOR, AutopilotState.AUTOMATIC_STAGED, true, "A slow session is monitored while independent work continues."),
        RecoveryCondition.CONVERSATION_ROLLOVER => Auto(RecoveryAction.CHECKPOINT_AND_ROLLOVER, AutopilotState.RECOVERING, false, "Conversation rollover is automatic and transactional."),
        RecoveryCondition.RESTART_RECOVERY => Auto(RecoveryAction.RESTORE_AND_RECONCILE, AutopilotState.RECOVERING, false, "Restart restores durable state before resuming."),
        _ => Auto(RecoveryAction.MONITOR, AutopilotState.AUTOMATIC_STAGED, true, "Continue." )
    };

    private static RecoveryDecision Auto(RecoveryAction action, AutopilotState state, bool allowSend, string reason) => new(action, state, allowSend, false, null, reason);
    private static RecoveryDecision Attention(AttentionCategory category, string reason) => new(RecoveryAction.CREATE_ATTENTION, AutopilotState.ATTENTION_REQUIRED, false, true, category, reason);
}

public sealed class WorkerSlotReusePolicy
{
    public WorkerSlot ReleaseIfAccepted(WorkerSlot slot, TaskState taskState, HandoffQuality handoffQuality)
    {
        if (taskState != TaskState.Completed || handoffQuality != HandoffQuality.Valid)
            return slot;
        return slot with { CurrentTaskId = null, IsActive = false };
    }
}

public enum NotificationEventKind
{
    VERIFIED_COMPLETION_MILESTONE,
    ATTENTION_REQUIRED,
    STALLED_AUTO_STOPPED,
    EXTERNAL_BLOCKER,
    UPDATE_INSTALLER_CANDIDATE_READY,
    VERIFIED_100,
    ROUTINE_RETRY,
    TEMP_ERROR_RECOVERED,
    WORKER_COMPLETED
}

public sealed record NotificationDecision(bool Notify, string Reason);

public sealed class SmartNotificationPolicy
{
    public NotificationDecision Evaluate(NotificationEventKind kind) => kind switch
    {
        NotificationEventKind.VERIFIED_COMPLETION_MILESTONE or
        NotificationEventKind.ATTENTION_REQUIRED or
        NotificationEventKind.STALLED_AUTO_STOPPED or
        NotificationEventKind.EXTERNAL_BLOCKER or
        NotificationEventKind.UPDATE_INSTALLER_CANDIDATE_READY or
        NotificationEventKind.VERIFIED_100 => new(true, "Meaningful operator event."),
        _ => new(false, "Routine autonomous event is suppressed to avoid notification spam.")
    };
}

public enum DestructiveActionKind
{
    NONE,
    DELETE_PROJECT_DATA,
    FULL_UNINSTALL_CLEANUP,
    DESTRUCTIVE_GITHUB_OPERATION,
    IRREVERSIBLE_EXTERNAL_OPERATION
}

public sealed record DestructiveApprovalDecision(bool Allowed, bool RequiresExplicitApproval, string Reason);

public sealed class DestructiveApprovalGate
{
    public DestructiveApprovalDecision Evaluate(DestructiveActionKind action, bool explicitApproval)
    {
        if (action == DestructiveActionKind.NONE) return new(true, false, "Action is not destructive.");
        if (!explicitApproval) return new(false, true, "Destructive or irreversible action requires explicit approval.");
        return new(true, true, "Explicit approval is present for the exact destructive action.");
    }
}

public enum ProjectTerminalState
{
    VERIFIED_100,
    BLOCKED_EXTERNAL,
    STALLED_AUTO_STOPPED,
    STOPPED_BY_OPERATOR
}

public sealed record ProjectTerminalRecord(
    ProjectRunId ProjectRunId,
    ProjectTerminalState State,
    string Reason,
    IReadOnlyList<string> Evidence,
    DateTimeOffset ReachedAt);

public enum WaveContinuationDecision
{
    START_NEXT_WAVE,
    WAIT_FOR_RUNNING_WORK,
    WAIT_FOR_DEPENDENCY,
    REFRESH_EVIDENCE,
    ENTER_CLOSURE_MODE,
    AUTO_STOP_STALLED,
    BLOCKED_EXTERNAL,
    VERIFIED_COMPLETE
}

public sealed record WaveContinuationContext(
    bool HasRunningWork,
    bool HasPendingDependency,
    bool EvidenceNeedsRefresh,
    bool HasExternalBlocker,
    bool AutoStoppedByStagnation,
    CompletionControlEvaluation Completion);

public sealed record WaveContinuationResult(WaveContinuationDecision Decision, AutopilotState State, string Reason);

public sealed class WaveContinuationPolicy
{
    public WaveContinuationResult Decide(WaveContinuationContext context)
    {
        if (context.Completion.Mode == ProjectCompletionMode.VerifiedComplete && context.Completion.VerifiedCompletion.Percent == 100m)
            return new(WaveContinuationDecision.VERIFIED_COMPLETE, AutopilotState.VERIFIED_COMPLETE, "All evidence-backed mandatory gates are satisfied.");
        if (context.HasExternalBlocker)
            return new(WaveContinuationDecision.BLOCKED_EXTERNAL, AutopilotState.BLOCKED_EXTERNAL, "External blocker is terminal for autonomous execution.");
        if (context.AutoStoppedByStagnation)
            return new(WaveContinuationDecision.AUTO_STOP_STALLED, AutopilotState.STALLED_AUTO_STOPPED, "Stagnation policy stopped repeated no-progress work.");
        if (context.Completion.Mode == ProjectCompletionMode.ClosureMode || context.Completion.VerifiedCompletion.Percent >= 99m)
            return new(WaveContinuationDecision.ENTER_CLOSURE_MODE, AutopilotState.CLOSURE_MODE, "Verified completion reached Closure Mode threshold.");
        if (context.HasRunningWork)
            return new(WaveContinuationDecision.WAIT_FOR_RUNNING_WORK, AutopilotState.AUTOMATIC_STAGED, "Independent running work should finish before another Manager decision.");
        if (context.HasPendingDependency)
            return new(WaveContinuationDecision.WAIT_FOR_DEPENDENCY, AutopilotState.WAITING_FOR_DEPENDENCY, "Known dependency is pending.");
        if (context.EvidenceNeedsRefresh)
            return new(WaveContinuationDecision.REFRESH_EVIDENCE, AutopilotState.WAITING_FOR_EVIDENCE, "Live evidence must be refreshed before dispatch.");
        return new(WaveContinuationDecision.START_NEXT_WAVE, AutopilotState.AUTOMATIC_STAGED, "Deterministic policy permits the next validated wave.");
    }
}

public sealed record ProgressDelta(decimal ManagerEstimateDelta, decimal VerifiedCompletionDelta)
{
    public static ProgressDelta Between(ManagerEstimate previousManager, ManagerEstimate currentManager, VerifiedCompletion previousVerified, VerifiedCompletion currentVerified) =>
        new(currentManager.Percent - previousManager.Percent, currentVerified.Percent - previousVerified.Percent);
}

public sealed record DecisionJournalRecord(
    DateTimeOffset Timestamp,
    ProjectRunId ProjectRunId,
    AutopilotState CurrentState,
    IReadOnlyDictionary<string, string> Inputs,
    IReadOnlyList<string> Evidence,
    string Decision,
    string Reason,
    string NextAction);

public interface IDecisionJournal
{
    Task AppendAsync(DecisionJournalRecord record, CancellationToken cancellationToken = default);
}

public sealed record AutopilotSanityInput(
    IReadOnlyList<EvidenceQualityAssessment> EvidenceAssessments,
    IReadOnlyList<PolicyBlocker> Blockers,
    ReassignmentDecision? Reassignment,
    bool UnsafeActionRequested,
    DestructiveApprovalDecision? DestructiveApproval);

public sealed class AutopilotManagerSanityChecker
{
    private readonly ManagerSanityChecker _baseChecker;

    public AutopilotManagerSanityChecker(ManagerSanityChecker? baseChecker = null) => _baseChecker = baseChecker ?? new ManagerSanityChecker();

    public IReadOnlyList<ManagerPlanFinding> Check(
        StructuredManagerPlan plan,
        OrchestrationWaveValidation validation,
        ProjectBaselineSnapshot live,
        ProjectCompletionMode completionMode,
        VerifiedCompletion verified,
        LoopAssessment loop,
        AutopilotSanityInput policy)
    {
        var findings = new List<ManagerPlanFinding>(_baseChecker.Check(plan, validation, live, completionMode, verified, loop));
        if (policy.EvidenceAssessments.Any(x => x.Quality is EvidenceQuality.STALE or EvidenceQuality.CONTRADICTED or EvidenceQuality.MISSING))
            findings.Add(new("AUTOPILOT_EVIDENCE_UNSAFE", "Manager recommendation relies on stale, contradicted or missing evidence.", PlanFindingSeverity.Block));
        if (policy.Blockers.Any(x => !x.IsResolved && x.Priority == ClosurePriority.P0_VERIFICATION_BLOCKER) && plan.Tasks.Count == 0)
            findings.Add(new("P0_BLOCKER_IGNORED", "Manager cannot idle while an internal P0 verification blocker remains unresolved.", PlanFindingSeverity.Block));
        if (policy.Reassignment is { Allowed: false })
            findings.Add(new("UNSAFE_REASSIGNMENT", policy.Reassignment.Reason, PlanFindingSeverity.Block));
        if (policy.UnsafeActionRequested)
            findings.Add(new("UNSAFE_ACTION", "Manager requested an action outside safe autonomous policy.", PlanFindingSeverity.Block));
        if (policy.DestructiveApproval is { Allowed: false, RequiresExplicitApproval: true })
            findings.Add(new("DESTRUCTIVE_APPROVAL_REQUIRED", "Manager cannot authorize a destructive action without explicit operator approval.", PlanFindingSeverity.Block));
        return findings;
    }
}
