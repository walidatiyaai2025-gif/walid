using System.Collections.Concurrent;
using PCCExecutive.Browser;

namespace PCCExecutive.Browser.Acceptance;

public sealed class RecordingDispatchLedger : IDispatchLedger
{
    private readonly InMemoryDispatchLedger _inner = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<DispatchState>> _history = new(StringComparer.Ordinal);

    public IReadOnlyList<DispatchState> History(string dispatchId) =>
        _history.TryGetValue(dispatchId, out var queue) ? queue.ToArray() : Array.Empty<DispatchState>();

    public async Task<DispatchReservation> ReserveAsync(string dispatchId, string contentHash, CancellationToken cancellationToken = default)
    {
        var reservation = await _inner.ReserveAsync(dispatchId, contentHash, cancellationToken).ConfigureAwait(false);
        if (reservation.Status == DispatchReservationStatus.New) Record(dispatchId, DispatchState.Prepared);
        return reservation;
    }

    public async Task UpdateAsync(string dispatchId, DispatchState state, string? reconciliationEvidence = null, CancellationToken cancellationToken = default)
    {
        await _inner.UpdateAsync(dispatchId, state, reconciliationEvidence, cancellationToken).ConfigureAwait(false);
        Record(dispatchId, state);
    }

    public Task<DispatchLedgerEntry?> GetAsync(string dispatchId, CancellationToken cancellationToken = default) =>
        _inner.GetAsync(dispatchId, cancellationToken);

    private void Record(string dispatchId, DispatchState state) =>
        _history.GetOrAdd(dispatchId, static _ => new ConcurrentQueue<DispatchState>()).Enqueue(state);
}

public sealed record LivePreparedSession(
    BrowserRuntimeRecord Runtime,
    ChatGptSemanticSnapshot Snapshot,
    LivePilotAcceptanceState State,
    string Reason);

public sealed record LiveRoundTripResult(
    LivePilotAcceptanceState State,
    string Reason,
    BrowserDispatchResult Dispatch,
    IReadOnlyList<DispatchState> DispatchTransitions,
    ChatGptSemanticSnapshot? FinalSnapshot,
    bool DuplicateBlocked,
    LiveResponseAssociation? ResponseAssociation,
    IReadOnlyList<string> EvidenceCodes,
    long ElapsedMilliseconds);

public sealed class LivePilotRuntimeDriver
{
    private readonly InMemoryBrowserRuntimeRegistry _registry;
    private readonly FileOwnershipMarkerStore _markers;
    private readonly SystemProcessInspector _processes;
    private readonly PlaywrightChromeRuntimeHost _host;
    private readonly OwnershipProofService _ownership;
    private readonly BrowserSessionController _sessions;
    private readonly PlaywrightChatGptBrowserAdapter _adapter;
    private readonly GlobalBrowserSendGate _gate = new();
    private readonly LiveLoginBoundary _login = new();

    public LivePilotRuntimeDriver(string profileRoot)
    {
        _registry = new InMemoryBrowserRuntimeRegistry();
        _markers = new FileOwnershipMarkerStore();
        _processes = new SystemProcessInspector();
        _host = new PlaywrightChromeRuntimeHost(profileRoot);
        _ownership = new OwnershipProofService(profileRoot, _markers, _processes);
        _sessions = new BrowserSessionController(_registry, _host, _ownership, _markers, _processes);
        _adapter = new PlaywrightChatGptBrowserAdapter(_host);
    }

    public string AdapterVersion => _adapter.AdapterVersion;
    public IBrowserRuntimeRegistry Registry => _registry;
    public BrowserSessionController Sessions => _sessions;

