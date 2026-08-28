namespace PCCExecutive.Browser;

public enum RolloverStage
{
    Active,
    RolloverRequested,
    CheckpointRequired,
    CheckpointCreated,
    NewConversationCreated,
    ContinuationPacketSubmitted,
    ContinuationAcknowledged,
    ContinuationValidated,
    NewConversationActive,
    OldConversationArchived,
    Failed
}

public sealed record ContinuationPacketData(
    string Project,
    string Repository,
    string LogicalAgent,
    string CurrentTask,
    string Wave,
    string LatestBranch,
    string LatestHead,
    string OpenPr,
    IReadOnlyList<string> CompletedWork,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<string> ImportantDecisions,
    string CheckpointId,
    string PreviousConversationId,
    string NextAction);

public sealed class ContinuationPacketBuilder
{
    public string Build(ContinuationPacketData data)
    {
        static string Lines(IEnumerable<string> values) => string.Join(" | ", values.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()));
        return string.Join('\n', new[]
        {
            $"PROJECT: {data.Project}",
            $"REPOSITORY: {data.Repository}",
            $"LOGICAL_AGENT: {data.LogicalAgent}",
            $"CURRENT_TASK: {data.CurrentTask}",
            $"WAVE: {data.Wave}",
            $"LATEST_BRANCH: {data.LatestBranch}",
            $"LATEST_HEAD: {data.LatestHead}",
            $"OPEN_PR: {data.OpenPr}",
            $"COMPLETED_WORK: {Lines(data.CompletedWork)}",
            $"BLOCKERS: {Lines(data.Blockers)}",
            $"IMPORTANT_DECISIONS: {Lines(data.ImportantDecisions)}",
            $"CHECKPOINT_ID: {data.CheckpointId}",
            $"PREVIOUS_CONVERSATION_ID: {data.PreviousConversationId}",
            $"NEXT_ACTION: {data.NextAction}",
            "FETCH LIVE STATE BEFORE MAKING NEW CONCLUSIONS."
        });
    }
}

public sealed record ContinuationProof(
    bool CorrectConversationOpened,
    bool ContinuationSubmitted,
    bool AcknowledgementObtained,
    bool LogicalIdentityMatches,
    bool ProjectIdentityMatches,
    bool TaskIdentityMatches,
    bool SessionHealthy,
    IReadOnlyList<string> Evidence)
{
    public bool IsValid => CorrectConversationOpened && ContinuationSubmitted && AcknowledgementObtained && LogicalIdentityMatches && ProjectIdentityMatches && TaskIdentityMatches && SessionHealthy;
}

public interface IContinuationProofPort
{
    Task<ContinuationProof> ValidateAsync(ConversationRecord candidate, string checkpointId, CancellationToken cancellationToken = default);
}

public interface IRolloverJournalPort
{
    Task RecordAsync(string logicalAgentId, string conversationId, RolloverStage stage, string reason, DateTimeOffset occurredAt, CancellationToken cancellationToken = default);
}

public sealed record RolloverRequest(
    ConversationRecord ActiveConversation,
    string RolloverReason,
    Func<string, ContinuationPacketData> PacketFactory);

public sealed record HardenedRolloverResult(
    bool Succeeded,
    RolloverStage Stage,
    ConversationRecord ActiveConversation,
    ConversationRecord? RetiredConversation,
    ConversationRecord? FailedCandidate,
    string? CheckpointId,
    string Reason,
    IReadOnlyList<string> Evidence);

public sealed class ConversationRolloverCoordinator
{
    private readonly IConversationCheckpointPort _checkpoints;
    private readonly IConversationCreator _creator;
    private readonly IContinuationSender _sender;
    private readonly IContinuationProofPort _proof;
    private readonly IConversationLifecycleStore _store;
    private readonly IRolloverJournalPort _journal;
    private readonly ContinuationPacketBuilder _packetBuilder;

    public ConversationRolloverCoordinator(
        IConversationCheckpointPort checkpoints,
        IConversationCreator creator,
        IContinuationSender sender,
        IContinuationProofPort proof,
        IConversationLifecycleStore store,
        IRolloverJournalPort journal,
        ContinuationPacketBuilder? packetBuilder = null)
    {
        _checkpoints = checkpoints;
        _creator = creator;
        _sender = sender;
        _proof = proof;
        _store = store;
        _journal = journal;
        _packetBuilder = packetBuilder ?? new ContinuationPacketBuilder();
    }

