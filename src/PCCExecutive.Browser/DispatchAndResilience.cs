using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace PCCExecutive.Browser;

public sealed class InMemoryDispatchLedger : IDispatchLedger
{
    private readonly ConcurrentDictionary<string, DispatchLedgerEntry> _entries = new(StringComparer.Ordinal);

    public Task<DispatchReservation> ReserveAsync(string dispatchId, string contentHash, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        while (true)
        {
            if (!_entries.TryGetValue(dispatchId, out var existing))
            {
                var created = new DispatchLedgerEntry(dispatchId, contentHash, DispatchState.Prepared, DateTimeOffset.UtcNow);
                if (_entries.TryAdd(dispatchId, created)) return Task.FromResult(new DispatchReservation(DispatchReservationStatus.New, created, "DISPATCH_RESERVED"));
                continue;
            }
            if (!StringComparer.Ordinal.Equals(existing.ContentHash, contentHash)) return Task.FromResult(new DispatchReservation(DispatchReservationStatus.ContentConflict, existing, "DISPATCH_ID_CONTENT_HASH_CONFLICT"));
            if (existing.State is DispatchState.Prepared or DispatchState.SafeRetry) return Task.FromResult(new DispatchReservation(DispatchReservationStatus.RetryAllowed, existing, existing.State == DispatchState.Prepared ? "PREPARED_REPLAY_SAME_DISPATCH_ALLOWED" : "SAFE_RETRY_EXPLICITLY_ALLOWED"));
            return Task.FromResult(new DispatchReservation(DispatchReservationStatus.DuplicateBlocked, existing, $"DISPATCH_ALREADY_{existing.State.ToString().ToUpperInvariant()}"));
        }
    }

    public Task UpdateAsync(string dispatchId, DispatchState state, string? reconciliationEvidence = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_entries.TryGetValue(dispatchId, out var existing)) throw new KeyNotFoundException($"Dispatch '{dispatchId}' is not reserved.");
        _entries[dispatchId] = existing with { State = state, UpdatedAt = DateTimeOffset.UtcNow, ReconciliationEvidence = reconciliationEvidence };
        return Task.CompletedTask;
    }

    public Task<DispatchLedgerEntry?> GetAsync(string dispatchId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); _entries.TryGetValue(dispatchId, out var entry); return Task.FromResult(entry);
    }
}

public static class FinalPreEnterAuthorization
{
    public static async Task<PreEnterAuthorizationDecision> AuthorizeAsync(
        IBrowserRuntimeRegistry runtimes,
        IOwnershipProofService ownership,
        string runtimeId,
        BrowserDispatchExpectation expected,
        CancellationToken cancellationToken = default)
    {
        var current = await runtimes.GetAsync(runtimeId, cancellationToken).ConfigureAwait(false);
        if (current is null) return Deny("FINAL_RUNTIME_NOT_FOUND");
        if (!StringComparer.Ordinal.Equals(current.ProjectRunId, expected.ProjectRunId)) return Deny("FINAL_PROJECT_RUN_MISMATCH");
        if (!StringComparer.Ordinal.Equals(current.LogicalAgentId, expected.LogicalAgentId)) return Deny("FINAL_LOGICAL_AGENT_MISMATCH");
        if (!StringComparer.Ordinal.Equals(current.WorkerSlotId, expected.WorkerSlotId)) return Deny("FINAL_WORKER_SLOT_MISMATCH");
        if (!StringComparer.Ordinal.Equals(current.TaskId, expected.TaskId)) return Deny("FINAL_TASK_MISMATCH");
        if (!StringComparer.Ordinal.Equals(current.ConversationIdentity, expected.ConversationIdentity)) return Deny("FINAL_CONVERSATION_MISMATCH");
        if (!StringComparer.OrdinalIgnoreCase.Equals(current.ProviderConversationIdentity, expected.ProviderConversationIdentity)) return Deny("FINAL_PROVIDER_CONVERSATION_MISMATCH");
        var proof = await ownership.ProveAsync(current, cancellationToken).ConfigureAwait(false);
        if (!proof.IsProven) return new(false, "FINAL_PCC_OWNERSHIP_NOT_PROVEN", new[] { proof.Reason });
        return new(true, "FINAL_PRE_ENTER_AUTHORIZED", new[] { "project-run:match", "logical-agent:match", $"worker-slot:{expected.WorkerSlotId ?? "MANAGER"}", "task:match", "conversation:match", "provider-conversation:match", "ownership:proven" });

        PreEnterAuthorizationDecision Deny(string reason) => new(false, reason, new[] { $"runtime:{runtimeId}" });
    }
}

