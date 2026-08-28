using System.Collections.Concurrent;

namespace PCCExecutive.Browser;

public enum RuntimeResilienceState
{
    Ready,
    Sending,
    Generating,
    Slow,
    Throttled,
    RateLimited,
    TempError,
    PartialResponse,
    SessionExpired,
    LoginRequired,
    Challenge,
    Offline,
    Stuck,
    Recovering,
    Paused,
    Failed,
    Done,
    ContextLimitDetected
}

public enum RecoveryAction
{
    None,
    KeepWaiting,
    Reinspect,
    ReloadChat,
    RestoreConversation,
    RestartPccSession,
    Escalate
}

public enum SendReconciliationState
{
    MessagePresent,
    MessageNotPresent,
    GenerationInProgress,
    ResponsePresent,
    CannotDetermine
}

public enum RetrySafety { NotSafe, SafeRetry }

public enum ResponseExecutionState
{
    Idle,
    GenerationStarted,
    Generating,
    ApparentlyComplete,
    Stopped,
    Partial,
    ExplicitError,
    RetryOrContinue,
    Unknown
}

public sealed record ResilienceControllerOptions(
    TimeSpan SlowAfter,
    TimeSpan StuckAfter,
    TimeSpan GlobalCooldownMinimum,
    TimeSpan GlobalCooldownMaximum,
    TimeSpan BaseDispatchInterval,
    TimeSpan MaximumAdaptiveInterval)
{
    public static ResilienceControllerOptions Default { get; } = new(
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(8),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromMinutes(5));

    public void Validate()
    {
        if (SlowAfter <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(SlowAfter));
        if (StuckAfter <= SlowAfter) throw new ArgumentOutOfRangeException(nameof(StuckAfter), "StuckAfter must be greater than SlowAfter.");
        if (GlobalCooldownMinimum <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(GlobalCooldownMinimum));
        if (GlobalCooldownMaximum < GlobalCooldownMinimum) throw new ArgumentOutOfRangeException(nameof(GlobalCooldownMaximum));
        if (BaseDispatchInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(BaseDispatchInterval));
        if (MaximumAdaptiveInterval < BaseDispatchInterval) throw new ArgumentOutOfRangeException(nameof(MaximumAdaptiveInterval));
    }
}

public sealed record RuntimeResilienceObservation(
    string RuntimeId,
    ChatGptSemanticSnapshot Snapshot,
    DateTimeOffset ObservedAt,
    DateTimeOffset? GenerationStartedAt = null,
    DateTimeOffset? LastGenerationProgressAt = null,
    bool SubmissionInFlight = false,
    bool ExplicitSessionExpired = false,
    bool ExplicitContextLimitDetected = false,
    TimeSpan? ServiceRetryAfter = null);

public sealed record RuntimeTransitionDecision(
    RuntimeResilienceState Previous,
    RuntimeResilienceState Current,
    FaultScope Scope,
    bool PauseUnsafeNewSends,
    bool PreserveInFlightGenerations,
    bool RequiresHumanAction,
    RecoveryAction RecoveryAction,
    TimeSpan? Cooldown,
    string Reason,
    IReadOnlyList<string> Evidence);

public sealed class RuntimeResilienceStateMachine
{
    private static readonly IReadOnlyDictionary<RuntimeResilienceState, IReadOnlySet<RuntimeResilienceState>> Allowed = Build();

    public bool CanTransition(RuntimeResilienceState from, RuntimeResilienceState to) =>
        from == to || Allowed.TryGetValue(from, out var states) && states.Contains(to);

    public RuntimeResilienceState Transition(RuntimeResilienceState from, RuntimeResilienceState to)
    {
        if (!CanTransition(from, to))
            throw new InvalidOperationException($"Illegal runtime resilience transition: {from} -> {to}.");
        return to;
    }

