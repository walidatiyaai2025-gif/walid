using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PCCExecutive.Browser;

namespace PCCExecutive.Browser.Acceptance;

public enum LivePilotAcceptanceState
{
    Pass,
    Fail,
    BlockedLogin,
    BlockedChallenge,
    BlockedRunner,
    BlockedDependency,
    NotExecuted
}

public enum LivePilotLevel { Level1 = 1, Level2 = 2, Level3 = 3 }

public sealed record LivePilotGateDecision(LivePilotAcceptanceState State, string Reason, bool MayUseLiveBrowser, bool MaySubmit);

public static class LivePilotGate
{
    public static LivePilotGateDecision Evaluate(bool enabled, bool windows, bool submitEnabled, bool hasManagerConversation, bool hasWorkerConversation)
    {
        if (!enabled) return new(LivePilotAcceptanceState.NotExecuted, "LIVE_BROWSER_OPT_IN_DISABLED", false, false);
        if (!windows) return new(LivePilotAcceptanceState.BlockedRunner, "LIVE_BROWSER_REQUIRES_WINDOWS_RUNNER", false, false);
        if (!submitEnabled) return new(LivePilotAcceptanceState.NotExecuted, "LIVE_PROMPT_SUBMISSION_NOT_OPTED_IN", true, false);
        if (!hasManagerConversation || !hasWorkerConversation) return new(LivePilotAcceptanceState.BlockedDependency, "CONTROLLED_MANAGER_AND_WORKER_CONVERSATION_URLS_REQUIRED", true, false);
        return new(LivePilotAcceptanceState.Pass, "LIVE_PILOT_PREREQUISITES_SATISFIED", true, true);
    }
}

public sealed record LiveConversationBinding(string ConversationId, string CanonicalUrl);

public static class LiveConversationIdentity
{
    private static readonly Regex ConversationPath = new(@"^/c/([A-Za-z0-9_-]{6,128})/?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static bool TryParse(string? value, out LiveConversationBinding binding)
    {
        binding = new(string.Empty, string.Empty);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(uri.Host, "chatgpt.com", StringComparison.OrdinalIgnoreCase) && !uri.Host.EndsWith(".chatgpt.com", StringComparison.OrdinalIgnoreCase)) return false;
        var match = ConversationPath.Match(uri.AbsolutePath);
        if (!match.Success) return false;
        var id = match.Groups[1].Value;
        binding = new(id, $"https://chatgpt.com/c/{id}");
        return true;
    }
}

public sealed record LiveLoginBoundaryDecision(LivePilotAcceptanceState State, bool BringToFront, bool PauseNewSends, string Reason);

public sealed class LiveLoginBoundary
{
    public LiveLoginBoundaryDecision Evaluate(ChatGptSemanticSnapshot snapshot)
    {
        if (snapshot.Auth.State == AuthState.Challenge)
            return new(LivePilotAcceptanceState.BlockedChallenge, true, true, "CHALLENGE_MANUAL_ACTION_REQUIRED");
        if (snapshot.Auth.State == AuthState.LoginRequired)
            return new(LivePilotAcceptanceState.BlockedLogin, true, true, "LOGIN_MANUAL_ACTION_REQUIRED");
        if (snapshot.Auth.State == AuthState.Unknown || snapshot.Input.State == InputState.Unknown || snapshot.Health.State == PageHealth.Unknown)
            return new(LivePilotAcceptanceState.Fail, false, true, "BROWSER_ADAPTER_UNCERTAIN");
        if (snapshot.Auth.State == AuthState.Authenticated && snapshot.Input.State == InputState.Ready && snapshot.Health.State == PageHealth.Healthy)
            return new(LivePilotAcceptanceState.Pass, false, false, "AUTHENTICATED_READY");
        return new(LivePilotAcceptanceState.NotExecuted, false, true, "AUTHENTICATED_READY_NOT_YET_PROVEN");
    }
}

