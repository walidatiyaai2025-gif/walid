using PCCExecutive.Browser;
using Xunit;

namespace PCCExecutive.Browser.Tests;

public sealed class ChatGptPageSelectionPolicyTests
{
    [Fact]
    public void Launch_selects_new_chat_surface_instead_of_an_old_restored_conversation()
    {
        var urls = new[]
        {
            "https://chatgpt.com/c/old-conversation",
            "https://example.com/",
            "https://chatgpt.com/"
        };

        Assert.Equal(2, ChatGptPageSelectionPolicy.SelectForLaunch(urls));
    }

    [Fact]
    public void Live_refresh_promotes_the_only_stable_conversation_created_from_new_chat()
    {
        var urls = new[]
        {
            "https://chatgpt.com/",
            "https://chatgpt.com/c/manager-conversation"
        };

        Assert.Equal(1, ChatGptPageSelectionPolicy.SelectForLiveRefresh(urls, 0));
    }

    [Fact]
    public void Live_refresh_refuses_to_guess_when_multiple_stable_conversations_are_ambiguous()
    {
        var urls = new[]
        {
            "https://chatgpt.com/",
            "https://chatgpt.com/c/first",
            "https://chatgpt.com/c/second"
        };

        Assert.Equal(0, ChatGptPageSelectionPolicy.SelectForLiveRefresh(urls, 0));
    }

    [Fact]
    public void Recovery_requires_exact_stable_provider_identity_when_it_is_known()
    {
        var urls = new[]
        {
            "https://chatgpt.com/c/wrong",
            "https://chatgpt.com/c/expected"
        };

        Assert.Equal(1, ChatGptPageSelectionPolicy.SelectForRecovery(urls, "expected"));
        Assert.Equal(-1, ChatGptPageSelectionPolicy.SelectForRecovery(urls, "missing"));
    }

    [Fact]
    public void Recovery_of_pending_new_identity_accepts_only_a_unique_stable_conversation()
    {
        Assert.Equal(0, ChatGptPageSelectionPolicy.SelectForRecovery(
            new[] { "https://chatgpt.com/c/only" },
            "NEW"));
        Assert.Equal(-1, ChatGptPageSelectionPolicy.SelectForRecovery(
            new[] { "https://chatgpt.com/c/one", "https://chatgpt.com/c/two" },
            "NEW"));
    }
}