    private static IReadOnlyDictionary<RuntimeResilienceState, IReadOnlySet<RuntimeResilienceState>> Build()
    {
        static IReadOnlySet<RuntimeResilienceState> S(params RuntimeResilienceState[] values) => values.ToHashSet();
        return new Dictionary<RuntimeResilienceState, IReadOnlySet<RuntimeResilienceState>>
        {
            [RuntimeResilienceState.Ready] = S(RuntimeResilienceState.Sending, RuntimeResilienceState.Generating, RuntimeResilienceState.Throttled, RuntimeResilienceState.RateLimited, RuntimeResilienceState.TempError, RuntimeResilienceState.SessionExpired, RuntimeResilienceState.LoginRequired, RuntimeResilienceState.Challenge, RuntimeResilienceState.Offline, RuntimeResilienceState.Paused, RuntimeResilienceState.ContextLimitDetected, RuntimeResilienceState.Failed),
            [RuntimeResilienceState.Sending] = S(RuntimeResilienceState.Generating, RuntimeResilienceState.Ready, RuntimeResilienceState.Throttled, RuntimeResilienceState.RateLimited, RuntimeResilienceState.TempError, RuntimeResilienceState.SessionExpired, RuntimeResilienceState.LoginRequired, RuntimeResilienceState.Challenge, RuntimeResilienceState.Offline, RuntimeResilienceState.Paused, RuntimeResilienceState.Failed),
            [RuntimeResilienceState.Generating] = S(RuntimeResilienceState.Slow, RuntimeResilienceState.Stuck, RuntimeResilienceState.PartialResponse, RuntimeResilienceState.Done, RuntimeResilienceState.TempError, RuntimeResilienceState.SessionExpired, RuntimeResilienceState.LoginRequired, RuntimeResilienceState.Challenge, RuntimeResilienceState.Offline, RuntimeResilienceState.Paused, RuntimeResilienceState.ContextLimitDetected, RuntimeResilienceState.Failed),
            [RuntimeResilienceState.Slow] = S(RuntimeResilienceState.Generating, RuntimeResilienceState.Stuck, RuntimeResilienceState.PartialResponse, RuntimeResilienceState.Done, RuntimeResilienceState.Recovering, RuntimeResilienceState.Paused, RuntimeResilienceState.ContextLimitDetected, RuntimeResilienceState.Failed),
            [RuntimeResilienceState.Stuck] = S(RuntimeResilienceState.Recovering, RuntimeResilienceState.Generating, RuntimeResilienceState.PartialResponse, RuntimeResilienceState.Done, RuntimeResilienceState.Paused, RuntimeResilienceState.Failed),
            [RuntimeResilienceState.Throttled] = S(RuntimeResilienceState.Recovering, RuntimeResilienceState.Paused, RuntimeResilienceState.RateLimited, RuntimeResilienceState.Failed),
            [RuntimeResilienceState.RateLimited] = S(RuntimeResilienceState.Recovering, RuntimeResilienceState.Paused, RuntimeResilienceState.Failed),
            [RuntimeResilienceState.TempError] = S(RuntimeResilienceState.Recovering, RuntimeResilienceState.Paused, RuntimeResilienceState.Ready, RuntimeResilienceState.Generating, RuntimeResilienceState.Failed),
            [RuntimeResilienceState.PartialResponse] = S(RuntimeResilienceState.Recovering, RuntimeResilienceState.Generating, RuntimeResilienceState.Paused, RuntimeResilienceState.Failed),
            [RuntimeResilienceState.SessionExpired] = S(RuntimeResilienceState.Recovering, RuntimeResilienceState.Paused, RuntimeResilienceState.Failed),
            [RuntimeResilienceState.LoginRequired] = S(RuntimeResilienceState.Recovering, RuntimeResilienceState.Paused, RuntimeResilienceState.Failed),
            [RuntimeResilienceState.Challenge] = S(RuntimeResilienceState.Recovering, RuntimeResilienceState.Paused, RuntimeResilienceState.Failed),
            [RuntimeResilienceState.Offline] = S(RuntimeResilienceState.Recovering, RuntimeResilienceState.Paused, RuntimeResilienceState.Failed),
            [RuntimeResilienceState.Recovering] = S(RuntimeResilienceState.Ready, RuntimeResilienceState.Generating, RuntimeResilienceState.Paused, RuntimeResilienceState.Failed),
            [RuntimeResilienceState.Paused] = S(RuntimeResilienceState.Recovering, RuntimeResilienceState.Ready, RuntimeResilienceState.Generating, RuntimeResilienceState.Failed),
            [RuntimeResilienceState.ContextLimitDetected] = S(RuntimeResilienceState.Recovering, RuntimeResilienceState.Paused, RuntimeResilienceState.Failed),
            [RuntimeResilienceState.Done] = S(RuntimeResilienceState.Ready, RuntimeResilienceState.Sending),
            [RuntimeResilienceState.Failed] = S(RuntimeResilienceState.Recovering, RuntimeResilienceState.Paused)
        };
    }
}