public sealed record LiveSessionMonitorEvidence(
    string LogicalAgentId,
    string? WorkerSlot,
    string? ConversationId,
    string RuntimeId,
    bool OwnedByPcc,
    string State,
    DateTimeOffset Heartbeat,
    string Visibility,
    string Health,
    DateTimeOffset LastActivity);

public static class LiveSessionMonitorEvidenceFactory
{
    public static LiveSessionMonitorEvidence Create(BrowserRuntimeRecord runtime, ChatGptSemanticSnapshot snapshot) =>
        new(runtime.LogicalAgentId, runtime.WorkerSlotId, runtime.ConversationIdentity, runtime.RuntimeId,
            runtime.CreatedByPcc || runtime.AdoptedExplicitly, runtime.State.ToString(), runtime.LastHeartbeatAt,
            runtime.Visibility.ToString(), snapshot.Health.State.ToString(), runtime.LastActivityAt);
}

public sealed record LivePilotArtifact(
    string Scenario,
    string SourceSha,
    string AdapterVersion,
    LivePilotAcceptanceState AcceptanceState,
    int Level,
    IReadOnlyList<LiveSessionMonitorEvidence> Sessions,
    IReadOnlyList<string> StateTransitions,
    IReadOnlyList<long> TimingsMilliseconds,
    IReadOnlyList<string> FailureCodes,
    IReadOnlyList<string> EvidenceCodes);

public static class LivePilotArtifactSanitizer
{
    private static readonly string[] ForbiddenMarkers =
    [
        "authorization:", "set-cookie:", "cookie:", "password=", "password:", "access_token", "access-token:",
        "refresh_token", "refresh-token:", "session_token", "session-token:", "localstorage", "__secure-", "__host-"
    ];
    private static readonly Regex Jwt = new(@"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b", RegexOptions.CultureInvariant);
    private static readonly Regex ApiKey = new(@"\bsk-[A-Za-z0-9_-]{16,}\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static string SerializeOrThrow(LivePilotArtifact artifact)
    {
        foreach (var value in EnumerateStrings(artifact))
        {
            if (LooksSensitive(value))
                throw new InvalidOperationException("LIVE_ACCEPTANCE_ARTIFACT_REJECTED_SENSITIVE_MATERIAL");
        }
        return JsonSerializer.Serialize(artifact, new JsonSerializerOptions { WriteIndented = true });
    }

    public static bool LooksSensitive(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (ForbiddenMarkers.Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase))) return true;
        return Jwt.IsMatch(value) || ApiKey.IsMatch(value);
    }

    private static IEnumerable<string> EnumerateStrings(LivePilotArtifact artifact)
    {
        yield return artifact.Scenario;
        yield return artifact.SourceSha;
        yield return artifact.AdapterVersion;
        foreach (var session in artifact.Sessions)
        {
            yield return session.LogicalAgentId;
            yield return session.WorkerSlot ?? string.Empty;
            yield return session.ConversationId ?? string.Empty;
            yield return session.RuntimeId;
            yield return session.State;
            yield return session.Visibility;
            yield return session.Health;
        }
        foreach (var item in artifact.StateTransitions) yield return item;
        foreach (var item in artifact.FailureCodes) yield return item;
        foreach (var item in artifact.EvidenceCodes) yield return item;
    }
}

public static class LivePilotPrompt
{
    public const string Marker = "PCC_EXECUTIVE_LIVE_PILOT";

    public static string Create(string taskId, int workerSlot) =>
        $"Return exactly four short lines and nothing else.\nTASK_ID: {taskId}\nWORKER_SLOT: {workerSlot}\nSTATUS: ACK\nNON_DESTRUCTIVE_MARKER: {Marker}";
}

public sealed record LiveResponseAssociation(bool Matches, string Reason);