public sealed class BrowserChatProvider
{
    private readonly IBrowserRuntimeRegistry _runtimes; private readonly IChatGptBrowserAdapter _adapter; private readonly IDispatchLedger _ledger; private readonly WrongChatGuard _wrongChatGuard; private readonly GlobalBrowserSendGate _globalGate; private readonly IOwnershipProofService _ownership; private readonly ConcurrentDictionary<string, SemaphoreSlim> _dispatchGates = new(StringComparer.Ordinal);
    public BrowserChatProvider(IBrowserRuntimeRegistry runtimes, IChatGptBrowserAdapter adapter, IDispatchLedger ledger, WrongChatGuard wrongChatGuard, GlobalBrowserSendGate globalGate, IOwnershipProofService ownership) { _runtimes = runtimes; _adapter = adapter; _ledger = ledger; _wrongChatGuard = wrongChatGuard; _globalGate = globalGate; _ownership = ownership ?? throw new ArgumentNullException(nameof(ownership)); }

    public async Task<BrowserDispatchResult> SendAsync(string runtimeId, BrowserDispatchRequest request, CancellationToken cancellationToken = default, Func<CancellationToken, Task>? beforeSubmit = null)
    {
        var gate = _globalGate.Snapshot;
        if (gate.IsPaused) return new(request.DispatchId, BrowserDispatchOutcome.NotSent, DispatchState.Prepared, "GLOBAL_SEND_PAUSED", new[] { gate.Reason ?? "global-pause" });
        var runtime = await _runtimes.GetAsync(runtimeId, cancellationToken).ConfigureAwait(false);
        if (runtime is null) return new(request.DispatchId, BrowserDispatchOutcome.NotSent, DispatchState.Prepared, "RUNTIME_NOT_FOUND", Array.Empty<string>());
        var expected = new BrowserDispatchExpectation(request.ProjectRunId, request.LogicalAgentId, request.TaskId, request.ConversationIdentity, request.ProviderConversationIdentity, request.WorkerSlotId);
        var snapshot = await _adapter.InspectAsync(runtime, expected, cancellationToken).ConfigureAwait(false);
        var guard = _wrongChatGuard.Evaluate(runtime, expected, snapshot);
        if (!guard.MaySend) return new(request.DispatchId, BrowserDispatchOutcome.NotSent, DispatchState.Prepared, guard.Reason, guard.Evidence);
        var proof = await _ownership.ProveAsync(runtime, cancellationToken).ConfigureAwait(false);
        if (!proof.IsProven) return new(request.DispatchId, BrowserDispatchOutcome.NotSent, DispatchState.Prepared, "PCC_OWNERSHIP_NOT_PROVEN", guard.Evidence.Append(proof.Reason).ToArray());
        if (beforeSubmit is not null) await beforeSubmit(cancellationToken).ConfigureAwait(false);
        var contentHash = request.ContentHash ?? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Prompt)));
        var dispatchGate = _dispatchGates.GetOrAdd(request.DispatchId, static _ => new SemaphoreSlim(1, 1));
        await dispatchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
        var reservation = await _ledger.ReserveAsync(request.DispatchId, contentHash, cancellationToken).ConfigureAwait(false);
        if (reservation.Status is DispatchReservationStatus.DuplicateBlocked or DispatchReservationStatus.ContentConflict) return new(request.DispatchId, BrowserDispatchOutcome.DuplicateBlocked, reservation.Entry.State, reservation.Reason, new[] { $"content-hash:{reservation.Entry.ContentHash}" });
        AdapterSubmissionResult submission;
        if (_adapter is IPhysicalSubmitAuthorizationAdapter physicalAdapter)
        {
            submission = await physicalAdapter.SubmitAuthorizedAsync(runtime, expected, request.Prompt, async ct =>
            {
                var authorization = await FinalPreEnterAuthorization.AuthorizeAsync(_runtimes, _ownership, runtimeId, expected, ct).ConfigureAwait(false);
                if (authorization.Authorized)
                    await _ledger.UpdateAsync(request.DispatchId, DispatchState.Submitting, "FINAL_PRE_ENTER_AUTHORIZED", ct).ConfigureAwait(false);
                return authorization;
            }, cancellationToken).ConfigureAwait(false);
            if (!submission.Triggered && string.Equals(submission.Reason, "PRE_ENTER_AUTHORIZATION_DENIED", StringComparison.Ordinal))
                return new(request.DispatchId, BrowserDispatchOutcome.NotSent, DispatchState.Prepared, submission.Reason, submission.Evidence);
        }
        else
        {
            await _ledger.UpdateAsync(request.DispatchId, DispatchState.Submitting, cancellationToken: cancellationToken).ConfigureAwait(false);
            submission = await _adapter.SubmitAsync(runtime, expected, request.Prompt, cancellationToken).ConfigureAwait(false);
        }
        if (submission.SubmittedUnknown) { await _ledger.UpdateAsync(request.DispatchId, DispatchState.SubmittedUnknown, string.Join(";", submission.Evidence), cancellationToken).ConfigureAwait(false); return new(request.DispatchId, BrowserDispatchOutcome.SubmittedUnknown, DispatchState.SubmittedUnknown, "SUBMITTED_UNKNOWN", submission.Evidence); }
        if (submission.ProvenSubmitted)
        {
            if (string.Equals(request.ProviderConversationIdentity, "NEW", StringComparison.OrdinalIgnoreCase))
            {
                var providerIdentity = await _adapter.GetCurrentConversationIdentityAsync(runtime, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(providerIdentity))
                {
                    await _ledger.UpdateAsync(request.DispatchId, DispatchState.SubmittedUnknown, "NEW_CONVERSATION_IDENTITY_NOT_PROVEN", cancellationToken).ConfigureAwait(false);
                    return new(request.DispatchId, BrowserDispatchOutcome.SubmittedUnknown, DispatchState.SubmittedUnknown, "SUBMITTED_UNKNOWN", submission.Evidence.Append("new-conversation-identity:unproven").ToArray());
                }
                runtime = runtime with { ProviderConversationIdentity = providerIdentity, LastActivityAt = DateTimeOffset.UtcNow };
                await _runtimes.UpsertAsync(runtime, cancellationToken).ConfigureAwait(false);
            }
            await _ledger.UpdateAsync(request.DispatchId, DispatchState.Submitted, string.Join(";", submission.Evidence), cancellationToken).ConfigureAwait(false);
            return new(request.DispatchId, BrowserDispatchOutcome.Submitted, DispatchState.Submitted, submission.Reason, submission.Evidence);
        }
        await _ledger.UpdateAsync(request.DispatchId, DispatchState.SafeRetry, string.Join(";", submission.Evidence), cancellationToken).ConfigureAwait(false);
        return new(request.DispatchId, BrowserDispatchOutcome.NotSent, DispatchState.SafeRetry, submission.Reason, submission.Evidence);
        }
        finally
        {
            dispatchGate.Release();
            if (dispatchGate.CurrentCount == 1) _dispatchGates.TryRemove(request.DispatchId, out _);
        }
    }
}

