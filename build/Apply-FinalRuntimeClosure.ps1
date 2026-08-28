$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Require-Text([string]$Path, [string]$Needle) {
    $full = Join-Path $root $Path
    if (-not (Test-Path $full)) { throw "Required runtime closure file missing: ${Path}" }
    $text = [IO.File]::ReadAllText($full)
    if (-not $text.Contains($Needle)) { throw "Required runtime closure invariant missing from ${Path}: $Needle" }
    return $text
}

$browser = Require-Text 'src/PCCExecutive.Browser/DispatchAndResilience.cs' 'Func<CancellationToken, Task>? beforeSubmit = null'
$proofIndex = $browser.IndexOf('var proof = await _ownership.ProveAsync', [StringComparison]::Ordinal)
$callbackIndex = $browser.IndexOf('if (beforeSubmit is not null) await beforeSubmit', [StringComparison]::Ordinal)
if ($proofIndex -lt 0 -or $callbackIndex -lt 0 -or $proofIndex -gt $callbackIndex) {
    throw 'Ownership proof must precede the durable pre-submit callback.'
}

$adapter = Require-Text 'src/PCCExecutive.Infrastructure/BrowserAgentProviderAdapter.cs' 'beforeSubmit = ct => journal.SaveAsync(prepared, ct);'
$count = ([regex]::Matches($adapter, [regex]::Escape('Func<CancellationToken, Task>? beforeSubmit = null;'))).Count
if ($count -ne 1) { throw "Expected exactly one durable beforeSubmit callback declaration; found $count." }

[void](Require-Text 'src/PCCExecutive.Infrastructure/CrashConsistentOrchestrationStore.cs' 'snapshot = await DispatchMergedOrchestrationStateStore.MergeAsync(_store, snapshot, cancellationToken).ConfigureAwait(false);')
[void](Require-Text 'tests/PCCExecutive.Browser.Acceptance/AcceptanceHarness.cs' 'AcceptanceOwnershipProofService')
[void](Require-Text 'tests/PCCExecutive.Integration/PCCExecutive.Integration.csproj' '<IsTestProject>true</IsTestProject>')
[void](Require-Text 'tests/PCCExecutive.E2E/PCCExecutive.E2E.csproj' '<IsTestProject>true</IsTestProject>')

Write-Host 'Final runtime closure invariants are already applied; validation is idempotent.'