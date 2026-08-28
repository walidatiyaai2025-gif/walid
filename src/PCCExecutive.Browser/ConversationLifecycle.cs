namespace PCCExecutive.Browser;

public sealed class ConversationLifecycleManager
{
    private readonly IConversationCheckpointPort _checkpoints; private readonly IConversationCreator _creator; private readonly IContinuationSender _sender; private readonly IContinuationValidator _validator; private readonly IConversationLifecycleStore _store;
    public ConversationLifecycleManager(IConversationCheckpointPort checkpoints, IConversationCreator creator, IContinuationSender sender, IContinuationValidator validator, IConversationLifecycleStore store) { _checkpoints = checkpoints; _creator = creator; _sender = sender; _validator = validator; _store = store; }

    public async Task<ConversationRolloverResult> RolloverAsync(ConversationRecord active, string rolloverReason, string continuationPacket, CancellationToken cancellationToken = default)
    {
        if (active.State != ConversationLifecycleState.Active) return new(false, active, null, "PREDECESSOR_NOT_ACTIVE");
        ConversationRecord? candidate = null;
        try
        {
            var checkpointId = await _checkpoints.CreateCheckpointAsync(active, cancellationToken).ConfigureAwait(false);
            var created = await _creator.CreateAsync(active, cancellationToken).ConfigureAwait(false);
            candidate = new ConversationRecord { ConversationId = created.ConversationId, LogicalAgentId = active.LogicalAgentId, ProjectRunId = active.ProjectRunId, Sequence = checked(active.Sequence + 1), UrlOrProviderIdentity = created.UrlOrProviderIdentity, CreatedAt = DateTimeOffset.UtcNow, PredecessorConversationId = active.ConversationId, RolloverReason = rolloverReason, State = ConversationLifecycleState.Candidate };
            await _store.SaveCandidateAsync(candidate, checkpointId, cancellationToken).ConfigureAwait(false);
            if (!await _sender.SendContinuationAsync(candidate, checkpointId, continuationPacket, cancellationToken).ConfigureAwait(false)) return await FailAsync(active, candidate, "CONTINUATION_SEND_FAILED", cancellationToken).ConfigureAwait(false);
            var validation = await _validator.ValidateAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (!validation.IsValid) return await FailAsync(active, candidate, $"CONTINUATION_VALIDATION_FAILED:{validation.Reason}", cancellationToken).ConfigureAwait(false);
            var archived = active with { SuccessorConversationId = candidate.ConversationId, RetiredAt = DateTimeOffset.UtcNow, RolloverReason = rolloverReason, State = ConversationLifecycleState.Archived };
            var successor = candidate with { State = ConversationLifecycleState.Active };
            await _store.CommitRolloverAsync(archived, successor, checkpointId, cancellationToken).ConfigureAwait(false);
            return new(true, successor, archived, "ROLLOVER_COMMITTED");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return await FailAsync(active, candidate, $"ROLLOVER_FAILED:{ex.GetType().Name}", cancellationToken).ConfigureAwait(false); }
    }

    private async Task<ConversationRolloverResult> FailAsync(ConversationRecord predecessor, ConversationRecord? candidate, string reason, CancellationToken cancellationToken)
    {
        var failed = candidate is null ? null : candidate with { State = ConversationLifecycleState.FailedCandidate };
        await _store.RecordFailedRolloverAsync(predecessor, failed, reason, cancellationToken).ConfigureAwait(false);
        return new(false, predecessor, null, reason);
    }
}

public sealed class ConversationHealthEstimator
{
    public ConversationHealthAssessment Assess(ConversationHealthObservation o)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(o.MessageCount); ArgumentOutOfRangeException.ThrowIfNegative(o.CapturedCharacterCount); ArgumentOutOfRangeException.ThrowIfNegative(o.SlowOrStuckEvents);
        if (o.MessageCount >= 250 || o.CapturedCharacterCount >= 1_000_000 || o.SlowOrStuckEvents >= 8) return new(ConversationHealthState.Rotate, true, "Heuristic pressure is high; rotate conservatively. This is not a ChatGPT token counter.");
        if (o.MessageCount >= 180 || o.CapturedCharacterCount >= 700_000 || o.SlowOrStuckEvents >= 5) return new(ConversationHealthState.RolloverSoon, true, "Heuristic growth indicates rollover should be prepared soon; no authoritative remaining-token claim is made.");
        if (o.MessageCount >= 80 || o.CapturedCharacterCount >= 250_000 || o.Age >= TimeSpan.FromDays(7)) return new(ConversationHealthState.Growing, true, "Conversation is growing based on conservative runtime heuristics only.");
        return new(ConversationHealthState.Fresh, true, "Conversation appears fresh based on conservative runtime heuristics only.");
    }
}
