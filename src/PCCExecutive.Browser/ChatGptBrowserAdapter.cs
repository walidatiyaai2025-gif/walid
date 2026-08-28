using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace PCCExecutive.Browser;

public sealed class WrongChatGuard
{
    public WrongChatDecision Evaluate(BrowserRuntimeRecord runtime, BrowserDispatchExpectation expected, ChatGptSemanticSnapshot snapshot)
    {
        var evidence = new List<string>();
        if (!StringComparer.Ordinal.Equals(runtime.ProjectRunId, expected.ProjectRunId)) return Deny("PROJECT_RUN_MISMATCH");
        evidence.Add("project-run:match");
        if (!StringComparer.Ordinal.Equals(runtime.LogicalAgentId, expected.LogicalAgentId)) return Deny("LOGICAL_AGENT_MISMATCH");
        evidence.Add("logical-agent:match");
        if (string.IsNullOrWhiteSpace(runtime.TaskId)) return new(false, "TASK_BINDING_UNKNOWN", evidence);
        if (!StringComparer.Ordinal.Equals(runtime.TaskId, expected.TaskId)) return Deny("TASK_MISMATCH");
        evidence.Add("task:match");
        if (string.IsNullOrWhiteSpace(runtime.ConversationIdentity)) return new(false, "CONVERSATION_BINDING_UNKNOWN", evidence);
        if (!StringComparer.Ordinal.Equals(runtime.ConversationIdentity, expected.ConversationIdentity)) return Deny("CONVERSATION_IDENTITY_MISMATCH");
        evidence.Add("conversation-binding:match");
        if (string.IsNullOrWhiteSpace(runtime.ProviderConversationIdentity)) return new(false, "PROVIDER_CONVERSATION_BINDING_UNKNOWN", evidence);
        if (!StringComparer.OrdinalIgnoreCase.Equals(runtime.ProviderConversationIdentity, expected.ProviderConversationIdentity)) return Deny("PROVIDER_CONVERSATION_BINDING_MISMATCH");
        evidence.Add("provider-conversation-binding:match");

        if (Uncertain(snapshot.Conversation)) return new(false, "BROWSER_ADAPTER_UNCERTAIN", evidence.Concat(snapshot.Conversation.Evidence).ToArray());
        if (snapshot.Conversation.State == ConversationMatch.Mismatch) return new(false, "PROVIDER_CONVERSATION_MISMATCH", evidence.Concat(snapshot.Conversation.Evidence).ToArray());
        if (Uncertain(snapshot.Input)) return new(false, "BROWSER_ADAPTER_UNCERTAIN", evidence.Concat(snapshot.Input.Evidence).ToArray());
        if (snapshot.Input.State != InputState.Ready) return new(false, "INPUT_NOT_READY", evidence.Concat(snapshot.Input.Evidence).ToArray());
        if (Uncertain(snapshot.Auth)) return new(false, "BROWSER_ADAPTER_UNCERTAIN", evidence.Concat(snapshot.Auth.Evidence).ToArray());
        if (snapshot.Auth.State == AuthState.LoginRequired) return new(false, "LOGIN_REQUIRED", evidence.Concat(snapshot.Auth.Evidence).ToArray());
        if (snapshot.Auth.State == AuthState.Challenge) return new(false, "CHALLENGE", evidence.Concat(snapshot.Auth.Evidence).ToArray());
        if (Uncertain(snapshot.Health)) return new(false, "BROWSER_ADAPTER_UNCERTAIN", evidence.Concat(snapshot.Health.Evidence).ToArray());
        if (snapshot.Health.State != PageHealth.Healthy) return new(false, $"PAGE_{snapshot.Health.State.ToString().ToUpperInvariant()}", evidence.Concat(snapshot.Health.Evidence).ToArray());
        if (Uncertain(snapshot.Generation)) return new(false, "BROWSER_ADAPTER_UNCERTAIN", evidence.Concat(snapshot.Generation.Evidence).ToArray());
        if (snapshot.Generation.State == GenerationState.Generating) return new(false, "GENERATION_ACTIVE", evidence.Concat(snapshot.Generation.Evidence).ToArray());
        evidence.Add("wrong-chat-guard:proven-safe");
        return new(true, "READY_TO_SEND", evidence);

        WrongChatDecision Deny(string reason) => new(false, reason, new[] { $"runtime:{runtime.RuntimeId}", $"expected-project-run:{expected.ProjectRunId}", $"expected-agent:{expected.LogicalAgentId}", $"expected-task:{expected.TaskId}", $"expected-conversation:{expected.ConversationIdentity}", $"adapter:{snapshot.AdapterVersion}" });
    }

