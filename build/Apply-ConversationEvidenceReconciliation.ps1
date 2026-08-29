$ErrorActionPreference = 'Stop'

function Replace-ExactlyOnce {
    param(
        [string]$Path,
        [string]$Pattern,
        [string]$Replacement,
        [string]$Label
    )
    $source = Get-Content $Path -Raw
    $regex = [regex]::new($Pattern, [System.Text.RegularExpressions.RegexOptions]::Singleline)
    $matches = $regex.Matches($source)
    if ($matches.Count -ne 1) {
        throw "$Label expected exactly one match in $Path but found $($matches.Count)."
    }
    $updated = $regex.Replace($source, $Replacement, 1)
    Set-Content $Path $updated -Encoding utf8NoBOM
}

$hostPath = 'src/PCCExecutive.Browser/PlaywrightChromeRuntimeHost.cs'
Replace-ExactlyOnce -Path $hostPath -Label 'page catalog interface' -Pattern @'
public interface IPlaywrightPageProvider\s*\{\s*Task<IPage\?> GetPageAsync\(string runtimeId, CancellationToken cancellationToken = default\);\s*\}
'@ -Replacement @'
public interface IPlaywrightPageProvider
{
    Task<IPage?> GetPageAsync(string runtimeId, CancellationToken cancellationToken = default);
}

public interface IPlaywrightPageCatalog
{
    Task<IReadOnlyList<IPage>> GetPagesAsync(string runtimeId, CancellationToken cancellationToken = default);
}
'@

Replace-ExactlyOnce -Path $hostPath -Label 'host catalog implementation' -Pattern 'public sealed class PlaywrightChromeRuntimeHost : IBrowserRuntimeHost, IPlaywrightPageProvider' -Replacement 'public sealed class PlaywrightChromeRuntimeHost : IBrowserRuntimeHost, IPlaywrightPageProvider, IPlaywrightPageCatalog'

Replace-ExactlyOnce -Path $hostPath -Label 'GetPagesAsync insertion' -Pattern @'
    public Task<IPage\?> GetPageAsync\(string runtimeId, CancellationToken cancellationToken = default\)\s*\{.*?\n    \}\s*\n\s*    public string ResolvePersistentProfilePath
'@ -Replacement @'
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

    public Task<IReadOnlyList<IPage>> GetPagesAsync(string runtimeId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_connections.TryGetValue(runtimeId, out var connection))
            return Task.FromResult<IReadOnlyList<IPage>>(Array.Empty<IPage>());

        IReadOnlyList<IPage> pages = connection.Browser.Contexts
            .SelectMany(x => x.Pages)
            .Where(x => !x.IsClosed)
            .ToArray();
        return Task.FromResult(pages);
    }

    public string ResolvePersistentProfilePath
'@

$adapterPath = 'src/PCCExecutive.Browser/ChatGptBrowserAdapter.cs'
Replace-ExactlyOnce -Path $adapterPath -Label 'resolver interface' -Pattern 'namespace PCCExecutive\.Browser;\s*\n\s*public sealed class WrongChatGuard' -Replacement @'
namespace PCCExecutive.Browser;

public interface IConversationIdentityEvidenceResolver
{
    Task<string?> ResolveConversationIdentityAsync(
        BrowserRuntimeRecord runtime,
        string? exactUserPrompt,
        IReadOnlyList<string>? requiredUserMessageFragments,
        CancellationToken cancellationToken = default);
}

public sealed class WrongChatGuard
'@

Replace-ExactlyOnce -Path $adapterPath -Label 'adapter resolver implementation' -Pattern 'public sealed class PlaywrightChatGptBrowserAdapter : IChatGptBrowserAdapter, IPhysicalSubmitAuthorizationAdapter' -Replacement 'public sealed class PlaywrightChatGptBrowserAdapter : IChatGptBrowserAdapter, IPhysicalSubmitAuthorizationAdapter, IConversationIdentityEvidenceResolver'
Replace-ExactlyOnce -Path $adapterPath -Label 'user selector' -Pattern '    private const string AssistantSelector = "\[data-message-author-role=''assistant''\]";' -Replacement @'
    private const string AssistantSelector = "[data-message-author-role='assistant']";
    private const string UserSelector = "[data-message-author-role='user']";