public sealed class ChatGptResilienceController
{
    private readonly ResilienceControllerOptions _options;
    private readonly RuntimeResilienceStateMachine _stateMachine;
    private readonly ConservativeCooldownPolicy _cooldown;
    private readonly ConcurrentDictionary<string, int> _globalFaultCounts = new(StringComparer.Ordinal);

    public ChatGptResilienceController(ResilienceControllerOptions? options = null, RuntimeResilienceStateMachine? stateMachine = null)
    {
        _options = options ?? ResilienceControllerOptions.Default;
        _options.Validate();
        _stateMachine = stateMachine ?? new RuntimeResilienceStateMachine();
        _cooldown = new ConservativeCooldownPolicy(_options.GlobalCooldownMinimum, _options.GlobalCooldownMaximum);
    }

    public RuntimeTransitionDecision Evaluate(RuntimeResilienceState previous, RuntimeResilienceObservation observation)
    {
        var detected = Detect(observation);
        var current = detected.Current;
        if (current == RuntimeResilienceState.Ready && IsRecoverableFault(previous))
            current = RuntimeResilienceState.Recovering;
        else if (previous == RuntimeResilienceState.Recovering && current == RuntimeResilienceState.Recovering)
            current = RuntimeResilienceState.Ready;

        if (!_stateMachine.CanTransition(previous, current))
            current = RuntimeResilienceState.Paused;

        TimeSpan? cooldown = null;
        if (detected.Scope == FaultScope.Global && detected.PauseUnsafeNewSends)
        {
            var count = _globalFaultCounts.AddOrUpdate(detected.Reason, 1, static (_, existing) => checked(existing + 1));
            cooldown = _cooldown.GetCooldown(count, observation.ServiceRetryAfter);
        }
        else if (current == RuntimeResilienceState.Ready)
        {
            _globalFaultCounts.Clear();
        }

        return detected with { Previous = previous, Current = current, Cooldown = cooldown };
    }

    public void ApplyGlobalGate(RuntimeTransitionDecision decision, GlobalBrowserSendGate gate, DateTimeOffset now)
    {
        if (decision.Scope == FaultScope.Global && decision.PauseUnsafeNewSends)
            gate.Apply(new ResilienceDecision(MapLegacy(decision.Current), FaultScope.Global, true, decision.RequiresHumanAction, decision.Reason), now, decision.Cooldown);
    }