    private static bool Uncertain<T>(SemanticDetection<T> detection) where T : struct, Enum =>
        detection.Confidence < 0.60 || string.Equals(detection.State.ToString(), "Unknown", StringComparison.Ordinal);
}

public sealed class PlaywrightChatGptBrowserAdapter : IChatGptBrowserAdapter
{
    public const string CurrentAdapterVersion = "chatgpt-web-semantic-v1";
    private const string ComposerSelector = "textarea, [contenteditable='true'][role='textbox'], [contenteditable='true'][data-lexical-editor='true']";
    private const string AssistantSelector = "[data-message-author-role='assistant']";
    private const string StopSelector = "button[aria-label*='Stop' i], button:has-text('Stop generating')";
    private const string LoginSelector = "a[href*='/auth/login'], button:has-text('Log in'), button:has-text('Sign in')";
    private const string ContinueSelector = "button:has-text('Continue generating'), button:has-text('Continue')";
    private readonly IPlaywrightPageProvider _pages;

    public PlaywrightChatGptBrowserAdapter(IPlaywrightPageProvider pages) => _pages = pages;
    public string AdapterVersion => CurrentAdapterVersion;

    public async Task<ChatGptSemanticSnapshot> InspectAsync(BrowserRuntimeRecord runtime, BrowserDispatchExpectation expectation, CancellationToken cancellationToken = default)
    {
        var page = await _pages.GetPageAsync(runtime.RuntimeId, cancellationToken).ConfigureAwait(false);
        if (page is null) return Unknown("playwright-page:missing");
        try
        {
            var body = await BodyAsync(page).ConfigureAwait(false);
            var composer = await VisibleAsync(page, ComposerSelector).ConfigureAwait(false);
            var assistantCount = await page.Locator(AssistantSelector).CountAsync().ConfigureAwait(false);
            var input = composer is null ? D(InputState.Unknown, .2, "composer:not-found") : await composer.IsEnabledAsync().ConfigureAwait(false) ? D(InputState.Ready, .92, "composer:visible", "composer:enabled") : D(InputState.Disabled, .92, "composer:disabled");
            var auth = Contains(body, "verify you are human", "checking your browser", "security challenge", "captcha") ? D(AuthState.Challenge, .92, "challenge-text:present") : page.Url.Contains("/auth/login", StringComparison.OrdinalIgnoreCase) || await HasVisibleAsync(page, LoginSelector).ConfigureAwait(false) ? D(AuthState.LoginRequired, .92, "login-ui:present") : composer is not null ? D(AuthState.Authenticated, .75, "composer:authenticated-surface-present") : D(AuthState.Unknown, .3, "auth:unproven");
            var generation = await HasVisibleAsync(page, StopSelector).ConfigureAwait(false) ? D(GenerationState.Generating, .92, "stop-generation-control:visible") : composer is not null && assistantCount > 0 ? D(GenerationState.Complete, .75, "assistant-message:present", "stop-generation-control:absent") : composer is not null ? D(GenerationState.Idle, .75, "composer:visible", "stop-generation-control:absent") : D(GenerationState.Unknown, .25, "generation:unproven");
            var conversation = DetectConversation(page.Url, expectation.ProviderConversationIdentity);
            var health = DetectHealth(page.Url, body, composer is not null, auth.State);
            var completeness = assistantCount == 0 ? ResponseCompleteness.None : await HasVisibleAsync(page, ContinueSelector).ConfigureAwait(false) || Contains(body, "network error") && generation.State != GenerationState.Generating ? ResponseCompleteness.Partial : generation.State == GenerationState.Complete ? ResponseCompleteness.Complete : ResponseCompleteness.Unknown;
            var response = assistantCount > 0 ? await LastAssistantAsync(page, assistantCount).ConfigureAwait(false) : null;
            return new(input, generation, auth, conversation, health, completeness, assistantCount, response, DateTimeOffset.UtcNow, AdapterVersion);
        }
        catch (PlaywrightException ex) { return Unknown($"playwright-inspection-error:{ex.GetType().Name}"); }
    }