public static class LivePilotResponseAssociation
{
    public static LiveResponseAssociation Validate(string? response, string taskId, int workerSlot)
    {
        if (string.IsNullOrWhiteSpace(response)) return new(false, "EMPTY_RESPONSE");
        var lines = response.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool Has(string prefix, string expected) => lines.Any(line => string.Equals(line, $"{prefix}: {expected}", StringComparison.OrdinalIgnoreCase));
        if (!Has("TASK_ID", taskId)) return new(false, "TASK_ID_MISMATCH");
        if (!Has("WORKER_SLOT", workerSlot.ToString(System.Globalization.CultureInfo.InvariantCulture))) return new(false, "WORKER_SLOT_MISMATCH");
        if (!Has("STATUS", "ACK")) return new(false, "STATUS_MISMATCH");
        if (!Has("NON_DESTRUCTIVE_MARKER", LivePilotPrompt.Marker)) return new(false, "MARKER_MISMATCH");
        return new(true, "RESPONSE_ASSOCIATION_PROVEN");
    }
}

public sealed record LiveCompletionDecision(bool Complete, string Reason);

public static class LiveResponseCompletionGate
{
    public static LiveCompletionDecision Evaluate(ChatGptSemanticSnapshot current, string? previousCapturedResponse)
    {
        if (current.ResponseCompleteness == ResponseCompleteness.Partial) return new(false, "PARTIAL_RESPONSE");
        if (current.Generation.State == GenerationState.Generating) return new(false, "GENERATION_ACTIVE");
        if (current.Auth.State == AuthState.LoginRequired) return new(false, "LOGIN_REQUIRED");
        if (current.Auth.State == AuthState.Challenge) return new(false, "CHALLENGE");
        if (current.Health.State == PageHealth.RateLimited) return new(false, "RATE_LIMITED");
        if (current.Health.State == PageHealth.Offline) return new(false, "OFFLINE");
        if (current.Input.State == InputState.Unknown || current.Generation.State == GenerationState.Unknown || current.Conversation.State == ConversationMatch.Unknown)
            return new(false, "BROWSER_ADAPTER_UNCERTAIN");
        var stable = !string.IsNullOrWhiteSpace(current.CapturedResponseText) && string.Equals(current.CapturedResponseText, previousCapturedResponse, StringComparison.Ordinal);
        var semanticProof = current.Generation.State == GenerationState.Complete &&
                            current.ResponseCompleteness == ResponseCompleteness.Complete &&
                            current.Input.State == InputState.Ready &&
                            current.Auth.State == AuthState.Authenticated &&
                            current.Conversation.State == ConversationMatch.Match &&
                            current.Health.State == PageHealth.Healthy &&
                            current.Generation.Evidence.Any(x => x.Contains("response-actions:visible", StringComparison.OrdinalIgnoreCase));
        return semanticProof && stable
            ? new(true, "RESPONSE_COMPLETE_STABLE_MULTI_SIGNAL")
            : new(false, semanticProof ? "RESPONSE_NOT_YET_STABLE" : "RESPONSE_COMPLETION_UNPROVEN");
    }
}

public sealed class LiveConversationEvidenceProbe : IConversationEvidenceProbe
{
    private readonly IPlaywrightPageProvider _pages;
    private readonly ConcurrentDictionary<string, string> _prompts = new(StringComparer.Ordinal);

    public LiveConversationEvidenceProbe(IPlaywrightPageProvider pages) => _pages = pages;

    public void Remember(string dispatchId, string prompt) => _prompts[dispatchId] = Normalize(prompt);

    public async Task<ConversationDispatchEvidence> InspectDispatchAsync(string runtimeId, string dispatchId, string contentHash, CancellationToken cancellationToken = default)
    {
        if (!_prompts.TryGetValue(dispatchId, out var expected))
            return new(null, false, false, .10, new[] { "live-probe:dispatch-prompt-not-registered" });
        var page = await _pages.GetPageAsync(runtimeId, cancellationToken).ConfigureAwait(false);
        if (page is null) return new(null, false, false, .10, new[] { "live-probe:page-missing" });
        try
        {
            var users = page.Locator("[data-message-author-role='user']");
            var count = await users.CountAsync().ConfigureAwait(false);
            for (var i = Math.Max(0, count - 20); i < count; i++)
            {
                var text = Normalize(await users.Nth(i).InnerTextAsync().ConfigureAwait(false));
                if (string.Equals(text, expected, StringComparison.Ordinal))
                    return new(true, false, false, .99, new[] { "live-probe:user-message-present", $"user-message-count:{count}" });
            }
            return new(null, false, false, .55, new[] { "live-probe:no-match-negative-proof-withheld", $"user-message-count:{count}" });
        }
        catch (Exception ex) when (ex.GetType().Namespace?.StartsWith("Microsoft.Playwright", StringComparison.Ordinal) == true)
        {
            return new(null, false, false, .10, new[] { $"live-probe:inspection-error:{ex.GetType().Name}" });
        }
    }