    public async Task<HardenedRolloverResult> RolloverAsync(RolloverRequest request, CancellationToken cancellationToken = default)
    {
        var active = request.ActiveConversation;
        if (active.State != ConversationLifecycleState.Active)
            return new(false, RolloverStage.Failed, active, null, null, null, "PREDECESSOR_NOT_ACTIVE", Array.Empty<string>());

        ConversationRecord? candidate = null;
        string? checkpointId = null;
        var evidence = new List<string>();
        try
        {
            await Record(active, RolloverStage.RolloverRequested, request.RolloverReason, cancellationToken).ConfigureAwait(false);
            await Record(active, RolloverStage.CheckpointRequired, "CHECKPOINT_REQUIRED_BEFORE_ROLLOVER", cancellationToken).ConfigureAwait(false);
            checkpointId = await _checkpoints.CreateCheckpointAsync(active, cancellationToken).ConfigureAwait(false);
            evidence.Add($"checkpoint:{checkpointId}");
            await Record(active, RolloverStage.CheckpointCreated, checkpointId, cancellationToken).ConfigureAwait(false);

            var created = await _creator.CreateAsync(active, cancellationToken).ConfigureAwait(false);
            candidate = new ConversationRecord
            {
                ConversationId = created.ConversationId,
                LogicalAgentId = active.LogicalAgentId,
                ProjectRunId = active.ProjectRunId,
                Sequence = checked(active.Sequence + 1),
                UrlOrProviderIdentity = created.UrlOrProviderIdentity,
                CreatedAt = DateTimeOffset.UtcNow,
                PredecessorConversationId = active.ConversationId,
                RolloverReason = request.RolloverReason,
                State = ConversationLifecycleState.Candidate
            };
            await _store.SaveCandidateAsync(candidate, checkpointId, cancellationToken).ConfigureAwait(false);
            await Record(candidate, RolloverStage.NewConversationCreated, created.UrlOrProviderIdentity, cancellationToken).ConfigureAwait(false);

            var packetData = request.PacketFactory(checkpointId) with { CheckpointId = checkpointId, PreviousConversationId = active.ConversationId };
            var packet = _packetBuilder.Build(packetData);
            if (!await _sender.SendContinuationAsync(candidate, checkpointId, packet, cancellationToken).ConfigureAwait(false))
                return await Fail(active, candidate, checkpointId, RolloverStage.ContinuationPacketSubmitted, "CONTINUATION_SEND_FAILED", evidence, cancellationToken).ConfigureAwait(false);
            await Record(candidate, RolloverStage.ContinuationPacketSubmitted, "CONTINUATION_PACKET_SUBMITTED", cancellationToken).ConfigureAwait(false);

            var proof = await _proof.ValidateAsync(candidate, checkpointId, cancellationToken).ConfigureAwait(false);
            evidence.AddRange(proof.Evidence);
            if (!proof.AcknowledgementObtained)
                return await Fail(active, candidate, checkpointId, RolloverStage.ContinuationAcknowledged, "CONTINUATION_ACKNOWLEDGEMENT_NOT_PROVEN", evidence, cancellationToken).ConfigureAwait(false);
            await Record(candidate, RolloverStage.ContinuationAcknowledged, "CONTINUATION_ACKNOWLEDGED", cancellationToken).ConfigureAwait(false);
            if (!proof.IsValid)
                return await Fail(active, candidate, checkpointId, RolloverStage.ContinuationValidated, "CONTINUATION_VALIDATION_FAILED", evidence, cancellationToken).ConfigureAwait(false);
            await Record(candidate, RolloverStage.ContinuationValidated, "CONTINUATION_IDENTITY_AND_HEALTH_VALIDATED", cancellationToken).ConfigureAwait(false);

            var now = DateTimeOffset.UtcNow;
            var archived = active with
            {
                SuccessorConversationId = candidate.ConversationId,
                RetiredAt = now,
                RolloverReason = request.RolloverReason,
                State = ConversationLifecycleState.Archived
            };
            var successor = candidate with { State = ConversationLifecycleState.Active };

            await _store.CommitRolloverAsync(archived, successor, checkpointId, cancellationToken).ConfigureAwait(false);
            await Record(successor, RolloverStage.NewConversationActive, "ATOMIC_ACTIVE_CONVERSATION_SWITCH_COMMITTED", cancellationToken).ConfigureAwait(false);
            await Record(archived, RolloverStage.OldConversationArchived, "PREDECESSOR_ARCHIVED_AFTER_SUCCESSOR_VALIDATION", cancellationToken).ConfigureAwait(false);
            return new(true, RolloverStage.OldConversationArchived, successor, archived, null, checkpointId, "ROLLOVER_COMMITTED", evidence);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return await Fail(active, candidate, checkpointId, RolloverStage.Failed, $"ROLLOVER_FAILED:{ex.GetType().Name}", evidence, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<HardenedRolloverResult> Fail(ConversationRecord active, ConversationRecord? candidate, string? checkpointId, RolloverStage stage, string reason, IReadOnlyList<string> evidence, CancellationToken cancellationToken)
    {
        var failedCandidate = candidate is null ? null : candidate with { State = ConversationLifecycleState.FailedCandidate };
        await _store.RecordFailedRolloverAsync(active, failedCandidate, reason, cancellationToken).ConfigureAwait(false);
        await Record(active, RolloverStage.Failed, reason, cancellationToken).ConfigureAwait(false);
        return new(false, stage, active, null, failedCandidate, checkpointId, reason, evidence);
    }

    private Task Record(ConversationRecord conversation, RolloverStage stage, string reason, CancellationToken cancellationToken) =>
        _journal.RecordAsync(conversation.LogicalAgentId, conversation.ConversationId, stage, reason, DateTimeOffset.UtcNow, cancellationToken);
}

public sealed record ConversationGrowthObservation(
    int MessageCount,
    long CapturedCharacterCount,
    int WaveCount,
    TimeSpan Age,
    int SlowOrStuckEvents,
    bool ContextLimitDetected,
    bool LongConversationComposerFailure);

public sealed record PreventiveRolloverDecision(ConversationHealthState State, bool FreezeNewWork, bool RequestCheckpoint, bool IsHeuristic, string Reason);

public sealed class PreventiveRolloverPolicy
{
    public PreventiveRolloverDecision Evaluate(ConversationGrowthObservation observation)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(observation.MessageCount);
        ArgumentOutOfRangeException.ThrowIfNegative(observation.CapturedCharacterCount);
        ArgumentOutOfRangeException.ThrowIfNegative(observation.WaveCount);
        ArgumentOutOfRangeException.ThrowIfNegative(observation.SlowOrStuckEvents);

        if (observation.ContextLimitDetected)
            return new(ConversationHealthState.Rotate, true, true, false, "CONTEXT_LIMIT_DETECTED_RECOVER_BY_ROLLOVER");
        if (observation.LongConversationComposerFailure)
            return new(ConversationHealthState.Rotate, true, true, true, "LONG_CONVERSATION_COMPOSER_FAILURE_HEURISTIC_ROTATE");
        if (observation.MessageCount >= 250 || observation.CapturedCharacterCount >= 1_000_000 || observation.WaveCount >= 35 || observation.SlowOrStuckEvents >= 8)
            return new(ConversationHealthState.Rotate, true, true, true, "HEURISTIC_ROTATE_NOT_A_TOKEN_COUNTER");
        if (observation.MessageCount >= 180 || observation.CapturedCharacterCount >= 700_000 || observation.WaveCount >= 25 || observation.SlowOrStuckEvents >= 5)
            return new(ConversationHealthState.RolloverSoon, false, true, true, "HEURISTIC_ROLLOVER_SOON_NOT_A_TOKEN_COUNTER");
        if (observation.MessageCount >= 80 || observation.CapturedCharacterCount >= 250_000 || observation.WaveCount >= 10 || observation.Age >= TimeSpan.FromDays(7))
            return new(ConversationHealthState.Growing, false, false, true, "HEURISTIC_GROWING_NOT_A_TOKEN_COUNTER");
        return new(ConversationHealthState.Fresh, false, false, true, "HEURISTIC_FRESH_NOT_A_TOKEN_COUNTER");
    }
}

public interface IConversationArchiveEvidencePort
{
    Task<bool> IsLineageSafelyArchivedAsync(string logicalAgentId, string conversationIdentity, CancellationToken cancellationToken = default);
}

public sealed record ArchivedRetirementResult(IReadOnlyList<string> RetiredRuntimeIds, IReadOnlyDictionary<string, string> SkippedReasons);

public sealed class ArchivedConversationRuntimeRetirementService
{
    private readonly IBrowserRuntimeRegistry _registry;
    private readonly BrowserSessionController _sessions;
    private readonly IConversationArchiveEvidencePort _archiveEvidence;

    public ArchivedConversationRuntimeRetirementService(IBrowserRuntimeRegistry registry, BrowserSessionController sessions, IConversationArchiveEvidencePort archiveEvidence)
    {
        _registry = registry;
        _sessions = sessions;
        _archiveEvidence = archiveEvidence;
    }

    public async Task<ArchivedRetirementResult> RetireArchivedAsync(CancellationToken cancellationToken = default)
    {
        var retired = new List<string>();
        var skipped = new Dictionary<string, string>(StringComparer.Ordinal);
        var runtimes = await _registry.ListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var runtime in runtimes.Where(x => x.IsArchived || x.State == BrowserSessionState.Archived))
        {
            if (string.IsNullOrWhiteSpace(runtime.ConversationIdentity))
            {
                skipped[runtime.RuntimeId] = "CONVERSATION_IDENTITY_UNKNOWN";
                continue;
            }
            if (!await _archiveEvidence.IsLineageSafelyArchivedAsync(runtime.LogicalAgentId, runtime.ConversationIdentity, cancellationToken).ConfigureAwait(false))
            {
                skipped[runtime.RuntimeId] = "LINEAGE_ARCHIVE_NOT_PROVEN";
                continue;
            }
            var result = await _sessions.KillAsync(runtime.RuntimeId, cancellationToken).ConfigureAwait(false);
            if (result.Succeeded) retired.Add(runtime.RuntimeId); else skipped[runtime.RuntimeId] = result.Reason;
        }
        return new(retired, skipped);
    }
}
