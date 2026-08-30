using System.Text.RegularExpressions;

namespace PCCExecutive.Browser;

public sealed record ChatGptConversationEvidenceCandidate(
    string ConversationIdentity,
    IReadOnlyList<string> UserMessages);

public static class ChatGptConversationEvidenceMatcher
{
    public static string? ResolveUniqueIdentity(
        IReadOnlyList<ChatGptConversationEvidenceCandidate> candidates,
        string? exactUserPrompt,
        IReadOnlyList<string>? requiredUserMessageFragments)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var exact = string.IsNullOrWhiteSpace(exactUserPrompt) ? null : Normalize(exactUserPrompt);
        var fragments = (requiredUserMessageFragments ?? Array.Empty<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (exact is null && fragments.Length < 2) return null;

        var identities = candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.ConversationIdentity))
            .Where(candidate => candidate.UserMessages.Any(message => MessageMatches(message, exact, fragments)))
            .Select(candidate => candidate.ConversationIdentity.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return identities.Length == 1 ? identities[0] : null;
    }

    private static bool MessageMatches(string message, string? exact, IReadOnlyList<string> fragments)
    {
        var normalized = Normalize(message);
        if (exact is not null && StringComparer.Ordinal.Equals(normalized, exact)) return true;
        return fragments.Count >= 2 && fragments.All(fragment => normalized.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    private static string Normalize(string value) =>
        Regex.Replace((value ?? string.Empty).Replace('\u00a0', ' '), @"\s+", " ").Trim();
}