public sealed class BrowserDispatchScheduler
{
    public DispatchTimingDecision Evaluate(DateTimeOffset now, DateTimeOffset? lastDispatchAt, int activeWorkers, DispatchSchedulerOptions options, GlobalSendGateSnapshot globalGate, ChatGptResilienceState recentState = ChatGptResilienceState.Ready)
    {
        options.Validate();
        if (options.Mode != DispatchMode.AutomaticStaged) return new(false, null, options.Mode == DispatchMode.Manual ? "MANUAL_DISPATCH_REQUIRED" : "ASSISTED_DISPATCH_REQUIRES_CONFIRMATION");
        if (globalGate.IsPaused) return new(false, globalGate.ResumeNotBefore, "GLOBAL_SEND_PAUSED");
        if (activeWorkers >= options.MaximumWorkers) return new(false, null, "MAXIMUM_WORKERS_REACHED");
        if (lastDispatchAt is null) return new(true, now, "FIRST_STAGED_DISPATCH_READY");
        var multiplier = options.AdaptivePacing ? recentState switch { ChatGptResilienceState.Slow => 2d, ChatGptResilienceState.Throttled => 3d, ChatGptResilienceState.TempError => 2.5d, _ => 1d } : 1d;
        var eligible = lastDispatchAt.Value + TimeSpan.FromTicks((long)(options.EffectiveBaseInterval.Ticks * multiplier));
        return now >= eligible ? new(true, eligible, "STAGED_INTERVAL_SATISFIED") : new(false, eligible, "STAGED_INTERVAL_PENDING");
    }
}

