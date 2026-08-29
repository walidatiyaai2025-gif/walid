using PCCExecutive.Browser;

namespace PCCExecutive.Browser.Tests;

public sealed record ChatGptHtmlFixture(string Name, string Url, string Html, string ExpectedConversationId);

public static class ChatGptAdapterFixtures
{
    public static ChatGptHtmlFixture HealthyIdle { get; } = F("healthy-idle", "conv-a", "<textarea data-testid='composer-text-input'></textarea>");
    public static ChatGptHtmlFixture Generating { get; } = F("generating", "conv-a", "<textarea data-testid='composer-text-input'></textarea><button data-testid='stop-button'>Stop</button><article data-message-author-role='assistant'>working</article>");
    public static ChatGptHtmlFixture ResponseComplete { get; } = F("response-complete", "conv-a", "<textarea data-testid='composer-text-input'></textarea><article data-message-author-role='assistant'>done<button data-testid='copy-turn-action-button'>Copy</button></article>");
    public static ChatGptHtmlFixture ResponseCompleteWithoutActions { get; } = F("response-complete-without-actions", "conv-a", "<textarea data-testid='composer-text-input'></textarea><article data-message-author-role='assistant'>{\"ManagerEstimate\":25,\"Tasks\":[]}</article>");
    public static ChatGptHtmlFixture SlowGeneration { get; } = F("slow-generation", "conv-a", "<textarea data-testid='composer-text-input'></textarea><button data-testid='stop-button'>Stop</button><div>taking longer than expected</div>");
    public static ChatGptHtmlFixture SendingTooFast { get; } = F("sending-too-fast", "conv-a", "<textarea data-testid='composer-text-input'></textarea><div>sending too quickly - try again in a few minutes</div>");
    public static ChatGptHtmlFixture TemporaryError { get; } = F("temporary-error", "conv-a", "<textarea data-testid='composer-text-input'></textarea><div>something went wrong</div>");
    public static ChatGptHtmlFixture LoginRequired { get; } = F("login-required", "conv-a", "<button>Log in</button>");
    public static ChatGptHtmlFixture Challenge { get; } = F("challenge", "conv-a", "<div>Verify you are human</div>");
    public static ChatGptHtmlFixture PartialResponse { get; } = F("partial-response", "conv-a", "<textarea data-testid='composer-text-input'></textarea><article data-message-author-role='assistant'>partial</article><button>Continue generating</button>");
    public static ChatGptHtmlFixture ContextLimit { get; } = F("context-limit", "conv-a", "<textarea data-testid='composer-text-input'></textarea><div>This conversation has reached its limit. Start a new chat to continue.</div>");
    public static ChatGptHtmlFixture ChangedUnknownUi { get; } = F("changed-unknown-ui", "conv-a", "<div class='new-shell-v999'>unknown controls</div>");
    public static ChatGptHtmlFixture UncertainSubmission { get; } = F("uncertain-submission", "conv-a", "<textarea data-testid='composer-text-input'></textarea><div data-submit-uncertain='true'></div>");
    public static ChatGptHtmlFixture WrongConversation { get; } = new("wrong-conversation", "https://chatgpt.com/c/conv-b", "<textarea data-testid='composer-text-input'></textarea>", "conv-a");
    public static ChatGptHtmlFixture ContinuationSuccessful { get; } = F("continuation-success", "conv-next", "<textarea data-testid='composer-text-input'></textarea><article data-message-author-role='assistant'>continuation acknowledged<button data-testid='copy-turn-action-button'>Copy</button></article><div data-continuation='validated'></div>");
    public static ChatGptHtmlFixture ContinuationFailed { get; } = F("continuation-failed", "conv-next", "<textarea data-testid='composer-text-input'></textarea><div data-continuation='failed'></div>");
    public static ChatGptHtmlFixture Offline { get; } = F("offline", "conv-a", "<div>You are offline</div>");