'@

Replace-ExactlyOnce -Path $adapterPath -Label 'evidence resolver method' -Pattern @'
    public async Task<ChatGptSemanticSnapshot> InspectAsync
'@ -Replacement @'
    public async Task<string?> ResolveConversationIdentityAsync(
        BrowserRuntimeRecord runtime,
        string? exactUserPrompt,
        IReadOnlyList<string>? requiredUserMessageFragments,
        CancellationToken cancellationToken = default)
    {
        var direct = await GetCurrentConversationIdentityAsync(runtime, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(direct)) return direct;

        IReadOnlyList<IPage> pages;
        if (_pages is IPlaywrightPageCatalog catalog)
            pages = await catalog.GetPagesAsync(runtime.RuntimeId, cancellationToken).ConfigureAwait(false);
        else
        {
            var current = await _pages.GetPageAsync(runtime.RuntimeId, cancellationToken).ConfigureAwait(false);
            pages = current is null ? Array.Empty<IPage>() : new[] { current };
        }

        var candidates = new List<ChatGptConversationEvidenceCandidate>();
        foreach (var page in pages.Where(x => !x.IsClosed))
        {
            if (!Normalize(page.Url, out var providerIdentity)) continue;
            try
            {
                var messages = await page.Locator(UserSelector).AllInnerTextsAsync().ConfigureAwait(false);
                candidates.Add(new ChatGptConversationEvidenceCandidate(providerIdentity, messages.ToArray()));
            }
            catch (PlaywrightException)
            {
                // A transient DOM race is non-authoritative. Keep reconciliation pending instead of guessing.
            }
        }

        return ChatGptConversationEvidenceMatcher.ResolveUniqueIdentity(
            candidates,
            exactUserPrompt,
            requiredUserMessageFragments);
    }

    public async Task<ChatGptSemanticSnapshot> InspectAsync
'@

@'
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
'@ | Set-Content 'src/PCCExecutive.Browser/ChatGptConversationEvidenceMatcher.cs' -Encoding utf8NoBOM

$dispatchPath = 'src/PCCExecutive.Browser/DispatchAndResilience.cs'
Replace-ExactlyOnce -Path $dispatchPath -Label 'post-submit identity evidence resolution' -Pattern 'var providerIdentity = await _adapter\.GetCurrentConversationIdentityAsync\(runtime, cancellationToken\)\.ConfigureAwait\(false\);' -Replacement @'
var providerIdentity = _adapter is IConversationIdentityEvidenceResolver resolver
                    ? await resolver.ResolveConversationIdentityAsync(runtime, request.Prompt, null, cancellationToken).ConfigureAwait(false)
                    : await _adapter.GetCurrentConversationIdentityAsync(runtime, cancellationToken).ConfigureAwait(false);
'@

$gatewayPath = 'src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs'
Replace-ExactlyOnce -Path $gatewayPath -Label 'manager reconciliation evidence resolution' -Pattern '            var providerIdentity = await _browserAdapter\.GetCurrentConversationIdentityAsync\(runtime, cancellationToken\)\.ConfigureAwait\(false\);' -Replacement @'
            var managerIdentityFragments = new List<string>
            {
                $"PROJECT_RUN: {run.Id}",
                $"REPOSITORY: {_projectRepository}",
                "Return one JSON object only with ManagerEstimate"
            };
            if (_managerBaseline is not null && !string.IsNullOrWhiteSpace(_managerBaseline.PccSourceSha))
                managerIdentityFragments.Add($"PCC_SOURCE_SHA: {_managerBaseline.PccSourceSha}");

            var providerIdentity = _browserAdapter is IConversationIdentityEvidenceResolver resolver
                ? await resolver.ResolveConversationIdentityAsync(runtime, null, managerIdentityFragments, cancellationToken).ConfigureAwait(false)
                : await _browserAdapter.GetCurrentConversationIdentityAsync(runtime, cancellationToken).ConfigureAwait(false);
'@

@'
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
'@ | Set-Content 'tests/PCCExecutive.Browser.Tests/ConversationIdentityEvidenceTests.cs' -Encoding utf8NoBOM

Write-Host 'Conversation evidence reconciliation hotfix applied.'