    private RuntimeTransitionDecision Detect(RuntimeResilienceObservation o)
    {
        var s = o.Snapshot;
        var evidence = FlattenEvidence(s);
        if (o.ExplicitContextLimitDetected || Has(evidence, "context-limit", "conversation-too-long", "maximum conversation length"))
            return D(RuntimeResilienceState.ContextLimitDetected, FaultScope.PerSession, true, false, RecoveryAction.RestoreConversation, "CONTEXT_LIMIT_DETECTED", evidence);
        if (s.Auth.State == AuthState.Challenge)
            return D(RuntimeResilienceState.Challenge, FaultScope.Global, true, true, RecoveryAction.Escalate, "CHALLENGE_REQUIRES_MANUAL_RESOLUTION", evidence);
        if (o.ExplicitSessionExpired || Has(evidence, "session-expired", "session has expired"))
            return D(RuntimeResilienceState.SessionExpired, FaultScope.Global, true, true, RecoveryAction.Escalate, "SESSION_EXPIRED", evidence);
        if (s.Auth.State == AuthState.LoginRequired)
            return D(RuntimeResilienceState.LoginRequired, FaultScope.Global, true, true, RecoveryAction.Escalate, "LOGIN_REQUIRED", evidence);
        if (s.Health.State == PageHealth.Offline)
            return D(RuntimeResilienceState.Offline, FaultScope.Global, true, false, RecoveryAction.Reinspect, "NETWORK_OFFLINE", evidence);
        if (s.Health.State == PageHealth.RateLimited)
            return D(RuntimeResilienceState.RateLimited, FaultScope.Global, true, false, RecoveryAction.KeepWaiting, "RATE_LIMITED", evidence);
        if (s.ResponseCompleteness == ResponseCompleteness.Partial)
            return D(RuntimeResilienceState.PartialResponse, FaultScope.PerSession, false, false, RecoveryAction.RestoreConversation, "PARTIAL_RESPONSE_CAPTURED", evidence);
        if (s.Health.State == PageHealth.TempError)
        {
            var global = Has(evidence, "sending-too-quickly", "account-level", "global-limit", "rate-limit");
            return D(RuntimeResilienceState.TempError, global ? FaultScope.Global : FaultScope.PerSession, global, false, RecoveryAction.Reinspect, global ? "GLOBAL_TEMP_ERROR" : "SESSION_TEMP_ERROR", evidence);
        }
        if (s.Health.State == PageHealth.Slow && s.Generation.State == GenerationState.Generating)
            return D(RuntimeResilienceState.Slow, FaultScope.PerSession, false, false, RecoveryAction.KeepWaiting, "PROVIDER_REPORTS_SLOW_GENERATION", evidence);
        if (s.Generation.State == GenerationState.Generating)
        {
            var progressAnchor = o.LastGenerationProgressAt ?? o.GenerationStartedAt;
            if (progressAnchor is not null)
            {
                var sinceProgress = o.ObservedAt - progressAnchor.Value;
                if (sinceProgress >= _options.StuckAfter)
                    return D(RuntimeResilienceState.Stuck, FaultScope.PerSession, false, false, RecoveryAction.Reinspect, "GENERATION_STUCK_NO_PROGRESS", evidence);
                if (sinceProgress >= _options.SlowAfter)
                    return D(RuntimeResilienceState.Slow, FaultScope.PerSession, false, false, RecoveryAction.KeepWaiting, "GENERATION_SLOW_BUT_ACTIVE", evidence);
            }
            return D(RuntimeResilienceState.Generating, FaultScope.PerSession, false, false, RecoveryAction.KeepWaiting, "GENERATION_IN_PROGRESS", evidence);
        }
        if (CriticalUnknown(s))
            return D(RuntimeResilienceState.Paused, FaultScope.PerSession, false, false, RecoveryAction.Reinspect, "BROWSER_ADAPTER_UNCERTAIN", evidence);
        if (s.Generation.State == GenerationState.Complete && s.ResponseCompleteness == ResponseCompleteness.Complete)
            return D(RuntimeResilienceState.Done, FaultScope.None, false, false, RecoveryAction.None, "RESPONSE_COMPLETE", evidence);
        if (o.SubmissionInFlight)
            return D(RuntimeResilienceState.Sending, FaultScope.PerSession, false, false, RecoveryAction.KeepWaiting, "SUBMISSION_IN_FLIGHT", evidence);
        if (s.Input.State == InputState.Ready && s.Auth.State == AuthState.Authenticated && s.Health.State == PageHealth.Healthy)
            return D(RuntimeResilienceState.Ready, FaultScope.None, false, false, RecoveryAction.None, "READY", evidence);
        return D(RuntimeResilienceState.Paused, FaultScope.PerSession, false, false, RecoveryAction.Reinspect, "STATE_NOT_PROVEN_SAFE", evidence);
    }

    private static RuntimeTransitionDecision D(RuntimeResilienceState state, FaultScope scope, bool pause, bool human, RecoveryAction action, string reason, IReadOnlyList<string> evidence) =>
        new(state, state, scope, pause, scope == FaultScope.Global, human, action, null, reason, evidence);