    public async Task<AdapterSubmissionResult> SubmitAsync(BrowserRuntimeRecord runtime, BrowserDispatchExpectation expectation, string prompt, CancellationToken cancellationToken = default)
    {
        var page = await _pages.GetPageAsync(runtime.RuntimeId, cancellationToken).ConfigureAwait(false);
        if (page is null) return new(false, false, false, "BROWSER_PAGE_MISSING", new[] { "submission:not-triggered" });
        var triggered = false;
        try
        {
            var before = await InspectAsync(runtime, expectation, cancellationToken).ConfigureAwait(false);
            if (before.Input.State != InputState.Ready || before.Auth.State != AuthState.Authenticated || before.Conversation.State != ConversationMatch.Match)
                return new(false, false, false, "PRE_SUBMIT_STATE_NOT_PROVEN", new[] { "submission:not-triggered", $"adapter:{AdapterVersion}" });
            var composer = await VisibleAsync(page, ComposerSelector).ConfigureAwait(false);
            if (composer is null) return new(false, false, false, "COMPOSER_NOT_FOUND", new[] { "submission:not-triggered" });
            await composer.FillAsync(prompt).ConfigureAwait(false);
            triggered = true;
            await composer.PressAsync("Enter").ConfigureAwait(false);
            await page.WaitForTimeoutAsync(700).ConfigureAwait(false);
            var after = await InspectAsync(runtime, expectation, cancellationToken).ConfigureAwait(false);
            var evidence = new[] { "submission:enter-triggered", $"assistant-count-before:{before.AssistantMessageCount}", $"assistant-count-after:{after.AssistantMessageCount}", $"generation-after:{after.Generation.State}", $"health-after:{after.Health.State}" };
            if (after.Generation.State == GenerationState.Generating || after.AssistantMessageCount > before.AssistantMessageCount) return new(true, true, false, "SUBMISSION_PROVEN", evidence);
            if (after.Health.State is PageHealth.RateLimited or PageHealth.TempError or PageHealth.Offline || after.Auth.State is AuthState.LoginRequired or AuthState.Challenge || after.Input.State == InputState.Unknown || after.Conversation.State == ConversationMatch.Unknown)
                return new(true, false, true, "SUBMITTED_UNKNOWN", evidence);
            var value = await ComposerValueAsync(composer).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(value) ? new(true, true, false, "SUBMISSION_PROVEN_COMPOSER_CLEARED", evidence) : new(true, false, true, "SUBMITTED_UNKNOWN", evidence);
        }
        catch (PlaywrightException ex)
        {
            return triggered ? new(true, false, true, "SUBMITTED_UNKNOWN", new[] { $"playwright:{ex.GetType().Name}", "submission-triggered-before-error" }) : new(false, false, false, "SUBMISSION_NOT_TRIGGERED", new[] { $"playwright:{ex.GetType().Name}" });
        }
    }

    private ChatGptSemanticSnapshot Unknown(string evidence) => new(D(InputState.Unknown, 0, evidence), D(GenerationState.Unknown, 0, evidence), D(AuthState.Unknown, 0, evidence), D(ConversationMatch.Unknown, 0, evidence), D(PageHealth.Unknown, 0, evidence), ResponseCompleteness.Unknown, 0, null, DateTimeOffset.UtcNow, AdapterVersion);
    private static SemanticDetection<T> D<T>(T state, double confidence, params string[] evidence) where T : struct, Enum => SemanticDetection<T>.Create(state, confidence, CurrentAdapterVersion, evidence);

