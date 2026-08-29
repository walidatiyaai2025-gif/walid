from pathlib import Path

adapter_path = Path('src/PCCExecutive.Browser/ChatGptBrowserAdapter.cs')
fixtures_path = Path('tests/PCCExecutive.Browser.Tests/ChatGptAdapterFixtures.cs')
regression_path = Path('tests/PCCExecutive.Browser.Tests/ChatGptCompletionRegressionTests.cs')

adapter = adapter_path.read_text(encoding='utf-8')
fixtures = fixtures_path.read_text(encoding='utf-8')
regression = regression_path.read_text(encoding='utf-8')

# Expand explicit role selectors to the current ChatGPT data-turn contract.
adapter = adapter.replace(
    '    private const string AssistantSelector = "[data-message-author-role=\'assistant\']";\n    private const string UserSelector = "[data-message-author-role=\'user\']";',
    '    private const string AssistantSelector = "[data-message-author-role=\'assistant\'], [data-turn=\'assistant\']";\n    private const string UserSelector = "[data-message-author-role=\'user\'], [data-turn=\'user\']";')

# Bind semantic inspection to the expected conversation tab rather than whichever ChatGPT tab
# happened to be current in the browser host.
adapter = adapter.replace(
    '        var page = await _pages.GetPageAsync(runtime.RuntimeId, cancellationToken).ConfigureAwait(false);\n        if (page is null) return null;\n        if (Normalize(page.Url, out var identity)) return identity;',
    '        var page = await ExpectedPageAsync(runtime, runtime.ProviderConversationIdentity, cancellationToken).ConfigureAwait(false);\n        if (page is null) return null;\n        if (Normalize(page.Url, out var identity)) return identity;',
    1)

old_inspect = '''        var page = await _pages.GetPageAsync(runtime.RuntimeId, cancellationToken).ConfigureAwait(false);
        if (page is null) return Unknown("playwright-page:missing");
        try
        {
            var body = await BodyAsync(page).ConfigureAwait(false);
            var composer = await VisibleAsync(page, ComposerSelector).ConfigureAwait(false);
            var assistantCount = await page.Locator(AssistantSelector).CountAsync().ConfigureAwait(false);
'''
new_inspect = '''        var page = await ExpectedPageAsync(runtime, expectation.ProviderConversationIdentity, cancellationToken).ConfigureAwait(false);
        if (page is null) return Unknown("playwright-page:missing");
        try
        {
            var body = await BodyAsync(page).ConfigureAwait(false);
            var composer = await VisibleAsync(page, ComposerSelector).ConfigureAwait(false);
            var assistantTexts = await AssistantTextsAsync(page).ConfigureAwait(false);
            var assistantCount = assistantTexts.Count;
'''
if old_inspect not in adapter:
    raise SystemExit('InspectAsync anchor not found')
adapter = adapter.replace(old_inspect, new_inspect, 1)

adapter = adapter.replace(
    '            var response = assistantCount > 0 ? await LastAssistantAsync(page, assistantCount).ConfigureAwait(false) : null;',
    '            var response = assistantCount > 0 ? assistantTexts[^1] : null;',
    1)

# Submit on the exact expected conversation page as well. This prevents a stale/new tab from
# receiving a prompt after conversation reconciliation has already bound the runtime.
submit_anchor = '''        var page = await _pages.GetPageAsync(runtime.RuntimeId, cancellationToken).ConfigureAwait(false);
        if (page is null) return new(false, false, false, "BROWSER_PAGE_MISSING", new[] { "submission:not-triggered" });
        var triggered = false;
'''
submit_replacement = '''        var page = await ExpectedPageAsync(runtime, expectation.ProviderConversationIdentity, cancellationToken).ConfigureAwait(false);
        if (page is null) return new(false, false, false, "BROWSER_PAGE_MISSING", new[] { "submission:not-triggered" });
        var triggered = false;
'''
if submit_anchor not in adapter:
    raise SystemExit('SubmitAuthorizedAsync anchor not found')
