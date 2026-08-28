using PCCExecutive.Browser;

namespace PCCExecutive.App.Presentation;

internal static class RecoveryRolloverLifecycleBridge
{
    public static Task<ConversationRolloverResult> CommitWithExistingConversationLifecycleManagerAsync(
        ConversationRecord predecessor,
        ConversationRecord provenCandidate,
        string checkpointId,
        string reason,
        Func<ConversationRecord, ConversationRecord, string, CancellationToken, Task> commit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commit);
        var ports = new PrevalidatedLifecyclePorts(provenCandidate, checkpointId, commit);
        var lifecycle = new ConversationLifecycleManager(ports, ports, ports, ports, ports);
        return lifecycle.RolloverAsync(
            predecessor,
            reason,
            "CONTINUATION_ALREADY_PROVEN_NO_RESEND",
            cancellationToken);
    }

    private sealed class PrevalidatedLifecyclePorts :
        IConversationCheckpointPort,
        IConversationCreator,
        IContinuationSender,
        IContinuationValidator,
        IConversationLifecycleStore
    {
        private readonly ConversationRecord _candidate;
        private readonly string _checkpointId;
        private readonly Func<ConversationRecord, ConversationRecord, string, CancellationToken, Task> _commit;

        public PrevalidatedLifecyclePorts(
            ConversationRecord candidate,
            string checkpointId,
            Func<ConversationRecord, ConversationRecord, string, CancellationToken, Task> commit)
        {
            _candidate = candidate;
            _checkpointId = checkpointId;
            _commit = commit;
        }

        public Task<string> CreateCheckpointAsync(ConversationRecord activeConversation, CancellationToken cancellationToken = default) =>
            Task.FromResult(_checkpointId);

        public Task<ConversationCreationResult> CreateAsync(ConversationRecord predecessor, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ConversationCreationResult(_candidate.ConversationId, _candidate.UrlOrProviderIdentity));

        public Task<bool> SendContinuationAsync(
            ConversationRecord candidate,
            string checkpointId,
            string continuationPacket,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                StringComparer.Ordinal.Equals(candidate.ConversationId, _candidate.ConversationId) &&
                StringComparer.Ordinal.Equals(checkpointId, _checkpointId));

        public Task<ContinuationValidationResult> ValidateAsync(ConversationRecord candidate, CancellationToken cancellationToken = default) =>
            Task.FromResult(
                StringComparer.Ordinal.Equals(candidate.ConversationId, _candidate.ConversationId)
                    ? new ContinuationValidationResult(true, "PREVALIDATED_LIVE_SEMANTICS_NO_RESEND")
                    : new ContinuationValidationResult(false, "CANDIDATE_IDENTITY_CHANGED_DURING_FINALIZATION"));

        public Task SaveCandidateAsync(ConversationRecord candidate, string checkpointId, CancellationToken cancellationToken = default)
        {
            if (!StringComparer.Ordinal.Equals(candidate.ConversationId, _candidate.ConversationId) ||
                !StringComparer.Ordinal.Equals(checkpointId, _checkpointId))
                throw new InvalidOperationException("Rollover lifecycle finalization correlation changed after semantic proof.");
            return Task.CompletedTask;
        }

        public Task CommitRolloverAsync(
            ConversationRecord predecessorArchived,
            ConversationRecord successorActive,
            string checkpointId,
            CancellationToken cancellationToken = default)
        {
            if (!StringComparer.Ordinal.Equals(checkpointId, _checkpointId))
                throw new InvalidOperationException("Rollover lifecycle checkpoint changed during finalization.");

            var successor = _candidate with
            {
                State = ConversationLifecycleState.Active,
                UrlOrProviderIdentity = successorActive.UrlOrProviderIdentity
            };
            return _commit(predecessorArchived, successor, checkpointId, cancellationToken);
        }

        public Task RecordFailedRolloverAsync(
            ConversationRecord predecessorStillActive,
            ConversationRecord? failedCandidate,
            string reason,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