    public static IReadOnlyList<ChatGptHtmlFixture> All { get; } = new[]
    {
        HealthyIdle, Generating, ResponseComplete, ResponseCompleteWithoutActions, SlowGeneration, SendingTooFast, TemporaryError,
        LoginRequired, Challenge, PartialResponse, ContextLimit, ChangedUnknownUi, UncertainSubmission,
        WrongConversation, ContinuationSuccessful, ContinuationFailed, Offline
    };

    private static ChatGptHtmlFixture F(string name, string conversationId, string html) => new(name, $"https://chatgpt.com/c/{conversationId}", html, conversationId);
}

public sealed class DeterministicHtmlFixtureProbe
{
    private const string Version = "fixture-semantic-v3";

    public ChatGptSemanticSnapshot Inspect(ChatGptHtmlFixture fixture)
    {
        var html = fixture.Html;
        var composer = Has(html, "composer-text-input") || Has(html, "<textarea");
        var assistant = Has(html, "data-message-author-role='assistant'") ? 1 : 0;
        var stop = Has(html, "stop-button");
        var responseAction = Has(html, "copy-turn-action-button");
        var challenge = Has(html, "verify you are human");
        var login = Has(html, ">log in<");
        var partial = Has(html, "continue generating");
        var contextLimit = Has(html, "reached its limit", "start a new chat to continue");
        var rateLimit = Has(html, "sending too quickly", "try again in a few minutes");
        var temp = Has(html, "something went wrong");
        var offline = Has(html, "you are offline");
        var slow = Has(html, "taking longer than expected");

        var input = composer ? D(InputState.Ready, .95, "fixture:composer") : D(InputState.Unknown, .10, "fixture:composer-missing");
        var auth = challenge ? D(AuthState.Challenge, .98, "fixture:challenge") : login ? D(AuthState.LoginRequired, .98, "fixture:login") : composer ? D(AuthState.Authenticated, .90, "fixture:authenticated") : D(AuthState.Unknown, .10, "fixture:auth-unknown");
        var conversation = fixture.Url.EndsWith($"/c/{fixture.ExpectedConversationId}", StringComparison.OrdinalIgnoreCase) ? D(ConversationMatch.Match, .98, "fixture:conversation-match") : D(ConversationMatch.Mismatch, .98, "fixture:conversation-mismatch");
        var health = offline ? D(PageHealth.Offline, .98, "fixture:offline") : contextLimit ? D(PageHealth.TempError, .98, "context-limit:explicit-ui") : rateLimit ? D(PageHealth.RateLimited, .98, "sending-too-quickly", "account-level:rate-limit") : temp ? D(PageHealth.TempError, .98, "fixture:temp-error") : slow ? D(PageHealth.Slow, .90, "fixture:slow") : composer ? D(PageHealth.Healthy, .90, "fixture:healthy") : D(PageHealth.Unknown, .10, "fixture:health-unknown");
        var generation = stop
            ? D(GenerationState.Generating, .98, "fixture:stop-visible")
            : assistant > 0 && composer && !partial
                ? D(GenerationState.Complete, responseAction ? .94 : .80, responseAction ? "fixture:response-actions" : "fixture:assistant-text-and-ready-composer")
                : assistant == 0 && composer
                    ? D(GenerationState.Idle, .90, "fixture:idle")
                    : D(GenerationState.Unknown, .30, "fixture:generation-unknown");
        var completeness = partial || contextLimit
            ? ResponseCompleteness.Partial
            : generation.State == GenerationState.Complete && assistant > 0 && health.State == PageHealth.Healthy
                ? ResponseCompleteness.Complete
                : assistant == 0 ? ResponseCompleteness.None : ResponseCompleteness.Unknown;
        var response = assistant > 0 ? (partial ? "partial" : "response") : null;
        return new(input, generation, auth, conversation, health, completeness, assistant, response, DateTimeOffset.UtcNow, Version);
    }

    private static SemanticDetection<T> D<T>(T state, double confidence, params string[] evidence) where T : struct, Enum => SemanticDetection<T>.Create(state, confidence, Version, evidence);
    private static bool Has(string value, params string[] needles) => needles.Any(x => value.Contains(x, StringComparison.OrdinalIgnoreCase));
}