    public static string HashPrompt(string prompt) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(prompt)));

    private static string Normalize(string value) => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

public sealed record LiveRestartIdentityEnvelope(
    string ProjectRunId,
    string LogicalAgentId,
    string? WorkerSlotId,
    string? TaskId,
    string? ConversationIdentity,
    string RuntimeId,
    IReadOnlyList<string> DispatchIds);

public interface ILiveRestartReconciliationPort
{
    Task<LiveRestartIdentityEnvelope?> RestoreAsync(string logicalAgentId, CancellationToken cancellationToken = default);
}

public static class LiveRestartReconciliation
{
    public static bool Matches(BrowserRuntimeRecord runtime, LiveRestartIdentityEnvelope restored) =>
        StringComparer.Ordinal.Equals(runtime.ProjectRunId, restored.ProjectRunId) &&
        StringComparer.Ordinal.Equals(runtime.LogicalAgentId, restored.LogicalAgentId) &&
        StringComparer.Ordinal.Equals(runtime.WorkerSlotId, restored.WorkerSlotId) &&
        StringComparer.Ordinal.Equals(runtime.TaskId, restored.TaskId) &&
        StringComparer.Ordinal.Equals(runtime.ConversationIdentity, restored.ConversationIdentity) &&
        StringComparer.Ordinal.Equals(runtime.RuntimeId, restored.RuntimeId);
}

public static class LivePilotProgression
{
    public static int ResolveWorkerCount(LivePilotLevel level, int requestedWorkers) => level switch
    {
        LivePilotLevel.Level1 => 1,
        LivePilotLevel.Level2 => Math.Clamp(requestedWorkers, 2, 3),
        LivePilotLevel.Level3 => Math.Clamp(requestedWorkers, 4, 5),
        _ => throw new ArgumentOutOfRangeException(nameof(level))
    };
}

public sealed class FaultInjectingSubmissionAdapter : IChatGptBrowserAdapter
{
    private readonly IChatGptBrowserAdapter _inner;
    private int _remainingForcedUnknown;

    public FaultInjectingSubmissionAdapter(IChatGptBrowserAdapter inner, int forcedUnknownSubmissions = 1)
    {
        _inner = inner;
        _remainingForcedUnknown = Math.Max(0, forcedUnknownSubmissions);
    }

    public string AdapterVersion => _inner.AdapterVersion;

    public Task<ChatGptSemanticSnapshot> InspectAsync(BrowserRuntimeRecord runtime, BrowserDispatchExpectation expectation, CancellationToken cancellationToken = default) =>
        _inner.InspectAsync(runtime, expectation, cancellationToken);

    public async Task<AdapterSubmissionResult> SubmitAsync(BrowserRuntimeRecord runtime, BrowserDispatchExpectation expectation, string prompt, CancellationToken cancellationToken = default)
    {
        var actual = await _inner.SubmitAsync(runtime, expectation, prompt, cancellationToken).ConfigureAwait(false);
        if (_remainingForcedUnknown > 0 && actual.Triggered)
        {
            Interlocked.Decrement(ref _remainingForcedUnknown);
            return new(true, false, true, "SUBMITTED_UNKNOWN_FAULT_INJECTED_AFTER_TRIGGER", actual.Evidence.Append("acceptance:fault-injected-uncertain-after-trigger").ToArray());
        }
        return actual;
    }
}