    private static bool IsRecoverableFault(RuntimeResilienceState state) => state is RuntimeResilienceState.Throttled or RuntimeResilienceState.RateLimited or RuntimeResilienceState.TempError or RuntimeResilienceState.SessionExpired or RuntimeResilienceState.LoginRequired or RuntimeResilienceState.Challenge or RuntimeResilienceState.Offline or RuntimeResilienceState.Stuck or RuntimeResilienceState.PartialResponse or RuntimeResilienceState.Paused or RuntimeResilienceState.ContextLimitDetected;
    private static bool CriticalUnknown(ChatGptSemanticSnapshot s) => s.Input.State == InputState.Unknown || s.Generation.State == GenerationState.Unknown || s.Auth.State == AuthState.Unknown || s.Conversation.State == ConversationMatch.Unknown || s.Health.State == PageHealth.Unknown || s.Input.Confidence < .60 || s.Auth.Confidence < .60 || s.Conversation.Confidence < .60 || s.Health.Confidence < .60;
    private static IReadOnlyList<string> FlattenEvidence(ChatGptSemanticSnapshot s) => s.Input.Evidence.Concat(s.Generation.Evidence).Concat(s.Auth.Evidence).Concat(s.Conversation.Evidence).Concat(s.Health.Evidence).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    private static bool Has(IEnumerable<string> evidence, params string[] needles) => evidence.Any(item => needles.Any(needle => item.Contains(needle, StringComparison.OrdinalIgnoreCase)));
    private static ChatGptResilienceState MapLegacy(RuntimeResilienceState state) => state switch
    {
        RuntimeResilienceState.Ready => ChatGptResilienceState.Ready,
        RuntimeResilienceState.Sending => ChatGptResilienceState.Sending,
        RuntimeResilienceState.Generating => ChatGptResilienceState.Generating,
        RuntimeResilienceState.Slow => ChatGptResilienceState.Slow,
        RuntimeResilienceState.Throttled => ChatGptResilienceState.Throttled,
        RuntimeResilienceState.RateLimited => ChatGptResilienceState.RateLimited,
        RuntimeResilienceState.TempError => ChatGptResilienceState.TempError,
        RuntimeResilienceState.PartialResponse => ChatGptResilienceState.PartialResponse,
        RuntimeResilienceState.SessionExpired => ChatGptResilienceState.SessionExpired,
        RuntimeResilienceState.LoginRequired or RuntimeResilienceState.Challenge => ChatGptResilienceState.LoginRequired,
        RuntimeResilienceState.Offline => ChatGptResilienceState.Offline,
        RuntimeResilienceState.Stuck => ChatGptResilienceState.Stuck,
        RuntimeResilienceState.Recovering => ChatGptResilienceState.Recovering,
        RuntimeResilienceState.Paused or RuntimeResilienceState.ContextLimitDetected => ChatGptResilienceState.Paused,
        RuntimeResilienceState.Failed => ChatGptResilienceState.Failed,
        RuntimeResilienceState.Done => ChatGptResilienceState.Done,
        _ => ChatGptResilienceState.Paused
    };
}

public sealed record AdaptivePacingState(TimeSpan CurrentInterval, int ConsecutiveHealthySends = 0);
public sealed record AdaptivePacingObservation(RuntimeResilienceState RecentState, int ActiveGeneratingSessions, bool GlobalPaused, bool Recovering, TimeSpan? ServiceRetryAfter = null);
public sealed record AdaptivePacingDecision(AdaptivePacingState State, string Reason);

public sealed class AdaptivePacingPolicy
{
    private readonly ResilienceControllerOptions _options;
    public AdaptivePacingPolicy(ResilienceControllerOptions? options = null) { _options = options ?? ResilienceControllerOptions.Default; _options.Validate(); }

