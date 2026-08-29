namespace PCCExecutive.Browser;

public sealed record PartialResponseCapture(
    string DispatchId,
    string TaskId,
    string LogicalAgentId,
    string ConversationIdentity,
    string CapturedText,
    IReadOnlyList<string> Evidence,
    DateTimeOffset CapturedAt);

public interface IPartialResponseCapturePort
{
    Task SaveAsync(PartialResponseCapture capture, CancellationToken cancellationToken = default);
}

public sealed record PartialResponseRecoveryPlan(
    bool Captured,
    bool MayReportDone,
    string ContinuationInstruction,
    string Reason,
    PartialResponseCapture? Capture);

public sealed class PartialResponseRecoveryCoordinator
{
    private readonly IPartialResponseCapturePort _capturePort;
    public PartialResponseRecoveryCoordinator(IPartialResponseCapturePort capturePort) => _capturePort = capturePort;

    public async Task<PartialResponseRecoveryPlan> CaptureAsync(BrowserDispatchRequest request, ChatGptSemanticSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        if (snapshot.ResponseCompleteness != ResponseCompleteness.Partial)
            return new(false, false, string.Empty, "RESPONSE_NOT_CLASSIFIED_PARTIAL", null);

        var text = snapshot.CapturedResponseText ?? string.Empty;
        var evidence = snapshot.Generation.Evidence.Concat(snapshot.Health.Evidence).Append($"adapter:{snapshot.AdapterVersion}").ToArray();
        var capture = new PartialResponseCapture(request.DispatchId, request.TaskId, request.LogicalAgentId, request.ConversationIdentity, text, evidence, DateTimeOffset.UtcNow);
        await _capturePort.SaveAsync(capture, cancellationToken).ConfigureAwait(false);
        var continuation = $"Continue the existing task without repeating completed text. DISPATCH_ID={request.DispatchId}; TASK_ID={request.TaskId}; CONVERSATION_ID={request.ConversationIdentity}. Reconcile the captured partial response before continuing.";
        return new(true, false, continuation, "PARTIAL_RESPONSE_CAPTURED_NOT_DONE", capture);
    }
}

public sealed class DispatchReconciliationCoordinator
{
    private readonly UncertainSendReconciler _reconciler;
    private readonly IDispatchLedger _ledger;

    public DispatchReconciliationCoordinator(UncertainSendReconciler reconciler, IDispatchLedger ledger)
    {
        _reconciler = reconciler;
        _ledger = ledger;
    }

    public async Task<SendReconciliationResult> ReconcileAsync(string runtimeId, DispatchLedgerEntry dispatch, CancellationToken cancellationToken = default)
    {
        var result = await _reconciler.ReconcileAsync(runtimeId, dispatch, cancellationToken).ConfigureAwait(false);
        var evidence = string.Join(";", result.Evidence);
        switch (result.State)
        {
            case SendReconciliationState.ResponsePresent:
                await _ledger.UpdateAsync(dispatch.DispatchId, DispatchState.ResponseComplete, evidence, cancellationToken).ConfigureAwait(false);
                break;
            case SendReconciliationState.GenerationInProgress:
                await _ledger.UpdateAsync(dispatch.DispatchId, DispatchState.Generating, evidence, cancellationToken).ConfigureAwait(false);
                break;
            case SendReconciliationState.MessagePresent:
                await _ledger.UpdateAsync(dispatch.DispatchId, DispatchState.Acknowledged, evidence, cancellationToken).ConfigureAwait(false);
                break;
            case SendReconciliationState.MessageNotPresent when result.RetrySafety == RetrySafety.SafeRetry:
                await _ledger.UpdateAsync(dispatch.DispatchId, DispatchState.SafeRetry, evidence, cancellationToken).ConfigureAwait(false);
                break;
            case SendReconciliationState.CannotDetermine:
                await _ledger.UpdateAsync(dispatch.DispatchId, DispatchState.SubmittedUnknown, evidence, cancellationToken).ConfigureAwait(false);
                break;
        }
        return result;
    }
}

public sealed record GlobalRateLimitRecoveryDecision(bool MayResumeNewSends, bool GateResumed, string Reason, TimeSpan? SuggestedInterval);

public sealed class GlobalRateLimitRecoveryCoordinator
{
    private readonly GlobalBrowserSendGate _gate;
    private readonly AdaptivePacingPolicy _pacing;

    public GlobalRateLimitRecoveryCoordinator(GlobalBrowserSendGate gate, AdaptivePacingPolicy? pacing = null)
    {
        _gate = gate;
        _pacing = pacing ?? new AdaptivePacingPolicy();
    }

    public GlobalRateLimitRecoveryDecision Reevaluate(DateTimeOffset now, IReadOnlyList<RuntimeTransitionDecision> currentHealth, AdaptivePacingState pacingState)
    {
        if (!_gate.Snapshot.IsPaused)
            return new(true, false, "GLOBAL_GATE_ALREADY_OPEN", pacingState.CurrentInterval);
        if (_gate.Snapshot.ResumeNotBefore is not null && now < _gate.Snapshot.ResumeNotBefore.Value)
            return new(false, false, "COOLDOWN_NOT_ELAPSED", pacingState.CurrentInterval);
        if (currentHealth.Any(x => x.Scope == FaultScope.Global && x.PauseUnsafeNewSends))
            return new(false, false, "GLOBAL_FAULT_STILL_PRESENT", pacingState.CurrentInterval);
        if (currentHealth.Count == 0 || currentHealth.Any(x => x.Current is RuntimeResilienceState.Paused or RuntimeResilienceState.Failed))
            return new(false, false, "HEALTH_NOT_PROVEN_FOR_RESUME", pacingState.CurrentInterval);

        if (!_gate.TryResume(now, "GLOBAL_HEALTH_REEVALUATED_SAFE"))
            return new(false, false, "GLOBAL_GATE_REFUSED_RESUME", pacingState.CurrentInterval);
        var pace = _pacing.Evaluate(pacingState, new AdaptivePacingObservation(RuntimeResilienceState.Recovering, currentHealth.Count(x => x.Current == RuntimeResilienceState.Generating), false, true));
        return new(true, true, "GLOBAL_SENDS_RESUMED_GRADUALLY", pace.State.CurrentInterval);
    }
}

public sealed record RecoveryEvidence(string RuntimeId, int Level, RecoveryAction Action, string Reason, DateTimeOffset OccurredAt, IReadOnlyList<string> Evidence);

public interface IRecoveryEvidencePort
{
    Task RecordAsync(RecoveryEvidence evidence, CancellationToken cancellationToken = default);
}

public sealed class RecoveryLadderCoordinator
{
    private readonly RecoveryLadder _ladder;
    private readonly IRecoveryEvidencePort _evidence;

    public RecoveryLadderCoordinator(IRecoveryEvidencePort evidence, RecoveryLadder? ladder = null)
    {
        _evidence = evidence;
        _ladder = ladder ?? new RecoveryLadder();
    }

    public async Task<RecoveryStep> DecideAndRecordAsync(string runtimeId, RecoveryAttemptContext context, IReadOnlyList<string> evidence, CancellationToken cancellationToken = default)
    {
        var step = _ladder.Decide(context);
        await _evidence.RecordAsync(new RecoveryEvidence(runtimeId, step.Level, step.Action, step.Reason, DateTimeOffset.UtcNow, evidence), cancellationToken).ConfigureAwait(false);
        return step;
    }
}