adapter = adapter.replace(submit_anchor, submit_replacement, 1)

# User-message evidence also accepts data-turn=user. If a UI variant drops both explicit role
# attributes, fall back to the semantic conversation-turn label.
user_messages_anchor = '                var messages = await page.Locator(UserSelector).AllInnerTextsAsync().ConfigureAwait(false);\n                candidates.Add(new ChatGptConversationEvidenceCandidate(providerIdentity, messages.ToArray()));'
user_messages_replacement = '                var messages = await UserMessageTextsAsync(page).ConfigureAwait(false);\n                candidates.Add(new ChatGptConversationEvidenceCandidate(providerIdentity, messages.ToArray()));'
if user_messages_anchor not in adapter:
    raise SystemExit('user message evidence anchor not found')
adapter = adapter.replace(user_messages_anchor, user_messages_replacement, 1)

helper_anchor = '    private static async Task<ILocator?> VisibleAsync(IPage page, string selector)\n'
if helper_anchor not in adapter:
    raise SystemExit('helper insertion anchor not found')
helpers = r'''    private async Task<IPage?> ExpectedPageAsync(BrowserRuntimeRecord runtime, string? expectedProviderIdentity, CancellationToken cancellationToken)
    {
        if (_pages is IPlaywrightPageCatalog catalog &&
            !string.IsNullOrWhiteSpace(expectedProviderIdentity) &&
            !string.Equals(expectedProviderIdentity, "NEW", StringComparison.OrdinalIgnoreCase) &&
            Normalize(expectedProviderIdentity, out var expected))
        {
            var pages = await catalog.GetPagesAsync(runtime.RuntimeId, cancellationToken).ConfigureAwait(false);
            var exact = pages
                .Where(x => !x.IsClosed && Normalize(x.Url, out var actual) && StringComparer.OrdinalIgnoreCase.Equals(actual, expected))
                .ToArray();
            if (exact.Length > 0) return exact[^1];
        }

        return await _pages.GetPageAsync(runtime.RuntimeId, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<string>> AssistantTextsAsync(IPage page)
    {
        try
        {
            var texts = await page.EvaluateAsync<string[]>(
                """
                () => {
                  const read = (el) => ((el && (el.innerText || el.textContent)) || '').trim();
                  const explicit = Array.from(document.querySelectorAll("[data-message-author-role='assistant'], [data-turn='assistant']"))
                    .map(read).filter(Boolean);
                  if (explicit.length) return explicit;

                  const turns = Array.from(document.querySelectorAll("article[data-testid^='conversation-turn-'], [data-testid^='conversation-turn-']"));
                  return turns.filter(turn => {
                    const role = (turn.getAttribute('data-turn') || '').toLowerCase();
                    if (role === 'assistant') return true;
                    if (role === 'user') return false;

                    const labels = Array.from(turn.querySelectorAll('h1,h2,h3,h4,h5,h6,[aria-label]'))
                      .map(el => `${read(el)} ${el.getAttribute('aria-label') || ''}`)
                      .join(' ').toLowerCase();
                    if (labels.includes('chatgpt said') || labels.includes('assistant said')) return true;
                    if (labels.includes('you said') || labels.includes('user said')) return false;

                    // Current ChatGPT assistant turns carry rendered markdown while user bubbles do not.
                    return !!turn.querySelector('.markdown, [class*="markdown"], [data-message-content="assistant"]');
                  }).map(read).filter(Boolean);
                }
                """).ConfigureAwait(false);
            return texts ?? Array.Empty<string>();
        }
        catch (PlaywrightException)
        {
            return Array.Empty<string>();
        }
    }

    private static async Task<IReadOnlyList<string>> UserMessageTextsAsync(IPage page)
    {
        try
        {
            var explicitTexts = await page.Locator(UserSelector).AllInnerTextsAsync().ConfigureAwait(false);
            if (explicitTexts.Count > 0) return explicitTexts.ToArray();

            var texts = await page.EvaluateAsync<string[]>(
                """
                () => {
                  const read = (el) => ((el && (el.innerText || el.textContent)) || '').trim();
                  return Array.from(document.querySelectorAll("article[data-testid^='conversation-turn-'], [data-testid^='conversation-turn-']"))
                    .filter(turn => {
                      const role = (turn.getAttribute('data-turn') || '').toLowerCase();
                      if (role === 'user') return true;
                      if (role === 'assistant') return false;
                      const labels = Array.from(turn.querySelectorAll('h1,h2,h3,h4,h5,h6,[aria-label]'))
                        .map(el => `${read(el)} ${el.getAttribute('aria-label') || ''}`)
                        .join(' ').toLowerCase();
                      return labels.includes('you said') || labels.includes('user said');
                    })
                    .map(read).filter(Boolean);
                }
                """).ConfigureAwait(false);
            return texts ?? Array.Empty<string>();
        }
        catch (PlaywrightException)
        {
            return Array.Empty<string>();
        }
    }

'''
adapter = adapter.replace(helper_anchor, helpers + helper_anchor, 1)

