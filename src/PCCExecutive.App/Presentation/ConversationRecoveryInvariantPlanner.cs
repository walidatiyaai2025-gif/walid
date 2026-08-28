using PCCExecutive.Browser;

namespace PCCExecutive.App.Presentation;

public sealed record ConversationRecoveryInvariantPlan(
    string? ActiveConversationId,
    IReadOnlyList<string> ArchiveConversationIds,
    IReadOnlyList<string> RetireRuntimeIds,
    bool PromoteSelectedConversation,
    bool UpdateLogicalSession);

public static class ConversationRecoveryInvariantPlanner
{
    public static ConversationRecoveryInvariantPlan Build(
        IReadOnlyList<ConversationRecord> conversations,
        string? durableCurrentConversationId,
        IReadOnlyList<BrowserRuntimeRecord> runtimes)
    {
        if (conversations.Count == 0)
            return new(null, Array.Empty<string>(), Array.Empty<string>(), false, false);

        var active = conversations
            .Where(x => x.State == ConversationLifecycleState.Active)
            .OrderByDescending(x => x.Sequence)
            .ThenByDescending(x => x.CreatedAt)
            .ToArray();

        ConversationRecord? selected;
        var promote = false;
        if (active.Length == 1)
        {
            selected = active[0];
        }
        else if (active.Length > 1)
        {
            selected = active.FirstOrDefault(x => StringComparer.Ordinal.Equals(x.ConversationId, durableCurrentConversationId)) ?? active[0];
        }
        else
        {
            var recoverable = conversations
                .Where(x => x.State is not ConversationLifecycleState.Candidate and not ConversationLifecycleState.FailedCandidate)
                .OrderByDescending(x => x.Sequence)
                .ThenByDescending(x => x.CreatedAt)
                .ToArray();
            selected = recoverable.FirstOrDefault(x => StringComparer.Ordinal.Equals(x.ConversationId, durableCurrentConversationId))
                ?? recoverable.FirstOrDefault(x => runtimes.Any(r => !r.IsArchived && r.State is not BrowserSessionState.Killed and not BrowserSessionState.Archived && StringComparer.Ordinal.Equals(r.ConversationIdentity, x.ConversationId)))
                ?? recoverable.FirstOrDefault();
            promote = selected is not null;
        }

        if (selected is null)
            return new(null, Array.Empty<string>(), Array.Empty<string>(), false, false);

        var archive = active
            .Where(x => !StringComparer.Ordinal.Equals(x.ConversationId, selected.ConversationId))
            .Select(x => x.ConversationId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var retiredConversationIds = conversations
            .Where(x => !StringComparer.Ordinal.Equals(x.ConversationId, selected.ConversationId) &&
                        (x.State is ConversationLifecycleState.Archived or ConversationLifecycleState.FailedCandidate || archive.Contains(x.ConversationId, StringComparer.Ordinal)))
            .Select(x => x.ConversationId)
            .ToHashSet(StringComparer.Ordinal);

        var retireRuntimeIds = runtimes
            .Where(x => !x.IsArchived && x.State is not BrowserSessionState.Killed and not BrowserSessionState.Archived &&
                        !StringComparer.Ordinal.Equals(x.ConversationIdentity, selected.ConversationId) &&
                        x.ConversationIdentity is not null && retiredConversationIds.Contains(x.ConversationIdentity))
            .Select(x => x.RuntimeId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return new(
            selected.ConversationId,
            archive,
            retireRuntimeIds,
            promote,
            !StringComparer.Ordinal.Equals(durableCurrentConversationId, selected.ConversationId));
    }
}