    public async Task<LivePreparedSession> PrepareSessionAsync(
        string runtimeId,
        string projectRunId,
        string logicalAgentId,
        string? workerSlot,
        string taskId,
        LiveConversationBinding binding,
        bool allowManualLogin,
        TimeSpan manualLoginTimeout,
        CancellationToken cancellationToken = default)
    {
        var runtime = await _sessions.CreateAsync(new BrowserSessionRequest(
            projectRunId, logicalAgentId, workerSlot, taskId, binding.ConversationId, binding.CanonicalUrl,
            BrowserVisibility.Hidden, runtimeId), cancellationToken).ConfigureAwait(false);

        var expectation = Expectation(runtime);
        var auth = await WaitForAuthenticationAsync(runtime, expectation, allowManualLogin, manualLoginTimeout, cancellationToken).ConfigureAwait(false);
        if (auth.State != LivePilotAcceptanceState.Pass)
            return new(runtime, auth.Snapshot, auth.State, auth.Reason);

        var page = await _host.GetPageAsync(runtime.RuntimeId, cancellationToken).ConfigureAwait(false);
        if (page is null) return new(runtime, auth.Snapshot, LivePilotAcceptanceState.Fail, "PCC_BROWSER_PAGE_MISSING");
        await page.GotoAsync(binding.CanonicalUrl).ConfigureAwait(false);
        await page.WaitForTimeoutAsync(1000).ConfigureAwait(false);

        var snapshot = await _adapter.InspectAsync(runtime, expectation, cancellationToken).ConfigureAwait(false);
        var drift = new ChatGptAdapterDriftGuard().Evaluate(snapshot);
        if (!drift.IsCertain) return new(runtime, snapshot, LivePilotAcceptanceState.Fail, drift.Reason);
        if (snapshot.Conversation.State != ConversationMatch.Match) return new(runtime, snapshot, LivePilotAcceptanceState.Fail, "LIVE_CONVERSATION_MATCH_NOT_PROVEN");
        if (snapshot.Input.State != InputState.Ready || snapshot.Auth.State != AuthState.Authenticated || snapshot.Health.State != PageHealth.Healthy)
            return new(runtime, snapshot, LivePilotAcceptanceState.Fail, "LIVE_CHATGPT_READY_NOT_PROVEN");
        await _sessions.HideAsync(runtime.RuntimeId, cancellationToken).ConfigureAwait(false);
        return new(runtime, snapshot, LivePilotAcceptanceState.Pass, "LIVE_SESSION_READY_AND_BOUND");
    }

    public async Task<LiveRoundTripResult> RunWorkerRoundTripAsync(
        BrowserRuntimeRecord runtime,
        string taskId,
        int workerSlot,
        TimeSpan responseTimeout,
        CancellationToken cancellationToken = default)
    {
        var ledger = new RecordingDispatchLedger();
        var provider = new BrowserChatProvider(_registry, _adapter, ledger, new WrongChatGuard(), _gate);
        var prompt = LivePilotPrompt.Create(taskId, workerSlot);
        var dispatchId = $"live-{taskId}-{Guid.NewGuid():N}";
        var request = Request(runtime, taskId, dispatchId, prompt);
        var evidence = new List<string>();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var sent = await provider.SendAsync(runtime.RuntimeId, request, cancellationToken).ConfigureAwait(false);
        evidence.Add($"dispatch-outcome:{sent.Outcome}");
        evidence.AddRange(sent.Evidence.Select(SafeEvidenceCode));
        if (sent.Outcome == BrowserDispatchOutcome.SubmittedUnknown)
            return new(LivePilotAcceptanceState.NotExecuted, "SUBMITTED_UNKNOWN_REQUIRES_RECONCILIATION", sent, ledger.History(dispatchId), null, false, null, evidence, stopwatch.ElapsedMilliseconds);
        if (sent.Outcome != BrowserDispatchOutcome.Submitted)
            return new(LivePilotAcceptanceState.Fail, sent.Reason, sent, ledger.History(dispatchId), null, false, null, evidence, stopwatch.ElapsedMilliseconds);

        var probe = new LiveConversationEvidenceProbe(_host);
        probe.Remember(dispatchId, prompt);
        var dispatchEntry = await ledger.GetAsync(dispatchId, cancellationToken).ConfigureAwait(false);
        if (dispatchEntry is not null)
        {
            var presence = await probe.InspectDispatchAsync(runtime.RuntimeId, dispatchId, dispatchEntry.ContentHash, cancellationToken).ConfigureAwait(false);
            if (presence.UserMessagePresent == true)
            {
                await ledger.UpdateAsync(dispatchId, DispatchState.Acknowledged, "live-probe:user-message-present", cancellationToken).ConfigureAwait(false);
                evidence.Add("dispatch:acknowledged-by-user-message-presence");
            }
        }

        string? previousResponse = null;
        ChatGptSemanticSnapshot? final = null;
        var classifier = new ChatGptResilienceClassifier(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(3));
        var deadline = DateTimeOffset.UtcNow + responseTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            final = await _adapter.InspectAsync(runtime, Expectation(runtime), cancellationToken).ConfigureAwait(false);
            var resilience = classifier.Classify(final, stopwatch.Elapsed);
            if (resilience.State == ChatGptResilienceState.Slow) evidence.Add("resilience:SLOW");
            if (resilience.State == ChatGptResilienceState.Stuck) evidence.Add("resilience:STUCK");
            if (final.Generation.State == GenerationState.Generating && !ledger.History(dispatchId).Contains(DispatchState.Generating))
                await ledger.UpdateAsync(dispatchId, DispatchState.Generating, "live-adapter:generation-active", cancellationToken).ConfigureAwait(false);

            if (final.Auth.State == AuthState.LoginRequired)
                return new(LivePilotAcceptanceState.BlockedLogin, "LOGIN_REQUIRED_MID_DISPATCH", sent, ledger.History(dispatchId), final, false, null, evidence, stopwatch.ElapsedMilliseconds);
            if (final.Auth.State == AuthState.Challenge)
                return new(LivePilotAcceptanceState.BlockedChallenge, "CHALLENGE_MID_DISPATCH", sent, ledger.History(dispatchId), final, false, null, evidence, stopwatch.ElapsedMilliseconds);
            if (final.Health.State == PageHealth.RateLimited)
            {
                _gate.Apply(new ResilienceDecision(ChatGptResilienceState.RateLimited, FaultScope.Global, true, false, "RATE_LIMITED"), DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30));
                evidence.Add("global-gate:paused-rate-limit");
                return new(LivePilotAcceptanceState.NotExecuted, "RATE_LIMITED_NEW_SENDS_PAUSED", sent, ledger.History(dispatchId), final, false, null, evidence, stopwatch.ElapsedMilliseconds);
            }
            if (final.Health.State == PageHealth.Offline)
                return new(LivePilotAcceptanceState.NotExecuted, "OFFLINE_STATE_PRESERVED", sent, ledger.History(dispatchId), final, false, null, evidence, stopwatch.ElapsedMilliseconds);
            if (final.ResponseCompleteness == ResponseCompleteness.Partial)
                return new(LivePilotAcceptanceState.NotExecuted, "PARTIAL_RESPONSE_NOT_DONE", sent, ledger.History(dispatchId), final, false, null, evidence.Append("response:partial-captured-in-memory").ToArray(), stopwatch.ElapsedMilliseconds);