public sealed class ChatGptResilienceClassifier
{
    private readonly TimeSpan _slowAfter; private readonly TimeSpan _stuckAfter;
    public ChatGptResilienceClassifier(TimeSpan? slowAfter = null, TimeSpan? stuckAfter = null) { _slowAfter = slowAfter ?? TimeSpan.FromMinutes(2); _stuckAfter = stuckAfter ?? TimeSpan.FromMinutes(8); }
    public ResilienceDecision Classify(ChatGptSemanticSnapshot s, TimeSpan elapsed)
    {
        if (s.Auth.State == AuthState.Challenge) return new(ChatGptResilienceState.LoginRequired, FaultScope.Global, true, true, "CHALLENGE_REQUIRES_MANUAL_RESOLUTION");
        if (s.Auth.State == AuthState.LoginRequired) return new(ChatGptResilienceState.LoginRequired, FaultScope.Global, true, true, "LOGIN_REQUIRED");
        if (s.Health.State == PageHealth.Offline) return new(ChatGptResilienceState.Offline, FaultScope.Global, true, false, "NETWORK_OFFLINE");
        if (s.Health.State == PageHealth.RateLimited) return new(ChatGptResilienceState.RateLimited, FaultScope.Global, true, false, "RATE_LIMITED");
        if (s.ResponseCompleteness == ResponseCompleteness.Partial) return new(ChatGptResilienceState.PartialResponse, FaultScope.PerSession, false, false, "PARTIAL_RESPONSE_CAPTURED");
        if (s.Health.State == PageHealth.TempError) { var global = s.Health.Evidence.Any(e => e.Contains("sending-too-quickly", StringComparison.OrdinalIgnoreCase) || e.Contains("account", StringComparison.OrdinalIgnoreCase) || e.Contains("global", StringComparison.OrdinalIgnoreCase)); return new(ChatGptResilienceState.TempError, global ? FaultScope.Global : FaultScope.PerSession, global, false, "TEMP_ERROR"); }
        if (s.Generation.State == GenerationState.Generating && elapsed >= _stuckAfter) return new(ChatGptResilienceState.Stuck, FaultScope.PerSession, false, false, "SESSION_GENERATION_STUCK");
        if (s.Generation.State == GenerationState.Generating && elapsed >= _slowAfter) return new(ChatGptResilienceState.Slow, FaultScope.PerSession, false, false, "SESSION_GENERATION_SLOW");
        if (s.Generation.State == GenerationState.Generating) return new(ChatGptResilienceState.Generating, FaultScope.PerSession, false, false, "GENERATION_IN_PROGRESS");
        if (s.Input.State == InputState.Unknown || s.Auth.State == AuthState.Unknown || s.Conversation.State == ConversationMatch.Unknown || s.Health.State == PageHealth.Unknown) return new(ChatGptResilienceState.Paused, FaultScope.PerSession, false, false, "BROWSER_ADAPTER_UNCERTAIN");
        if (s.Generation.State == GenerationState.Complete && s.ResponseCompleteness == ResponseCompleteness.Complete) return new(ChatGptResilienceState.Done, FaultScope.None, false, false, "RESPONSE_COMPLETE");
        if (s.Input.State == InputState.Ready && s.Auth.State == AuthState.Authenticated && s.Health.State == PageHealth.Healthy) return new(ChatGptResilienceState.Ready, FaultScope.None, false, false, "READY");
        return new(ChatGptResilienceState.Paused, FaultScope.PerSession, false, false, "STATE_NOT_PROVEN_SAFE");
    }
}

public sealed class GlobalBrowserSendGate
{
    private readonly object _sync = new(); private GlobalSendGateSnapshot _snapshot = new(false, null, null, null);
    public GlobalSendGateSnapshot Snapshot { get { lock (_sync) return _snapshot; } }
    public void Apply(ResilienceDecision decision, DateTimeOffset now, TimeSpan? cooldown = null) { if (decision.Scope != FaultScope.Global || !decision.PauseUnsafeNewSends) return; lock (_sync) _snapshot = new(true, decision.Reason, now, cooldown is null ? null : now + cooldown.Value); }
    public bool TryResume(DateTimeOffset now, string reason) { lock (_sync) { if (!_snapshot.IsPaused) return true; if (_snapshot.ResumeNotBefore is not null && now < _snapshot.ResumeNotBefore.Value) return false; _snapshot = new(false, reason, null, null); return true; } }
}

public sealed class ConservativeCooldownPolicy
{
    private readonly TimeSpan _minimum; private readonly TimeSpan _maximum;
    public ConservativeCooldownPolicy(TimeSpan? minimum = null, TimeSpan? maximum = null) { _minimum = minimum ?? TimeSpan.FromSeconds(30); _maximum = maximum ?? TimeSpan.FromMinutes(15); }
    public TimeSpan GetCooldown(int count, TimeSpan? explicitServiceGuidance = null) { if (explicitServiceGuidance is not null) return Clamp(explicitServiceGuidance.Value); var exponent = Math.Clamp(count - 1, 0, 8); var ticks = _minimum.Ticks * Math.Pow(2, exponent); return Clamp(TimeSpan.FromTicks((long)Math.Min(ticks, long.MaxValue))); }
    private TimeSpan Clamp(TimeSpan value) => value < _minimum ? _minimum : value > _maximum ? _maximum : value;
}
