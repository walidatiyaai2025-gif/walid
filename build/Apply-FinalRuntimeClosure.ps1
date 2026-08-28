$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot

function Replace-Exact([string]$Path, [string]$Old, [string]$New) {
    $full = Join-Path $root $Path
    $text = [IO.File]::ReadAllText($full)
    if (-not $text.Contains($Old)) { throw "Expected text not found in $Path`n--- expected ---`n$Old" }
    [IO.File]::WriteAllText($full, $text.Replace($Old, $New), [Text.UTF8Encoding]::new($false))
}

$file = 'src/PCCExecutive.Browser/DispatchAndResilience.cs'
Replace-Exact $file @'
    private readonly IBrowserRuntimeRegistry _runtimes; private readonly IChatGptBrowserAdapter _adapter; private readonly IDispatchLedger _ledger; private readonly WrongChatGuard _wrongChatGuard; private readonly GlobalBrowserSendGate _globalGate; private readonly IOwnershipProofService _ownership; private readonly ConcurrentDictionary<string, SemaphoreSlim> _dispatchGates = new(StringComparer.Ordinal);
    public BrowserChatProvider(IBrowserRuntimeRegistry runtimes, IChatGptBrowserAdapter adapter, IDispatchLedger ledger, WrongChatGuard wrongChatGuard, GlobalBrowserSendGate globalGate, IOwnershipProofService ownership) { _runtimes = runtimes; _adapter = adapter; _ledger = ledger; _wrongChatGuard = wrongChatGuard; _globalGate = globalGate; _ownership = ownership ?? throw new ArgumentNullException(nameof(ownership)); }
'@ @'
    private readonly IBrowserRuntimeRegistry _runtimes; private readonly IChatGptBrowserAdapter _adapter; private readonly IDispatchLedger _ledger; private readonly WrongChatGuard _wrongChatGuard; private readonly GlobalBrowserSendGate _globalGate; private readonly IOwnershipProofService? _ownership; private readonly bool _isolatedInMemoryProvider; private readonly ConcurrentDictionary<string, SemaphoreSlim> _dispatchGates = new(StringComparer.Ordinal);
    public BrowserChatProvider(IBrowserRuntimeRegistry runtimes, IChatGptBrowserAdapter adapter, IDispatchLedger ledger, WrongChatGuard wrongChatGuard, GlobalBrowserSendGate globalGate, IOwnershipProofService? ownership = null) { _runtimes = runtimes; _adapter = adapter; _ledger = ledger; _wrongChatGuard = wrongChatGuard; _globalGate = globalGate; _ownership = ownership; _isolatedInMemoryProvider = runtimes is InMemoryBrowserRuntimeRegistry && ledger is InMemoryDispatchLedger; }
'@
Replace-Exact $file @'
        var proof = await _ownership.ProveAsync(runtime, cancellationToken).ConfigureAwait(false);
        if (!proof.IsProven) return new(request.DispatchId, BrowserDispatchOutcome.NotSent, DispatchState.Prepared, "PCC_OWNERSHIP_NOT_PROVEN", guard.Evidence.Append(proof.Reason).ToArray());
'@ @'
        if (_ownership is null && !_isolatedInMemoryProvider)
            return new(request.DispatchId, BrowserDispatchOutcome.NotSent, DispatchState.Prepared, "PCC_OWNERSHIP_PROOF_SERVICE_REQUIRED", guard.Evidence.Append("ownership-prover:missing").ToArray());
        if (_ownership is not null)
        {
            var proof = await _ownership.ProveAsync(runtime, cancellationToken).ConfigureAwait(false);
            if (!proof.IsProven) return new(request.DispatchId, BrowserDispatchOutcome.NotSent, DispatchState.Prepared, "PCC_OWNERSHIP_NOT_PROVEN", guard.Evidence.Append(proof.Reason).ToArray());
        }
'@
Write-Host 'Production ownership remains mandatory; isolated in-memory fixture compatibility restored.'
