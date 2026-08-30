using PCCExecutive.Browser;
using Xunit;

namespace PCCExecutive.Browser.Tests;

public sealed class ConversationIdentityEvidenceTests
{
    [Fact]
    public void Exact_prompt_selects_the_unique_matching_conversation()
    {
        var candidates = new[]
        {
            new ChatGptConversationEvidenceCandidate("old", new[] { "unrelated" }),
            new ChatGptConversationEvidenceCandidate("expected", new[] { "PROJECT_RUN: abc\nReturn JSON only." })
        };

        var identity = ChatGptConversationEvidenceMatcher.ResolveUniqueIdentity(
            candidates,
            "PROJECT_RUN: abc\r\nReturn JSON only.",
            null);

        Assert.Equal("expected", identity);
    }

    [Fact]
    public void Durable_manager_fragments_disambiguate_among_multiple_open_chatgpt_tabs()
    {
        var candidates = new[]
        {
            new ChatGptConversationEvidenceCandidate("old-run", new[] { "PROJECT_RUN: old REPOSITORY: owner/repo Return one JSON object only with ManagerEstimate" }),
            new ChatGptConversationEvidenceCandidate("current-run", new[] { "PROJECT_RUN: current REPOSITORY: owner/repo PCC_SOURCE_SHA: deadbeef Return one JSON object only with ManagerEstimate" })
        };

        var identity = ChatGptConversationEvidenceMatcher.ResolveUniqueIdentity(
            candidates,
            null,
            new[] { "PROJECT_RUN: current", "REPOSITORY: owner/repo", "PCC_SOURCE_SHA: deadbeef", "Return one JSON object only with ManagerEstimate" });

        Assert.Equal("current-run", identity);
    }

    [Fact]
    public void Same_conversation_open_twice_is_not_treated_as_identity_ambiguity()
    {
        var candidates = new[]
        {
            new ChatGptConversationEvidenceCandidate("same", new[] { "PROJECT_RUN: current REPOSITORY: owner/repo" }),
            new ChatGptConversationEvidenceCandidate("same", new[] { "PROJECT_RUN: current REPOSITORY: owner/repo" })
        };

        var identity = ChatGptConversationEvidenceMatcher.ResolveUniqueIdentity(
            candidates,
            null,
            new[] { "PROJECT_RUN: current", "REPOSITORY: owner/repo" });

        Assert.Equal("same", identity);
    }

    [Fact]
    public void Different_conversations_with_the_same_evidence_are_refused_as_ambiguous()
    {
        var candidates = new[]
        {
            new ChatGptConversationEvidenceCandidate("first", new[] { "PROJECT_RUN: current REPOSITORY: owner/repo" }),
            new ChatGptConversationEvidenceCandidate("second", new[] { "PROJECT_RUN: current REPOSITORY: owner/repo" })
        };

        var identity = ChatGptConversationEvidenceMatcher.ResolveUniqueIdentity(
            candidates,
            null,
            new[] { "PROJECT_RUN: current", "REPOSITORY: owner/repo" });

        Assert.Null(identity);
    }
}
