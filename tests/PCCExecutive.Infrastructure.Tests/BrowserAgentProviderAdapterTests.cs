using PCCExecutive.Application;
using PCCExecutive.Browser;
using PCCExecutive.Domain;
using PCCExecutive.Infrastructure;
using Xunit;

namespace PCCExecutive.Infrastructure.Tests;

public sealed class BrowserAgentProviderAdapterTests
{
    [Fact]
    public async Task Canonical_application_request_reaches_browser_provider_without_parallel_abstraction()
    {
        var registry = new InMemoryBrowserRuntimeRegistry();
        var runtime = new BrowserRuntimeRecord
        {
            RuntimeId = "runtime",
            ProjectRunId = ProjectRunId.New().ToString(),
            LogicalAgentId = LogicalAgentId.New().ToString(),
            TaskId = "task",
            ConversationIdentity = ConversationId.New().ToString(),
            ProviderConversationIdentity = "provider-conversation",
            ProfilePath = "profile",
            CreatedByPcc = true,
            OwnershipNonce = "nonce",
            State = BrowserSessionState.Ready,
            LastHeartbeatAt = DateTimeOffset.UtcNow,
            LastActivityAt = DateTimeOffset.UtcNow
        };
        await registry.UpsertAsync(runtime);
        var browser = new PCCExecutive.Browser.BrowserChatProvider(registry, new SafeAdapter(), new InMemoryDispatchLedger(), new WrongChatGuard(), new GlobalBrowserSendGate());
        var provider = new BrowserAgentProviderAdapter(registry, browser);
        var request = new AgentRequest(ProjectRunIdFrom(runtime.ProjectRunId), LogicalAgentIdFrom(runtime.LogicalAgentId), ConversationIdFrom(runtime.ConversationIdentity!), DispatchId.New(), "do work", "hash");

        var result = await provider.SendAsync(request);

        Assert.True(result.Accepted);
        Assert.False(result.IsUncertain);
        Assert.Null(result.ErrorCode);
    }

    [Fact]
    public async Task Conversation_mismatch_fails_safe_before_send()
    {
        var registry = new InMemoryBrowserRuntimeRegistry();
        var run = ProjectRunId.New();
        var agent = LogicalAgentId.New();
        await registry.UpsertAsync(new BrowserRuntimeRecord
        {
            RuntimeId = "runtime",
            ProjectRunId = run.ToString(),
            LogicalAgentId = agent.ToString(),
            TaskId = "task",
            ConversationIdentity = ConversationId.New().ToString(),
            ProviderConversationIdentity = "provider-conversation",
            ProfilePath = "profile",
            CreatedByPcc = true,
            OwnershipNonce = "nonce",
            State = BrowserSessionState.Ready,
            LastHeartbeatAt = DateTimeOffset.UtcNow,
            LastActivityAt = DateTimeOffset.UtcNow
        });
        var provider = new BrowserAgentProviderAdapter(registry, new PCCExecutive.Browser.BrowserChatProvider(registry, new SafeAdapter(), new InMemoryDispatchLedger(), new WrongChatGuard(), new GlobalBrowserSendGate()));

        var result = await provider.SendAsync(new AgentRequest(run, agent, ConversationId.New(), DispatchId.New(), "do work", "hash"));

        Assert.False(result.Accepted);
        Assert.Equal("WRONG_CONVERSATION_BINDING", result.ErrorCode);
    }

    private static ProjectRunId ProjectRunIdFrom(string value) => new(Guid.ParseExact(value, "N"));
    private static LogicalAgentId LogicalAgentIdFrom(string value) => new(Guid.ParseExact(value, "N"));
    private static ConversationId ConversationIdFrom(string value) => new(Guid.ParseExact(value, "N"));

    private sealed class SafeAdapter : IChatGptBrowserAdapter
    {
        public string AdapterVersion => "test";
        public Task<ChatGptSemanticSnapshot> InspectAsync(BrowserRuntimeRecord runtime, BrowserDispatchExpectation expectation, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatGptSemanticSnapshot(
                SemanticDetection<InputState>.Create(InputState.Ready, 1, AdapterVersion, "ready"),
                SemanticDetection<GenerationState>.Create(GenerationState.Idle, 1, AdapterVersion, "idle"),
                SemanticDetection<AuthState>.Create(AuthState.Authenticated, 1, AdapterVersion, "auth"),
                SemanticDetection<ConversationMatch>.Create(ConversationMatch.Match, 1, AdapterVersion, "match"),
                SemanticDetection<PageHealth>.Create(PageHealth.Healthy, 1, AdapterVersion, "healthy"),
                ResponseCompleteness.None,
                0,
                null,
                DateTimeOffset.UtcNow,
                AdapterVersion));

        public Task<AdapterSubmissionResult> SubmitAsync(BrowserRuntimeRecord runtime, BrowserDispatchExpectation expectation, string prompt, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AdapterSubmissionResult(true, true, false, "SUBMITTED", new[] { "test-submit" }));
    }
}
