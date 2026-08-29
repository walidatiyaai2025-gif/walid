$ErrorActionPreference = 'Stop'

$hostPath = 'src/PCCExecutive.Browser/PlaywrightChromeRuntimeHost.cs'
$hostSource = Get-Content $hostPath -Raw

$launchPattern = 'var context = browser\.Contexts\.FirstOrDefault\(\)\s*\?\? throw new InvalidOperationException\("Chrome CDP connection has no browser context\."\);\s*var page = context\.Pages\.FirstOrDefault\(\) \?\? await context\.NewPageAsync\(\)\.ConfigureAwait\(false\);'
$launchReplacement = @'
var context = browser.Contexts.FirstOrDefault()
                ?? throw new InvalidOperationException("Chrome CDP connection has no browser context.");
            var launchPages = context.Pages.Where(x => !x.IsClosed).ToArray();
            var launchPageIndex = ChatGptPageSelectionPolicy.SelectForLaunch(launchPages.Select(x => x.Url).ToArray());
            IPage page;
            if (launchPageIndex >= 0)
            {
                page = launchPages[launchPageIndex];
            }
            else
            {
                page = await context.NewPageAsync().ConfigureAwait(false);
                await page.GotoAsync("https://chatgpt.com/", new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 20_000
                }).ConfigureAwait(false);
            }
'@
$launchRegex = [regex]::new($launchPattern, [System.Text.RegularExpressions.RegexOptions]::Singleline)
if (-not $launchRegex.IsMatch($hostSource)) { throw 'Launch page-selection pattern not found.' }
$hostSource = $launchRegex.Replace($hostSource, $launchReplacement, 1)

$recoverPattern = 'var context = browser\.Contexts\.FirstOrDefault\(\);\s*if \(context is null\) return false;\s*var page = context\.Pages\.FirstOrDefault\(\) \?\? await context\.NewPageAsync\(\)\.ConfigureAwait\(false\);'
$recoverReplacement = @'
var context = browser.Contexts.FirstOrDefault();
        if (context is null) return false;
        var recoveryPages = context.Pages.Where(x => !x.IsClosed).ToArray();
        var recoveryPageIndex = ChatGptPageSelectionPolicy.SelectForRecovery(
            recoveryPages.Select(x => x.Url).ToArray(),
            runtime.ProviderConversationIdentity);
        if (recoveryPageIndex < 0) return false;
        var page = recoveryPages[recoveryPageIndex];
'@
$recoverRegex = [regex]::new($recoverPattern, [System.Text.RegularExpressions.RegexOptions]::Singleline)
if (-not $recoverRegex.IsMatch($hostSource)) { throw 'Recovery page-selection pattern not found.' }
$hostSource = $recoverRegex.Replace($hostSource, $recoverReplacement, 1)

$getPagePattern = 'public Task<IPage\?> GetPageAsync\(string runtimeId, CancellationToken cancellationToken = default\)\s*\{\s*cancellationToken\.ThrowIfCancellationRequested\(\);\s*return Task\.FromResult\(_connections\.TryGetValue\(runtimeId, out var connection\) \? connection\.Page : null\);\s*\}'
$getPageReplacement = @'
public Task<IPage?> GetPageAsync(string runtimeId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_connections.TryGetValue(runtimeId, out var connection))
            return Task.FromResult<IPage?>(null);

        var pages = connection.Browser.Contexts
            .SelectMany(x => x.Pages)
            .Where(x => !x.IsClosed)
            .ToArray();
        if (pages.Length == 0)
            return Task.FromResult<IPage?>(null);

        var currentIndex = Array.FindIndex(pages, x => ReferenceEquals(x, connection.Page));
        var selectedIndex = ChatGptPageSelectionPolicy.SelectForLiveRefresh(
            pages.Select(x => x.Url).ToArray(),
            currentIndex);
        if (selectedIndex >= 0 && !ReferenceEquals(connection.Page, pages[selectedIndex]))
        {
            connection = connection with { Page = pages[selectedIndex] };
            _connections[runtimeId] = connection;
        }

        return Task.FromResult<IPage?>(connection.Page.IsClosed ? null : connection.Page);
    }
'@
$getPageRegex = [regex]::new($getPagePattern, [System.Text.RegularExpressions.RegexOptions]::Singleline)
if (-not $getPageRegex.IsMatch($hostSource)) { throw 'GetPageAsync pattern not found.' }
$hostSource = $getPageRegex.Replace($hostSource, $getPageReplacement, 1)
Set-Content $hostPath $hostSource -Encoding utf8NoBOM