            var completion = LiveResponseCompletionGate.Evaluate(final, previousResponse);
            if (completion.Complete)
            {
                await ledger.UpdateAsync(dispatchId, DispatchState.ResponseComplete, completion.Reason, cancellationToken).ConfigureAwait(false);
                var association = LivePilotResponseAssociation.Validate(final.CapturedResponseText, taskId, workerSlot);
                var duplicate = await provider.SendAsync(runtime.RuntimeId, request, cancellationToken).ConfigureAwait(false);
                var duplicateBlocked = duplicate.Outcome == BrowserDispatchOutcome.DuplicateBlocked;
                evidence.Add(completion.Reason);
                evidence.Add(association.Reason);
                evidence.Add(duplicateBlocked ? "duplicate-send:blocked" : "duplicate-send:NOT_BLOCKED");
                var state = association.Matches && duplicateBlocked ? LivePilotAcceptanceState.Pass : LivePilotAcceptanceState.Fail;
                var reason = association.Matches ? duplicateBlocked ? "REAL_WORKER_ROUND_TRIP_PROVEN" : "DUPLICATE_PROTECTION_FAILED" : association.Reason;
                return new(state, reason, sent, ledger.History(dispatchId), final, duplicateBlocked, association, evidence, stopwatch.ElapsedMilliseconds);
            }