    private static SemanticDetection<ConversationMatch> DetectConversation(string currentUrl, string expectedIdentity)
    {
        if (!Normalize(currentUrl, out var actual) || !Normalize(expectedIdentity, out var expected)) return D(ConversationMatch.Unknown, .3, "provider-conversation:unparseable");
        return StringComparer.OrdinalIgnoreCase.Equals(actual, expected) ? D(ConversationMatch.Match, .92, $"provider-conversation:{actual}") : D(ConversationMatch.Mismatch, .92, $"expected:{expected}", $"actual:{actual}");
    }

    private static SemanticDetection<PageHealth> DetectHealth(string url, string body, bool composerVisible, AuthState auth)
    {
        if (url.StartsWith("chrome-error://", StringComparison.OrdinalIgnoreCase) || Contains(body, "you are offline", "no internet", "network connection was lost")) return D(PageHealth.Offline, .92, "offline-evidence:present");
        if (Contains(body, "too many requests", "rate limit", "try again in a few minutes", "sending too quickly", "you've reached")) return D(PageHealth.RateLimited, .92, "rate-limit-guidance:present");
        if (Contains(body, "something went wrong", "temporary error", "failed to load", "there was an error generating")) return D(PageHealth.TempError, .92, "temporary-error:present");
        if (Contains(body, "taking longer than expected", "still working on this")) return D(PageHealth.Slow, .75, "slow-guidance:present");
        return auth == AuthState.Authenticated && composerVisible ? D(PageHealth.Healthy, .75, "authenticated-composer:healthy-surface") : D(PageHealth.Unknown, .3, "page-health:unproven");
    }

    private static async Task<ILocator?> VisibleAsync(IPage page, string selector)
    {
        var locator = page.Locator(selector); var count = await locator.CountAsync().ConfigureAwait(false);
        for (var i = 0; i < count; i++) if (await locator.Nth(i).IsVisibleAsync().ConfigureAwait(false)) return locator.Nth(i);
        return null;
    }
    private static async Task<bool> HasVisibleAsync(IPage page, string selector) => await VisibleAsync(page, selector).ConfigureAwait(false) is not null;
    private static async Task<string> BodyAsync(IPage page) { try { var text = await page.Locator("body").InnerTextAsync().ConfigureAwait(false); return text.Length <= 50_000 ? text : text[..50_000]; } catch (PlaywrightException) { return string.Empty; } }
    private static async Task<string?> LastAssistantAsync(IPage page, int count) { try { var text = await page.Locator(AssistantSelector).Nth(count - 1).InnerTextAsync().ConfigureAwait(false); return text.Length <= 100_000 ? text : text[..100_000]; } catch (PlaywrightException) { return null; } }
    private static async Task<string?> ComposerValueAsync(ILocator composer) { try { var tag = await composer.EvaluateAsync<string>("el => el.tagName.toLowerCase()").ConfigureAwait(false); return tag is "textarea" or "input" ? await composer.InputValueAsync().ConfigureAwait(false) : await composer.InnerTextAsync().ConfigureAwait(false); } catch (PlaywrightException) { return null; } }
    private static bool Contains(string source, params string[] needles) => needles.Any(x => source.Contains(x, StringComparison.OrdinalIgnoreCase));
    private static bool Normalize(string value, out string id)
    {
        id = string.Empty;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri)) { var m = Regex.Match(uri.AbsolutePath, @"/c/([^/?#]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant); if (!m.Success) return false; id = m.Groups[1].Value.Trim(); return id.Length > 0; }
        var direct = Regex.Match(value ?? string.Empty, @"(?:^|/c/)([A-Za-z0-9_-]{6,})$", RegexOptions.CultureInvariant); if (!direct.Success) return false; id = direct.Groups[1].Value.Trim(); return id.Length > 0;
    }
}
