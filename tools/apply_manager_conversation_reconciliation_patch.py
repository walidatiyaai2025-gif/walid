from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def replace_once(path: Path, old: str, new: str) -> None:
    text = path.read_text(encoding="utf-8")
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"Expected one match in {path}, found {count}")
    path.write_text(text.replace(old, new, 1), encoding="utf-8")


runtime = ROOT / "src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs"

old_result = '''        if (result.Accepted)
        {
            var updatedRuntime = await _runtimeRegistry.GetAsync(runtime.RuntimeId, cancellationToken).ConfigureAwait(false);
            if (updatedRuntime?.ProviderConversationIdentity is { Length: > 0 } providerIdentity)
                await _store.SaveBrowserConversationAsync(new ConversationRecord { ConversationId = logicalConversation, LogicalAgentId = managerAgentId.ToString(), ProjectRunId = run.Id.ToString(), Sequence = 1, UrlOrProviderIdentity = providerIdentity, CreatedAt = DateTimeOffset.UtcNow, State = ConversationLifecycleState.Active }, cancellationToken).ConfigureAwait(false);
        }
        await _store.SaveCheckpointAsync(new DurableCheckpoint($"manager-dispatch:{result.DispatchId}", run.Id.ToString(), "manager-dispatch-v1", JsonSerializer.Serialize(new { request.DispatchId, request.ContentHash, result.Accepted, result.IsUncertain, result.ErrorCode, result.ProviderEvidence }), DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
        _latestManagerHandoff = result.IsUncertain
            ? $"SUBMITTED_UNKNOWN — Manager dispatch {result.DispatchId} requires reconciliation before retry."
            : result.Accepted
                ? $"Manager request {result.DispatchId} submitted. Waiting for a complete structured response."
                : $"Manager send stopped safely: {result.ErrorCode ?? result.ProviderEvidence ?? "unknown provider state"}.";
        _autopilot = result.Accepted ? "PLANNING" : result.IsUncertain ? "WAITING_FOR_EVIDENCE" : "READY";
        CaptureProviderAttention(result.ErrorCode, runtime.RuntimeId, "Manager ChatGPT session");
        if (result.Accepted && _settings.AutoResume) EnsureAutopilotLoop();
        await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
'''

new_result = '''        if (result.Accepted)
        {
            var updatedRuntime = await _runtimeRegistry.GetAsync(runtime.RuntimeId, cancellationToken).ConfigureAwait(false);
            if (updatedRuntime?.ProviderConversationIdentity is { Length: > 0 } providerIdentity && !string.Equals(providerIdentity, "NEW", StringComparison.OrdinalIgnoreCase))
                await _store.SaveBrowserConversationAsync(new ConversationRecord { ConversationId = logicalConversation, LogicalAgentId = managerAgentId.ToString(), ProjectRunId = run.Id.ToString(), Sequence = 1, UrlOrProviderIdentity = providerIdentity, CreatedAt = DateTimeOffset.UtcNow, State = ConversationLifecycleState.Active }, cancellationToken).ConfigureAwait(false);
        }
        await _store.SaveCheckpointAsync(new DurableCheckpoint($"manager-dispatch:{result.DispatchId}", run.Id.ToString(), "manager-dispatch-v1", JsonSerializer.Serialize(new { request.DispatchId, request.ContentHash, result.Accepted, result.IsUncertain, result.ErrorCode, result.ProviderEvidence }), DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
        var postSendRuntime = await _runtimeRegistry.GetAsync(runtime.RuntimeId, cancellationToken).ConfigureAwait(false);
        var providerConversationPending = result.Accepted &&
            (string.IsNullOrWhiteSpace(postSendRuntime?.ProviderConversationIdentity) || string.Equals(postSendRuntime.ProviderConversationIdentity, "NEW", StringComparison.OrdinalIgnoreCase));
        _latestManagerHandoff = providerConversationPending
            ? $"RECONCILING_CONVERSATION — Manager request {result.DispatchId} is accepted. Waiting for ChatGPT to expose the stable conversation identity; no resend will occur."
            : result.IsUncertain
                ? $"SUBMITTED_UNKNOWN — Manager dispatch {result.DispatchId} requires reconciliation before retry."
                : result.Accepted
                    ? $"Manager request {result.DispatchId} submitted. Waiting for a complete structured response."
                    : $"Manager send stopped safely: {result.ErrorCode ?? result.ProviderEvidence ?? "unknown provider state"}.";
        _autopilot = providerConversationPending ? "RECONCILING_CONVERSATION" : result.Accepted ? "PLANNING" : result.IsUncertain ? "WAITING_FOR_EVIDENCE" : "READY";
        CaptureProviderAttention(result.ErrorCode, runtime.RuntimeId, "Manager ChatGPT session");
        if (result.Accepted && _settings.AutoResume) EnsureAutopilotLoop();
        await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
'''
replace_once(runtime, old_result, new_result)

old_identity = '''        if (string.IsNullOrWhiteSpace(runtime.TaskId) || string.IsNullOrWhiteSpace(runtime.ConversationIdentity) || string.IsNullOrWhiteSpace(runtime.ProviderConversationIdentity) || string.Equals(runtime.ProviderConversationIdentity, "NEW", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Manager conversation identity is not yet proven. Wait for submission reconciliation before reading a response.");

        var expected = new BrowserDispatchExpectation(run.Id.ToString(), managerAgentId.ToString(), runtime.TaskId, runtime.ConversationIdentity, runtime.ProviderConversationIdentity);
'''