$adapterPath = 'src/PCCExecutive.Browser/ChatGptBrowserAdapter.cs'
$adapterSource = Get-Content $adapterPath -Raw
$identityPattern = 'public async Task<string\?> GetCurrentConversationIdentityAsync\(BrowserRuntimeRecord runtime, CancellationToken cancellationToken = default\)\s*\{\s*var page = await _pages\.GetPageAsync\(runtime\.RuntimeId, cancellationToken\)\.ConfigureAwait\(false\);\s*return page is not null && Normalize\(page\.Url, out var identity\) \? identity : null;\s*\}'
$identityReplacement = @'
public async Task<string?> GetCurrentConversationIdentityAsync(BrowserRuntimeRecord runtime, CancellationToken cancellationToken = default)
    {
        var page = await _pages.GetPageAsync(runtime.RuntimeId, cancellationToken).ConfigureAwait(false);
        if (page is null) return null;
        if (Normalize(page.Url, out var identity)) return identity;

        try
        {
            await page.WaitForURLAsync(
                new Regex(@"/c/[^/?#]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
                new PageWaitForURLOptions { Timeout = 2_500 }).ConfigureAwait(false);
        }
        catch (PlaywrightException)
        {
            // The autopilot will poll again. A URL timeout is not a runtime failure and never authorizes a resend.
        }

        return Normalize(page.Url, out identity) ? identity : null;
    }
'@
$identityRegex = [regex]::new($identityPattern, [System.Text.RegularExpressions.RegexOptions]::Singleline)
if (-not $identityRegex.IsMatch($adapterSource)) { throw 'Conversation identity resolver pattern not found.' }
$adapterSource = $identityRegex.Replace($adapterSource, $identityReplacement, 1)
Set-Content $adapterPath $adapterSource -Encoding utf8NoBOM

@'
namespace PCCExecutive.Browser;

public static class ChatGptPageSelectionPolicy
{
    public static int SelectForLaunch(IReadOnlyList<string> urls)
    {
        ArgumentNullException.ThrowIfNull(urls);
        var newSurface = ChatGptIndices(urls).Where(i => IsNewSurface(urls[i])).ToArray();
        return newSurface.Length == 0 ? -1 : newSurface[^1];
    }

    public static int SelectForRecovery(IReadOnlyList<string> urls, string? expectedProviderConversationIdentity)
    {
        ArgumentNullException.ThrowIfNull(urls);
        var chatGpt = ChatGptIndices(urls).ToArray();
        if (chatGpt.Length == 0) return -1;

        if (!string.IsNullOrWhiteSpace(expectedProviderConversationIdentity) &&
            !string.Equals(expectedProviderConversationIdentity, "NEW", StringComparison.OrdinalIgnoreCase))
        {
            var expected = NormalizeIdentity(expectedProviderConversationIdentity);
            if (string.IsNullOrWhiteSpace(expected)) return -1;
            var exact = chatGpt.Where(i =>
                TryGetConversationIdentity(urls[i], out var actual) &&
                StringComparer.OrdinalIgnoreCase.Equals(actual, expected)).ToArray();
            return exact.Length == 0 ? -1 : exact[^1];
        }

        var newSurface = chatGpt.Where(i => IsNewSurface(urls[i])).ToArray();
        if (newSurface.Length > 0) return newSurface[^1];
        var stable = chatGpt.Where(i => TryGetConversationIdentity(urls[i], out _)).ToArray();
        return stable.Length == 1 ? stable[0] : -1;
    }

    public static int SelectForLiveRefresh(IReadOnlyList<string> urls, int currentIndex)
    {
        ArgumentNullException.ThrowIfNull(urls);
        var chatGpt = ChatGptIndices(urls).ToArray();
        if (chatGpt.Length == 0) return -1;

        if (currentIndex >= 0 && currentIndex < urls.Count && IsChatGpt(urls[currentIndex]))
        {
            if (TryGetConversationIdentity(urls[currentIndex], out _))
                return currentIndex;

            if (IsNewSurface(urls[currentIndex]))
            {
                var stable = chatGpt.Where(i => i != currentIndex && TryGetConversationIdentity(urls[i], out _)).ToArray();
                return stable.Length == 1 ? stable[0] : currentIndex;
            }
        }

        var newSurface = chatGpt.Where(i => IsNewSurface(urls[i])).ToArray();
        if (newSurface.Length > 0) return newSurface[^1];
        var stableFallback = chatGpt.Where(i => TryGetConversationIdentity(urls[i], out _)).ToArray();
        return stableFallback.Length == 1 ? stableFallback[0] : -1;
    }

    public static bool TryGetConversationIdentity(string? value, out string identity)
    {
        identity = string.Empty;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !IsChatGptHost(uri.Host))
            return false;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i + 1 < segments.Length; i++)
        {
            if (!string.Equals(segments[i], "c", StringComparison.OrdinalIgnoreCase)) continue;
            identity = segments[i + 1];
            return !string.IsNullOrWhiteSpace(identity);
        }
        return false;
    }

    private static IEnumerable<int> ChatGptIndices(IReadOnlyList<string> urls) =>
        Enumerable.Range(0, urls.Count).Where(i => IsChatGpt(urls[i]));

    private static bool IsChatGpt(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && IsChatGptHost(uri.Host);

    private static bool IsNewSurface(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !IsChatGptHost(uri.Host)) return false;
        var path = uri.AbsolutePath.Trim('/');
        return path.Length == 0 || string.Equals(path, "new", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsChatGptHost(string host) =>
        string.Equals(host, "chatgpt.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".chatgpt.com", StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeIdentity(string value)
    {
        if (TryGetConversationIdentity(value, out var fromUrl)) return fromUrl;
        var trimmed = value.Trim().Trim('/');
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }
}
'@ | Set-Content 'src/PCCExecutive.Browser/ChatGptPageSelectionPolicy.cs' -Encoding utf8NoBOM

@'
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
'@ | Set-Content 'tests/PCCExecutive.Browser.Tests/ChatGptPageSelectionPolicyTests.cs' -Encoding utf8NoBOM

if ((Get-Content $hostPath -Raw) -notmatch 'SelectForLiveRefresh') { throw 'Host live page rebinding was not applied.' }
if ((Get-Content $adapterPath -Raw) -notmatch 'PageWaitForURLOptions') { throw 'Adapter URL stabilization wait was not applied.' }