    public AdaptivePacingDecision Evaluate(AdaptivePacingState previous, AdaptivePacingObservation observation)
    {
        if (observation.ActiveGeneratingSessions < 0) throw new ArgumentOutOfRangeException(nameof(observation.ActiveGeneratingSessions));
        var current = previous.CurrentInterval <= TimeSpan.Zero ? _options.BaseDispatchInterval : previous.CurrentInterval;
        if (observation.ServiceRetryAfter is not null)
            return new(new AdaptivePacingState(Clamp(Max(current, observation.ServiceRetryAfter.Value)), 0), "SERVICE_RETRY_GUIDANCE");
        if (observation.GlobalPaused)
            return new(new AdaptivePacingState(Clamp(Max(current, _options.GlobalCooldownMinimum)), 0), "GLOBAL_PAUSE_HOLDS_NEW_SENDS");

        var pressure = observation.RecentState switch
        {
            RuntimeResilienceState.RateLimited => 6d,
            RuntimeResilienceState.Throttled => 4d,
            RuntimeResilienceState.TempError => 3d,
            RuntimeResilienceState.Stuck => 3d,
            RuntimeResilienceState.Slow => 2d,
            RuntimeResilienceState.Recovering => 2d,
            _ => 1d
        };
        pressure += Math.Min(observation.ActiveGeneratingSessions, 5) * .15d;
        if (observation.Recovering) pressure = Math.Max(pressure, 2d);

        if (pressure > 1d)
        {
            var desired = TimeSpan.FromTicks((long)Math.Min(_options.BaseDispatchInterval.Ticks * pressure, _options.MaximumAdaptiveInterval.Ticks));
            return new(new AdaptivePacingState(Clamp(Max(current, desired)), 0), $"PRESSURE_INCREASE:{pressure:0.##}x");
        }

        var healthy = checked(previous.ConsecutiveHealthySends + 1);
        var delta = current - _options.BaseDispatchInterval;
        var next = delta <= TimeSpan.Zero ? _options.BaseDispatchInterval : current - TimeSpan.FromTicks(Math.Max(1, delta.Ticks / 4));
        if (healthy >= 4 && next - _options.BaseDispatchInterval < TimeSpan.FromMilliseconds(250)) next = _options.BaseDispatchInterval;
        return new(new AdaptivePacingState(Clamp(next), healthy), next == _options.BaseDispatchInterval ? "BASE_INTERVAL_RESTORED" : "GRADUAL_RECOVERY_TOWARD_BASE");
    }

    private TimeSpan Clamp(TimeSpan value) => value < _options.BaseDispatchInterval ? _options.BaseDispatchInterval : value > _options.MaximumAdaptiveInterval ? _options.MaximumAdaptiveInterval : value;
    private static TimeSpan Max(TimeSpan left, TimeSpan right) => left >= right ? left : right;
}

public sealed record ConversationDispatchEvidence(
    bool? UserMessagePresent,
    bool GenerationInProgress,
    bool ResponsePresent,
    double Confidence,
    IReadOnlyList<string> Evidence);

public interface IConversationEvidenceProbe
{
    Task<ConversationDispatchEvidence> InspectDispatchAsync(string runtimeId, string dispatchId, string contentHash, CancellationToken cancellationToken = default);
}

public sealed record SendReconciliationResult(SendReconciliationState State, RetrySafety RetrySafety, string Reason, IReadOnlyList<string> Evidence);

public sealed class UncertainSendReconciler
{
    private readonly IConversationEvidenceProbe _probe;
    public UncertainSendReconciler(IConversationEvidenceProbe probe) => _probe = probe;