            previousResponse = final.CapturedResponseText;
            await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
        }

        return new(LivePilotAcceptanceState.Fail, "LIVE_RESPONSE_TIMEOUT", sent, ledger.History(dispatchId), final, false, null, evidence, stopwatch.ElapsedMilliseconds);
    }

    public async Task<(bool NoSend, string Reason)> VerifyWrongChatNoSendAsync(
        BrowserRuntimeRecord runtime,
        string taskId,
        LiveConversationBinding wrongConversation,
        LiveConversationBinding correctConversation,
        CancellationToken cancellationToken = default)
    {
        var page = await _host.GetPageAsync(runtime.RuntimeId, cancellationToken).ConfigureAwait(false);
        if (page is null) return (false, "PCC_BROWSER_PAGE_MISSING");
        await page.GotoAsync(wrongConversation.CanonicalUrl).ConfigureAwait(false);
        await page.WaitForTimeoutAsync(500).ConfigureAwait(false);
        var adapter = new CountingAdapter(_adapter);
        var provider = new BrowserChatProvider(_registry, adapter, new InMemoryDispatchLedger(), new WrongChatGuard(), _gate);
        var request = Request(runtime, taskId, $"wrong-chat-{Guid.NewGuid():N}", LivePilotPrompt.Create(taskId, int.TryParse(runtime.WorkerSlotId, out var slot) ? slot : 1));
        var result = await provider.SendAsync(runtime.RuntimeId, request, cancellationToken).ConfigureAwait(false);
        await page.GotoAsync(correctConversation.CanonicalUrl).ConfigureAwait(false);
        return (result.Outcome == BrowserDispatchOutcome.NotSent && adapter.SubmitCalls == 0 && result.Reason == "PROVIDER_CONVERSATION_MISMATCH", result.Reason);
    }

    public async Task<(bool NoDuplicate, SendReconciliationResult Reconciliation, BrowserDispatchResult First, BrowserDispatchResult Second)> RunControlledUncertainSendAsync(
        BrowserRuntimeRecord runtime,
        string taskId,
        int workerSlot,
        CancellationToken cancellationToken = default)
    {
        var ledger = new RecordingDispatchLedger();
        var faultAdapter = new FaultInjectingSubmissionAdapter(_adapter);
        var provider = new BrowserChatProvider(_registry, faultAdapter, ledger, new WrongChatGuard(), _gate);
        var prompt = LivePilotPrompt.Create(taskId, workerSlot);
        var dispatchId = $"uncertain-{taskId}-{Guid.NewGuid():N}";
        var request = Request(runtime, taskId, dispatchId, prompt);
        var probe = new LiveConversationEvidenceProbe(_host);
        probe.Remember(dispatchId, prompt);

        var first = await provider.SendAsync(runtime.RuntimeId, request, cancellationToken).ConfigureAwait(false);
        var second = await provider.SendAsync(runtime.RuntimeId, request, cancellationToken).ConfigureAwait(false);
        var entry = await ledger.GetAsync(dispatchId, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("UNCERTAIN_DISPATCH_LEDGER_ENTRY_MISSING");
        var coordinator = new DispatchReconciliationCoordinator(new UncertainSendReconciler(probe), ledger);
        var reconciliation = await coordinator.ReconcileAsync(runtime.RuntimeId, entry, cancellationToken).ConfigureAwait(false);
        var safe = first.Outcome == BrowserDispatchOutcome.SubmittedUnknown && second.Outcome == BrowserDispatchOutcome.DuplicateBlocked && reconciliation.RetrySafety == RetrySafety.NotSafe;
        return (safe, reconciliation, first, second);
    }

    public async Task<SessionActionResult> CrashOwnedRuntimeAndRecoverLogicalIdentityAsync(string runtimeId, CancellationToken cancellationToken = default)
    {
        var before = await _registry.GetAsync(runtimeId, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("RUNTIME_NOT_FOUND");
        var killed = await _sessions.KillAsync(runtimeId, cancellationToken).ConfigureAwait(false);
        if (!killed.Succeeded) return killed;
        var recovered = await _sessions.RecoverOrphanAsync(runtimeId, cancellationToken).ConfigureAwait(false);
        if (!recovered.Succeeded || recovered.Runtime is null) return recovered;
        if (!StringComparer.Ordinal.Equals(before.ProjectRunId, recovered.Runtime.ProjectRunId) || !StringComparer.Ordinal.Equals(before.LogicalAgentId, recovered.Runtime.LogicalAgentId) || !StringComparer.Ordinal.Equals(before.TaskId, recovered.Runtime.TaskId) || !StringComparer.Ordinal.Equals(before.ConversationIdentity, recovered.Runtime.ConversationIdentity))
            return new(false, recovered.RuntimeId, "LOGICAL_IDENTITY_LOST_DURING_CRASH_RECOVERY", recovered.Runtime);
        return recovered;
    }

    public Task<KillAllResult> ShutdownAsync(CancellationToken cancellationToken = default) => _sessions.KillAllPccSessionsAsync(cancellationToken);

    public async Task<LivePilotArtifact> BuildArtifactAsync(string scenario, string sourceSha, LivePilotAcceptanceState state, int level, IReadOnlyList<string> transitions, IReadOnlyList<long> timings, IReadOnlyList<string> failures, IReadOnlyList<string> evidence, CancellationToken cancellationToken = default)
    {
        var runtimes = await _registry.ListAsync(cancellationToken).ConfigureAwait(false);
        var sessions = new List<LiveSessionMonitorEvidence>();
        foreach (var runtime in runtimes.Where(x => x.State != BrowserSessionState.Killed))
        {
            var snapshot = await _adapter.InspectAsync(runtime, Expectation(runtime), cancellationToken).ConfigureAwait(false);
            sessions.Add(LiveSessionMonitorEvidenceFactory.Create(runtime, snapshot));
        }
        return new(scenario, sourceSha, AdapterVersion, state, level, sessions, transitions, timings, failures, evidence);
    }

    private async Task<(LivePilotAcceptanceState State, string Reason, ChatGptSemanticSnapshot Snapshot)> WaitForAuthenticationAsync(
        BrowserRuntimeRecord runtime,
        BrowserDispatchExpectation expectation,
        bool allowManualLogin,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        var foregrounded = false;
        ChatGptSemanticSnapshot snapshot = await _adapter.InspectAsync(runtime, expectation, cancellationToken).ConfigureAwait(false);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var decision = _login.Evaluate(snapshot);
            if (decision.State == LivePilotAcceptanceState.Pass)
            {
                if (foregrounded) await _sessions.HideAsync(runtime.RuntimeId, cancellationToken).ConfigureAwait(false);
                return (decision.State, decision.Reason, snapshot);
            }
            if (decision.State == LivePilotAcceptanceState.BlockedChallenge)
                return (decision.State, decision.Reason, snapshot);
            if (decision.State == LivePilotAcceptanceState.BlockedLogin)
            {
                if (!allowManualLogin) return (decision.State, decision.Reason, snapshot);
                if (!foregrounded)
                {
                    var brought = await _sessions.BringToFrontAsync(runtime.RuntimeId, cancellationToken).ConfigureAwait(false);
                    if (!brought.Succeeded) return (LivePilotAcceptanceState.Fail, brought.Reason, snapshot);
                    foregrounded = true;
                }
            }
            else if (decision.State == LivePilotAcceptanceState.Fail)
            {
                return (decision.State, decision.Reason, snapshot);
            }
            await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
            snapshot = await _adapter.InspectAsync(runtime, expectation, cancellationToken).ConfigureAwait(false);
        }
        return (LivePilotAcceptanceState.BlockedLogin, "MANUAL_LOGIN_TIMEOUT", snapshot);
    }

    private static BrowserDispatchExpectation Expectation(BrowserRuntimeRecord runtime) =>
        new(runtime.ProjectRunId, runtime.LogicalAgentId, runtime.TaskId ?? "live-task", runtime.ConversationIdentity ?? "unknown", runtime.ProviderConversationIdentity ?? "unknown");

    private static BrowserDispatchRequest Request(BrowserRuntimeRecord runtime, string taskId, string dispatchId, string prompt) =>
        new(dispatchId, runtime.ProjectRunId, runtime.LogicalAgentId, taskId,
            runtime.ConversationIdentity ?? throw new InvalidOperationException("CONVERSATION_IDENTITY_REQUIRED"),
            runtime.ProviderConversationIdentity ?? throw new InvalidOperationException("PROVIDER_CONVERSATION_IDENTITY_REQUIRED"), prompt);

    private static string SafeEvidenceCode(string evidence)
    {
        var normalized = string.Join(' ', evidence.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 120 ? normalized : normalized[..120];
    }

    private sealed class CountingAdapter(IChatGptBrowserAdapter inner) : IChatGptBrowserAdapter
    {
        public int SubmitCalls { get; private set; }
        public string AdapterVersion => inner.AdapterVersion;
        public Task<ChatGptSemanticSnapshot> InspectAsync(BrowserRuntimeRecord runtime, BrowserDispatchExpectation expectation, CancellationToken cancellationToken = default) => inner.InspectAsync(runtime, expectation, cancellationToken);
        public async Task<AdapterSubmissionResult> SubmitAsync(BrowserRuntimeRecord runtime, BrowserDispatchExpectation expectation, string prompt, CancellationToken cancellationToken = default)
        {
            SubmitCalls++;
            return await inner.SubmitAsync(runtime, expectation, prompt, cancellationToken).ConfigureAwait(false);
        }
    }
}
