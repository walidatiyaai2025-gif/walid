$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot

function Replace-Exact([string]$Path, [string]$Old, [string]$New) {
    $full = Join-Path $root $Path
    $text = [IO.File]::ReadAllText($full)
    if (-not $text.Contains($Old)) { throw "Expected text not found in $Path`n--- expected ---`n$Old" }
    [IO.File]::WriteAllText($full, $text.Replace($Old, $New), [Text.UTF8Encoding]::new($false))
}

Replace-Exact 'src/PCCExecutive.Application/Foundation.cs' `
'public sealed record AgentRequest(ProjectRunId ProjectRunId, LogicalAgentId LogicalAgentId, ConversationId ConversationId, DispatchId DispatchId, string Content, string ContentHash);' `
'public sealed record AgentRequest(ProjectRunId ProjectRunId, LogicalAgentId LogicalAgentId, ConversationId ConversationId, DispatchId DispatchId, string Content, string ContentHash, WorkerSlotId? WorkerSlotId = null, TaskId? TaskId = null, WaveId? WaveId = null);'

Replace-Exact 'src/PCCExecutive.Application/ManagerWorkerOrchestration.cs' `
'var request = new AgentRequest(projectRunId, binding.LogicalAgentId, binding.ConversationId, DispatchId.New(), content, hash);' `
'var request = new AgentRequest(projectRunId, binding.LogicalAgentId, binding.ConversationId, DispatchId.New(), content, hash, binding.SlotId, task.Id, plan.WaveId);'

Replace-Exact 'src/PCCExecutive.Browser/BrowserContracts.cs' @'
public sealed record BrowserDispatchExpectation(
    string ProjectRunId,
    string LogicalAgentId,
    string TaskId,
    string ConversationIdentity,
    string ProviderConversationIdentity);
'@ @'
public sealed record BrowserDispatchExpectation(
    string ProjectRunId,
    string LogicalAgentId,
    string TaskId,
    string ConversationIdentity,
    string ProviderConversationIdentity,
    string? WorkerSlotId = null);
'@

Replace-Exact 'src/PCCExecutive.Browser/BrowserContracts.cs' @'
public sealed record BrowserDispatchRequest(
    string DispatchId,
    string ProjectRunId,
    string LogicalAgentId,
    string TaskId,
    string ConversationIdentity,
    string ProviderConversationIdentity,
    string Prompt,
    string? ContentHash = null);
'@ @'
public sealed record BrowserDispatchRequest(
    string DispatchId,
    string ProjectRunId,
    string LogicalAgentId,
    string TaskId,
    string ConversationIdentity,
    string ProviderConversationIdentity,
    string Prompt,
    string? ContentHash = null,
    string? WorkerSlotId = null);
'@

Replace-Exact 'src/PCCExecutive.Browser/ChatGptBrowserAdapter.cs' `
'        if (!StringComparer.Ordinal.Equals(runtime.LogicalAgentId, expected.LogicalAgentId)) return Deny("LOGICAL_AGENT_MISMATCH");
        evidence.Add("logical-agent:match");
        if (string.IsNullOrWhiteSpace(runtime.TaskId)) return new(false, "TASK_BINDING_UNKNOWN", evidence);' `
'        if (!StringComparer.Ordinal.Equals(runtime.LogicalAgentId, expected.LogicalAgentId)) return Deny("LOGICAL_AGENT_MISMATCH");
        evidence.Add("logical-agent:match");
        if (!StringComparer.Ordinal.Equals(runtime.WorkerSlotId, expected.WorkerSlotId)) return Deny("WORKER_SLOT_MISMATCH");
        evidence.Add("worker-slot:match");
        if (string.IsNullOrWhiteSpace(runtime.TaskId)) return new(false, "TASK_BINDING_UNKNOWN", evidence);'

Replace-Exact 'src/PCCExecutive.Browser/ChatGptBrowserAdapter.cs' `
'$"expected-agent:{expected.LogicalAgentId}", $"expected-task:{expected.TaskId}"' `
'$"expected-agent:{expected.LogicalAgentId}", $"expected-worker-slot:{expected.WorkerSlotId ?? "MANAGER"}", $"expected-task:{expected.TaskId}"'

Replace-Exact 'src/PCCExecutive.Browser/DispatchAndResilience.cs' `
'    private readonly IBrowserRuntimeRegistry _runtimes; private readonly IChatGptBrowserAdapter _adapter; private readonly IDispatchLedger _ledger; private readonly WrongChatGuard _wrongChatGuard; private readonly GlobalBrowserSendGate _globalGate;
    public BrowserChatProvider(IBrowserRuntimeRegistry runtimes, IChatGptBrowserAdapter adapter, IDispatchLedger ledger, WrongChatGuard wrongChatGuard, GlobalBrowserSendGate globalGate) { _runtimes = runtimes; _adapter = adapter; _ledger = ledger; _wrongChatGuard = wrongChatGuard; _globalGate = globalGate; }' `