# Modern ChatGPT DOM regression fixture: role moved away from data-message-author-role and the
# assistant turn is discoverable via the conversation-turn container/ChatGPT label/markdown.
fixture_anchor = '    public static ChatGptHtmlFixture ResponseCompleteWithoutActions { get; } = F("response-complete-without-actions", "conv-a", "<textarea data-testid=\'composer-text-input\'></textarea><article data-message-author-role=\'assistant\'>{\\\"ManagerEstimate\\\":25,\\\"Tasks\\\":[]}</article>");\n'
if fixture_anchor not in fixtures:
    raise SystemExit('fixture anchor not found')
modern_fixture = fixture_anchor + '    public static ChatGptHtmlFixture ResponseCompleteModernTurn { get; } = F("response-complete-modern-turn", "conv-a", "<textarea data-testid=\'composer-text-input\'></textarea><article data-testid=\'conversation-turn-2\'><h6 class=\'sr-only\'>ChatGPT said:</h6><div class=\'markdown\'>{\\\"ManagerEstimate\\\":25,\\\"Tasks\\\":[]}</div></article>");\n'
fixtures = fixtures.replace(fixture_anchor, modern_fixture, 1)
fixtures = fixtures.replace(
    '        HealthyIdle, Generating, ResponseComplete, ResponseCompleteWithoutActions, SlowGeneration, SendingTooFast, TemporaryError,',
    '        HealthyIdle, Generating, ResponseComplete, ResponseCompleteWithoutActions, ResponseCompleteModernTurn, SlowGeneration, SendingTooFast, TemporaryError,',
    1)
fixtures = fixtures.replace(
    '        var assistant = Has(html, "data-message-author-role=\'assistant\'") ? 1 : 0;',
    '        var assistant = Has(html, "data-message-author-role=\'assistant\'", "data-turn=\'assistant\'", "chatgpt said:", "class=\'markdown\'") ? 1 : 0;',
    1)

regression_insert = '''
    [Fact]
    public void Modern_conversation_turn_markup_is_detected_as_completed_assistant_response()
    {
        var snapshot = new DeterministicHtmlFixtureProbe().Inspect(ChatGptAdapterFixtures.ResponseCompleteModernTurn);

        Assert.Equal(1, snapshot.AssistantMessageCount);
        Assert.Equal(GenerationState.Complete, snapshot.Generation.State);
        Assert.Equal(ResponseCompleteness.Complete, snapshot.ResponseCompleteness);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.CapturedResponseText));
    }
'''
closing = '\n}\n'
if not regression.endswith(closing):
    raise SystemExit('regression file closing anchor not found')
regression = regression[:-len(closing)] + regression_insert + closing

adapter_path.write_text(adapter, encoding='utf-8')
fixtures_path.write_text(fixtures, encoding='utf-8')
regression_path.write_text(regression, encoding='utf-8')