    public async Task<SendReconciliationResult> ReconcileAsync(string runtimeId, DispatchLedgerEntry? dispatch, CancellationToken cancellationToken = default)
    {
        if (dispatch is null)
            return new(SendReconciliationState.CannotDetermine, RetrySafety.NotSafe, "DISPATCH_EVIDENCE_MISSING_NO_AUTOMATIC_RESEND", Array.Empty<string>());
        if (dispatch.State != DispatchState.SubmittedUnknown)
            return new(SendReconciliationState.CannotDetermine, RetrySafety.NotSafe, "DISPATCH_NOT_SUBMITTED_UNKNOWN", Array.Empty<string>());

        ConversationDispatchEvidence? evidence;
        try
        {
            evidence = await _probe.InspectDispatchAsync(runtimeId, dispatch.DispatchId, dispatch.ContentHash, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return new(SendReconciliationState.CannotDetermine, RetrySafety.NotSafe, "PROBE_FAILED_NO_AUTOMATIC_RESEND", new[] { $"probe-error:{ex.GetType().Name}" });
        }
        if (evidence is null)
            return new(SendReconciliationState.CannotDetermine, RetrySafety.NotSafe, "PROBE_RETURNED_NULL_NO_AUTOMATIC_RESEND", Array.Empty<string>());
        var semanticEvidence = evidence.Evidence ?? Array.Empty<string>();
        if (evidence.ResponsePresent)
            return new(SendReconciliationState.ResponsePresent, RetrySafety.NotSafe, "RESPONSE_PRESENT_NO_RETRY", semanticEvidence);
        if (evidence.GenerationInProgress)
            return new(SendReconciliationState.GenerationInProgress, RetrySafety.NotSafe, "GENERATION_IN_PROGRESS_NO_RETRY", semanticEvidence);
        if (evidence.UserMessagePresent == true)
            return new(SendReconciliationState.MessagePresent, RetrySafety.NotSafe, "MESSAGE_PRESENT_NO_RETRY", semanticEvidence);
        if (evidence.UserMessagePresent == false && evidence.Confidence >= .90)
            return new(SendReconciliationState.MessageNotPresent, RetrySafety.SafeRetry, "MESSAGE_ABSENCE_PROVEN_SAFE_RETRY", semanticEvidence);
        return new(SendReconciliationState.CannotDetermine, RetrySafety.NotSafe, "CANNOT_DETERMINE_NO_AUTOMATIC_RESEND", semanticEvidence);
    }
}

public sealed record ResponseCompletionObservation(
    ChatGptSemanticSnapshot Snapshot,
    bool StopControlVisible,
    bool ContinueControlVisible,
    bool RetryControlVisible,
    bool ResponseActionsVisible,
    bool ExplicitErrorVisible,
    bool GenerationWasObserved);

public sealed record ResponseCompletionDecision(ResponseExecutionState State, bool MayReportDone, string Reason, IReadOnlyList<string> Evidence);

public sealed class ResponseCompletionClassifier
{
    public ResponseCompletionDecision Classify(ResponseCompletionObservation o)
    {
        var evidence = o.Snapshot.Generation.Evidence.Concat(o.Snapshot.Health.Evidence).ToArray();
        if (o.ExplicitErrorVisible) return new(ResponseExecutionState.ExplicitError, false, "EXPLICIT_ERROR_VISIBLE", evidence);
        if (o.ContinueControlVisible || o.RetryControlVisible) return new(o.Snapshot.ResponseCompleteness == ResponseCompleteness.Partial ? ResponseExecutionState.Partial : ResponseExecutionState.RetryOrContinue, false, "RETRY_OR_CONTINUE_CONTROL_VISIBLE", evidence);
        if (o.Snapshot.ResponseCompleteness == ResponseCompleteness.Partial) return new(ResponseExecutionState.Partial, false, "PARTIAL_RESPONSE", evidence);
        if (o.StopControlVisible || o.Snapshot.Generation.State == GenerationState.Generating) return new(o.GenerationWasObserved ? ResponseExecutionState.Generating : ResponseExecutionState.GenerationStarted, false, "GENERATION_ACTIVE", evidence);
        if (o.GenerationWasObserved && o.Snapshot.Generation.State == GenerationState.Complete && o.Snapshot.ResponseCompleteness == ResponseCompleteness.Complete && o.ResponseActionsVisible)
            return new(ResponseExecutionState.ApparentlyComplete, true, "COMPLETION_PROVEN_BY_MULTIPLE_SEMANTIC_SIGNALS", evidence);
        if (o.GenerationWasObserved && o.Snapshot.Generation.State != GenerationState.Generating && !o.ResponseActionsVisible && o.Snapshot.CapturedResponseText is not null)
            return new(ResponseExecutionState.Stopped, false, "GENERATION_STOPPED_WITHOUT_COMPLETION_PROOF", evidence);
        if (o.Snapshot.Generation.State == GenerationState.Idle && o.Snapshot.AssistantMessageCount == 0) return new(ResponseExecutionState.Idle, false, "IDLE_NO_RESPONSE", evidence);
        return new(ResponseExecutionState.Unknown, false, "RESPONSE_COMPLETION_UNPROVEN", evidence);
    }
}

public sealed record RecoveryAttemptContext(
    int Attempt,
    RuntimeResilienceState State,
    bool GenerationStillActive,
    bool DestructiveRecoveryEvidence,
    bool ExactConversationUrlKnown,
    bool PccOwnershipProven);

public sealed record RecoveryStep(int Level, RecoveryAction Action, string Reason, bool RequiresEvidenceRecording);

public sealed class RecoveryLadder
{
    public RecoveryStep Decide(RecoveryAttemptContext context)
    {
        if (context.Attempt <= 1) return new(1, context.GenerationStillActive ? RecoveryAction.KeepWaiting : RecoveryAction.Reinspect, "WAIT_AND_REINSPECT", true);
        if (context.GenerationStillActive && !context.DestructiveRecoveryEvidence) return new(1, RecoveryAction.KeepWaiting, "PRESERVE_ACTIVE_GENERATION_WITHOUT_RECOVERY_EVIDENCE", true);
        if (context.Attempt == 2) return new(2, RecoveryAction.ReloadChat, "RELOAD_CURRENT_CHAT", true);
        if (context.Attempt == 3) return context.ExactConversationUrlKnown ? new(3, RecoveryAction.RestoreConversation, "RESTORE_EXACT_CONVERSATION", true) : new(5, RecoveryAction.Escalate, "CONVERSATION_URL_UNKNOWN", true);
        if (context.Attempt == 4) return context.PccOwnershipProven ? new(4, RecoveryAction.RestartPccSession, "RESTART_PROVEN_PCC_OWNED_SESSION", true) : new(5, RecoveryAction.Escalate, "OWNERSHIP_NOT_PROVEN_NO_RESTART", true);
        return new(5, RecoveryAction.Escalate, "RECOVERY_LADDER_EXHAUSTED", true);
    }
}

public sealed record RuntimePreservationEnvelope(
    string ProjectRunId,
    string LogicalAgentId,
    string? TaskId,
    string? ConversationIdentity,
    IReadOnlyList<string> DispatchIds,
    string? CapturedResponse,
    string Reason,
    DateTimeOffset CapturedAt);

public interface IRuntimePreservationPort
{
    Task PreserveAsync(RuntimePreservationEnvelope envelope, CancellationToken cancellationToken = default);
}

public sealed record AttentionBoundaryRequirement(bool Required, string Category, string Reason, string RequiredAction, string? RuntimeId);

public sealed class AuthenticationRecoveryCoordinator
{
    private readonly IRuntimePreservationPort _preservation;
    public AuthenticationRecoveryCoordinator(IRuntimePreservationPort preservation) => _preservation = preservation;

    public async Task<AttentionBoundaryRequirement> PauseSafelyAsync(string runtimeId, RuntimePreservationEnvelope envelope, RuntimeResilienceState state, CancellationToken cancellationToken = default)
    {
        if (state is not (RuntimeResilienceState.LoginRequired or RuntimeResilienceState.SessionExpired or RuntimeResilienceState.Challenge))
            return new(false, "NONE", "AUTH_RECOVERY_NOT_REQUIRED", string.Empty, runtimeId);
        await _preservation.PreserveAsync(envelope, cancellationToken).ConfigureAwait(false);
        var action = state == RuntimeResilienceState.Challenge ? "Open the exact PCC browser session and complete the account challenge manually." : "Open the exact PCC browser session and sign in manually.";
        return new(true, state.ToString().ToUpperInvariant(), "UNSAFE_SENDS_PAUSED_STATE_PRESERVED", action, runtimeId);
    }
}
