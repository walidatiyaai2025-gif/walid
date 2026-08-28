using PCCExecutive.Application;
using PCCExecutive.Browser;
using PCCExecutive.Domain;

namespace PCCExecutive.Infrastructure;

public sealed class BrowserAgentProviderAdapter : IAgentProvider
{
    private readonly IBrowserRuntimeRegistry _runtimes;
    private readonly PCCExecutive.Browser.BrowserChatProvider _provider;

    public BrowserAgentProviderAdapter(IBrowserRuntimeRegistry runtimes, PCCExecutive.Browser.BrowserChatProvider provider)
    {
        _runtimes = runtimes;
        _provider = provider;
    }

    public AgentProviderKind Kind => AgentProviderKind.BrowserChat;

    public async Task<ProviderHealth> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var runtimes = await _runtimes.ListAsync(cancellationToken).ConfigureAwait(false);
        var active = runtimes.Where(x => !x.IsArchived && x.State is not BrowserSessionState.Killed and not BrowserSessionState.Archived).ToArray();
        if (active.Length == 0)
            return new(true, false, false, "NO_BOUND_BROWSER_RUNTIME", "BrowserChatProvider is configured as default; no PCC-owned runtime is bound yet.");

        var authenticatedCandidate = active.Any(x => x.State is BrowserSessionState.Ready or BrowserSessionState.Hidden or BrowserSessionState.Visible or BrowserSessionState.Active);
        return new(true, authenticatedCandidate, false, authenticatedCandidate ? "PCC_BROWSER_RUNTIME_READY" : "PCC_BROWSER_RUNTIME_DEGRADED", $"owned-runtime-count:{active.Length}");
    }

    public async Task<AgentResult> SendAsync(AgentRequest request, CancellationToken cancellationToken = default)
    {
        var runtimes = await _runtimes.ListAsync(cancellationToken).ConfigureAwait(false);
        var runtime = runtimes
            .Where(x => !x.IsArchived && x.State is not BrowserSessionState.Killed and not BrowserSessionState.Archived)
            .FirstOrDefault(x => StringComparer.Ordinal.Equals(x.ProjectRunId, request.ProjectRunId.ToString()) && StringComparer.Ordinal.Equals(x.LogicalAgentId, request.LogicalAgentId.ToString()));

        if (runtime is null)
            return new(request.DispatchId, false, false, false, false, null, "owned-runtime:not-found", "BROWSER_RUNTIME_NOT_BOUND");

        if (string.IsNullOrWhiteSpace(runtime.TaskId) || string.IsNullOrWhiteSpace(runtime.ConversationIdentity) || string.IsNullOrWhiteSpace(runtime.ProviderConversationIdentity))
            return new(request.DispatchId, false, false, false, false, null, $"runtime:{runtime.RuntimeId}", "BROWSER_DISPATCH_BINDING_INCOMPLETE");

        if (!StringComparer.Ordinal.Equals(runtime.ConversationIdentity, request.ConversationId.ToString()))
            return new(request.DispatchId, false, false, false, false, null, $"runtime:{runtime.RuntimeId};conversation:mismatch", "WRONG_CONVERSATION_BINDING");

        var browserRequest = new BrowserDispatchRequest(
            request.DispatchId.ToString(),
            request.ProjectRunId.ToString(),
            request.LogicalAgentId.ToString(),
            runtime.TaskId,
            runtime.ConversationIdentity,
            runtime.ProviderConversationIdentity,
            request.Content,
            request.ContentHash);

        var result = await _provider.SendAsync(runtime.RuntimeId, browserRequest, cancellationToken).ConfigureAwait(false);
        var evidence = string.Join(";", result.Evidence.Prepend(result.Reason));
        return result.Outcome switch
        {
            BrowserDispatchOutcome.Submitted => new(request.DispatchId, true, result.State == PCCExecutive.Browser.DispatchState.Generating, false, false, null, evidence, null),
            BrowserDispatchOutcome.SubmittedUnknown => new(request.DispatchId, false, false, false, true, null, evidence, "SUBMITTED_UNKNOWN"),
            BrowserDispatchOutcome.DuplicateBlocked => new(request.DispatchId, false, false, false, false, null, evidence, "DUPLICATE_SEND_BLOCKED"),
            _ => new(request.DispatchId, false, false, false, false, null, evidence, result.Reason)
        };
    }
}