'    private readonly IBrowserRuntimeRegistry _runtimes; private readonly IChatGptBrowserAdapter _adapter; private readonly IDispatchLedger _ledger; private readonly WrongChatGuard _wrongChatGuard; private readonly GlobalBrowserSendGate _globalGate; private readonly IOwnershipProofService _ownership; private readonly ConcurrentDictionary<string, SemaphoreSlim> _dispatchGates = new(StringComparer.Ordinal);
    public BrowserChatProvider(IBrowserRuntimeRegistry runtimes, IChatGptBrowserAdapter adapter, IDispatchLedger ledger, WrongChatGuard wrongChatGuard, GlobalBrowserSendGate globalGate, IOwnershipProofService ownership) { _runtimes = runtimes; _adapter = adapter; _ledger = ledger; _wrongChatGuard = wrongChatGuard; _globalGate = globalGate; _ownership = ownership ?? throw new ArgumentNullException(nameof(ownership)); }'

Replace-Exact 'src/PCCExecutive.Browser/DispatchAndResilience.cs' `
'        var expected = new BrowserDispatchExpectation(request.ProjectRunId, request.LogicalAgentId, request.TaskId, request.ConversationIdentity, request.ProviderConversationIdentity);
        var snapshot = await _adapter.InspectAsync(runtime, expected, cancellationToken).ConfigureAwait(false);
        var guard = _wrongChatGuard.Evaluate(runtime, expected, snapshot);
        if (!guard.MaySend) return new(request.DispatchId, BrowserDispatchOutcome.NotSent, DispatchState.Prepared, guard.Reason, guard.Evidence);
        var contentHash = request.ContentHash ?? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Prompt)));
        var reservation = await _ledger.ReserveAsync(request.DispatchId, contentHash, cancellationToken).ConfigureAwait(false);' `
'        var expected = new BrowserDispatchExpectation(request.ProjectRunId, request.LogicalAgentId, request.TaskId, request.ConversationIdentity, request.ProviderConversationIdentity, request.WorkerSlotId);
        var snapshot = await _adapter.InspectAsync(runtime, expected, cancellationToken).ConfigureAwait(false);
        var guard = _wrongChatGuard.Evaluate(runtime, expected, snapshot);
        if (!guard.MaySend) return new(request.DispatchId, BrowserDispatchOutcome.NotSent, DispatchState.Prepared, guard.Reason, guard.Evidence);
        var proof = await _ownership.ProveAsync(runtime, cancellationToken).ConfigureAwait(false);
        if (!proof.IsProven) return new(request.DispatchId, BrowserDispatchOutcome.NotSent, DispatchState.Prepared, "PCC_OWNERSHIP_NOT_PROVEN", guard.Evidence.Append(proof.Reason).ToArray());
        var contentHash = request.ContentHash ?? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Prompt)));
        var dispatchGate = _dispatchGates.GetOrAdd(request.DispatchId, static _ => new SemaphoreSlim(1, 1));
        await dispatchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
        var reservation = await _ledger.ReserveAsync(request.DispatchId, contentHash, cancellationToken).ConfigureAwait(false);'

Replace-Exact 'src/PCCExecutive.Browser/DispatchAndResilience.cs' `
'        await _ledger.UpdateAsync(request.DispatchId, DispatchState.SafeRetry, string.Join(";", submission.Evidence), cancellationToken).ConfigureAwait(false);
        return new(request.DispatchId, BrowserDispatchOutcome.NotSent, DispatchState.SafeRetry, submission.Reason, submission.Evidence);
    }
}' `
'        await _ledger.UpdateAsync(request.DispatchId, DispatchState.SafeRetry, string.Join(";", submission.Evidence), cancellationToken).ConfigureAwait(false);
        return new(request.DispatchId, BrowserDispatchOutcome.NotSent, DispatchState.SafeRetry, submission.Reason, submission.Evidence);
        }
        finally
        {
            dispatchGate.Release();
            if (dispatchGate.CurrentCount == 1) _dispatchGates.TryRemove(request.DispatchId, out _);
        }
    }
}'

Replace-Exact 'src/PCCExecutive.Browser/DispatchAndResilience.cs' `
'            if (existing.State == DispatchState.SafeRetry) return Task.FromResult(new DispatchReservation(DispatchReservationStatus.RetryAllowed, existing, "SAFE_RETRY_EXPLICITLY_ALLOWED"));' `
'            if (existing.State is DispatchState.Prepared or DispatchState.SafeRetry) return Task.FromResult(new DispatchReservation(DispatchReservationStatus.RetryAllowed, existing, existing.State == DispatchState.Prepared ? "PREPARED_REPLAY_SAME_DISPATCH_ALLOWED" : "SAFE_RETRY_EXPLICITLY_ALLOWED"));'

Replace-Exact 'src/PCCExecutive.Infrastructure/DurableState.cs' `
'                if (existing.State == PCCExecutive.Browser.DispatchState.SafeRetry)
                    return new(DispatchReservationStatus.RetryAllowed, existing, "SAFE_RETRY_EXPLICITLY_ALLOWED");' `
'                if (existing.State is PCCExecutive.Browser.DispatchState.Prepared or PCCExecutive.Browser.DispatchState.SafeRetry)
                    return new(DispatchReservationStatus.RetryAllowed, existing, existing.State == PCCExecutive.Browser.DispatchState.Prepared ? "PREPARED_REPLAY_SAME_DISPATCH_ALLOWED" : "SAFE_RETRY_EXPLICITLY_ALLOWED");'

Write-Host 'Core contract and final-send safety patch applied.'
