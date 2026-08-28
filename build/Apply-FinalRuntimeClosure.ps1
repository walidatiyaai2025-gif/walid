$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot

function Replace-IfPresent([string]$Path, [string]$Old, [string]$New) {
    $full = Join-Path $root $Path
    $text = [IO.File]::ReadAllText($full)
    if ($text.Contains($Old)) {
        [IO.File]::WriteAllText($full, $text.Replace($Old, $New), [Text.UTF8Encoding]::new($false))
        return $true
    }
    return $false
}

$browser = 'src/PCCExecutive.Browser/DispatchAndResilience.cs'
[void](Replace-IfPresent $browser @'
    private readonly IBrowserRuntimeRegistry _runtimes; private readonly IChatGptBrowserAdapter _adapter; private readonly IDispatchLedger _ledger; private readonly WrongChatGuard _wrongChatGuard; private readonly GlobalBrowserSendGate _globalGate; private readonly IOwnershipProofService? _ownership; private readonly bool _isolatedInMemoryProvider; private readonly ConcurrentDictionary<string, SemaphoreSlim> _dispatchGates = new(StringComparer.Ordinal);
    public BrowserChatProvider(IBrowserRuntimeRegistry runtimes, IChatGptBrowserAdapter adapter, IDispatchLedger ledger, WrongChatGuard wrongChatGuard, GlobalBrowserSendGate globalGate, IOwnershipProofService? ownership = null) { _runtimes = runtimes; _adapter = adapter; _ledger = ledger; _wrongChatGuard = wrongChatGuard; _globalGate = globalGate; _ownership = ownership; _isolatedInMemoryProvider = runtimes is InMemoryBrowserRuntimeRegistry && ledger is InMemoryDispatchLedger; }
'@ @'
    private readonly IBrowserRuntimeRegistry _runtimes; private readonly IChatGptBrowserAdapter _adapter; private readonly IDispatchLedger _ledger; private readonly WrongChatGuard _wrongChatGuard; private readonly GlobalBrowserSendGate _globalGate; private readonly IOwnershipProofService _ownership; private readonly ConcurrentDictionary<string, SemaphoreSlim> _dispatchGates = new(StringComparer.Ordinal);
    public BrowserChatProvider(IBrowserRuntimeRegistry runtimes, IChatGptBrowserAdapter adapter, IDispatchLedger ledger, WrongChatGuard wrongChatGuard, GlobalBrowserSendGate globalGate, IOwnershipProofService ownership) { _runtimes = runtimes; _adapter = adapter; _ledger = ledger; _wrongChatGuard = wrongChatGuard; _globalGate = globalGate; _ownership = ownership ?? throw new ArgumentNullException(nameof(ownership)); }
'@)
[void](Replace-IfPresent $browser @'
        if (_ownership is null && !_isolatedInMemoryProvider)
            return new(request.DispatchId, BrowserDispatchOutcome.NotSent, DispatchState.Prepared, "PCC_OWNERSHIP_PROOF_SERVICE_REQUIRED", guard.Evidence.Append("ownership-prover:missing").ToArray());
        if (_ownership is not null)
        {
            var proof = await _ownership.ProveAsync(runtime, cancellationToken).ConfigureAwait(false);
            if (!proof.IsProven) return new(request.DispatchId, BrowserDispatchOutcome.NotSent, DispatchState.Prepared, "PCC_OWNERSHIP_NOT_PROVEN", guard.Evidence.Append(proof.Reason).ToArray());
        }
'@ @'
        var proof = await _ownership.ProveAsync(runtime, cancellationToken).ConfigureAwait(false);
        if (!proof.IsProven) return new(request.DispatchId, BrowserDispatchOutcome.NotSent, DispatchState.Prepared, "PCC_OWNERSHIP_NOT_PROVEN", guard.Evidence.Append(proof.Reason).ToArray());
'@)

$tests = 'tests/PCCExecutive.Browser.Tests/BrowserRuntimeTests.cs'
[void](Replace-IfPresent $tests 'new BrowserDispatchRequest("dispatch-1",runtime.ProjectRunId,runtime.LogicalAgentId,runtime.TaskId!,runtime.ConversationIdentity!,runtime.ProviderConversationIdentity!,"prompt")' 'new BrowserDispatchRequest("dispatch-1",runtime.ProjectRunId,runtime.LogicalAgentId,runtime.TaskId!,runtime.ConversationIdentity!,runtime.ProviderConversationIdentity!,"prompt",null,runtime.WorkerSlotId)')
[void](Replace-IfPresent $tests 'new BrowserDispatchRequest("new-dispatch", runtime.ProjectRunId, runtime.LogicalAgentId, runtime.TaskId!, runtime.ConversationIdentity!, "NEW", "prompt")' 'new BrowserDispatchRequest("new-dispatch", runtime.ProjectRunId, runtime.LogicalAgentId, runtime.TaskId!, runtime.ConversationIdentity!, "NEW", "prompt", null, runtime.WorkerSlotId)')
[void](Replace-IfPresent $tests 'private static BrowserDispatchExpectation Expectation(BrowserRuntimeRecord r)=>new(r.ProjectRunId,r.LogicalAgentId,r.TaskId!,r.ConversationIdentity!,r.ProviderConversationIdentity!);' 'private static BrowserDispatchExpectation Expectation(BrowserRuntimeRecord r)=>new(r.ProjectRunId,r.LogicalAgentId,r.TaskId!,r.ConversationIdentity!,r.ProviderConversationIdentity!,r.WorkerSlotId);')

Write-Host 'Production ownership is mandatory and Browser fixtures carry WorkerSlotId explicitly.'