new_identity = '''        if (string.IsNullOrWhiteSpace(runtime.TaskId) || string.IsNullOrWhiteSpace(runtime.ConversationIdentity))
            throw new InvalidOperationException("Manager dispatch binding is incomplete before response reconciliation.");

        if (string.IsNullOrWhiteSpace(runtime.ProviderConversationIdentity) || string.Equals(runtime.ProviderConversationIdentity, "NEW", StringComparison.OrdinalIgnoreCase))
        {
            var proof = await _ownership.ProveAsync(runtime, cancellationToken).ConfigureAwait(false);
            if (!proof.IsProven)
                throw new InvalidOperationException($"Manager conversation reconciliation refused because PCC ownership is not proven: {proof.Reason}.");

            var providerIdentity = await _browserAdapter.GetCurrentConversationIdentityAsync(runtime, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(providerIdentity) || string.Equals(providerIdentity, "NEW", StringComparison.OrdinalIgnoreCase))
            {
                _autopilot = "RECONCILING_CONVERSATION";
                _latestManagerHandoff = "RECONCILING_CONVERSATION — Manager submission is already accepted, but ChatGPT has not exposed a stable conversation identity yet. PCC is polling automatically; no resend and no Loop Guard error.";
                _recovery.Insert(0, new RecoveryEventSummary(DateTimeOffset.UtcNow, "RECONCILING_CONVERSATION", "Provider conversation identity is pending after accepted Manager submission.", true));
                await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            runtime = runtime with { ProviderConversationIdentity = providerIdentity, LastActivityAt = DateTimeOffset.UtcNow };
            await _runtimeRegistry.UpsertAsync(runtime, cancellationToken).ConfigureAwait(false);
            await _store.SaveBrowserConversationAsync(new ConversationRecord { ConversationId = runtime.ConversationIdentity!, LogicalAgentId = managerAgentId.ToString(), ProjectRunId = run.Id.ToString(), Sequence = 1, UrlOrProviderIdentity = providerIdentity, CreatedAt = DateTimeOffset.UtcNow, State = ConversationLifecycleState.Active }, cancellationToken).ConfigureAwait(false);
            _recovery.Insert(0, new RecoveryEventSummary(DateTimeOffset.UtcNow, "CONVERSATION_READY", $"Manager provider conversation identity proven: {providerIdentity}", true));
        }

        if (_autopilot == "RECONCILING_CONVERSATION")
            _autopilot = "PLANNING";
        var providerConversationIdentity = runtime.ProviderConversationIdentity!;
        var expected = new BrowserDispatchExpectation(run.Id.ToString(), managerAgentId.ToString(), runtime.TaskId, runtime.ConversationIdentity, providerConversationIdentity);
'''
replace_once(runtime, old_identity, new_identity)

old_loop = '''                    else if (_autopilot is "PLANNING" or "MANAGER_REVIEW")
                        await ReconcileManagerResponseAsync(cancellationToken).ConfigureAwait(false);
'''
new_loop = '''                    else if (_autopilot is "PLANNING" or "MANAGER_REVIEW" or "RECONCILING_CONVERSATION")
                        await ReconcileManagerResponseAsync(cancellationToken).ConfigureAwait(false);
'''
replace_once(runtime, old_loop, new_loop)

# Regression contract for the exact live failure: accepted Manager submission with provider identity still NEW
# must remain a reconciliation wait, never a runtime exception / Loop Guard failure.
test = ROOT / "tests/PCCExecutive.App.Tests/ProductionRecoveryWiringContractTests.cs"
marker = '''    [Fact]
    public void Normal_disposal_invokes_safe_shutdown_coordinator()
'''
insert = '''    [Fact]
    public void Manager_provider_conversation_identity_pending_is_reconciled_without_runtime_stall()
    {
        var source = ReadSource("src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs");
        var start = Slice(source, "private async Task StartManagerAsync", "private string BuildManagerPrompt");
        var reconcile = Slice(source, "private async Task ReconcileManagerResponseAsync", "private async Task StartDispatchAsync");
        var loop = Slice(source, "private async Task RunAutopilotLoopAsync", "private async Task RunSessionActionAsync");

        Assert.Contains("RECONCILING_CONVERSATION", start, StringComparison.Ordinal);
        Assert.Contains("_browserAdapter.GetCurrentConversationIdentityAsync(runtime", reconcile, StringComparison.Ordinal);
        Assert.Contains("no resend and no Loop Guard error", reconcile, StringComparison.Ordinal);
        Assert.DoesNotContain("Manager conversation identity is not yet proven", reconcile, StringComparison.Ordinal);
        Assert.Contains("_autopilot is \"PLANNING\" or \"MANAGER_REVIEW\" or \"RECONCILING_CONVERSATION\"", loop, StringComparison.Ordinal);
    }

    [Fact]
    public void Normal_disposal_invokes_safe_shutdown_coordinator()
'''
replace_once(test, marker, insert)
