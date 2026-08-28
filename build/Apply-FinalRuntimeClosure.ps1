$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot

function Replace-Exact([string]$Path, [string]$Old, [string]$New) {
    $full = Join-Path $root $Path
    $text = [IO.File]::ReadAllText($full)
    if (-not $text.Contains($Old)) { throw "Expected text not found in $Path`n--- expected ---`n$Old" }
    [IO.File]::WriteAllText($full, $text.Replace($Old, $New), [Text.UTF8Encoding]::new($false))
}

$tests = 'tests/PCCExecutive.Browser.Tests/BrowserRuntimeTests.cs'
Replace-Exact $tests 'new BrowserChatProvider(registry,adapter,ledger,new WrongChatGuard(),new GlobalBrowserSendGate())' 'new BrowserChatProvider(registry,adapter,ledger,new WrongChatGuard(),new GlobalBrowserSendGate(),ProvenOwnership(runtime))'
Replace-Exact $tests 'new BrowserChatProvider(registry, adapter, new InMemoryDispatchLedger(), new WrongChatGuard(), new GlobalBrowserSendGate())' 'new BrowserChatProvider(registry, adapter, new InMemoryDispatchLedger(), new WrongChatGuard(), new GlobalBrowserSendGate(), ProvenOwnership(runtime))'
Replace-Exact $tests @'
    private static OwnershipMarker Marker(BrowserRuntimeRecord r)=>new(r.RuntimeId,r.ProcessId!.Value,r.ProcessStartIdentity!,r.ContextIdentity!,r.ProfilePath,r.CreatedByPcc,r.AdoptedExplicitly,r.OwnershipNonce);
'@ @'
    private static OwnershipMarker Marker(BrowserRuntimeRecord r)=>new(r.RuntimeId,r.ProcessId!.Value,r.ProcessStartIdentity!,r.ContextIdentity!,r.ProfilePath,r.CreatedByPcc,r.AdoptedExplicitly,r.OwnershipNonce);
    private static IOwnershipProofService ProvenOwnership(BrowserRuntimeRecord r){var markers=new FakeMarkers();markers.Set(Marker(r));var processes=new FakeProcesses();processes.Set(r.ProcessId!.Value,r.ProcessStartIdentity!,true);return new OwnershipProofService(Path.GetDirectoryName(r.ProfilePath)!,markers,processes);}
'@
Write-Host 'Explicit Browser ownership test fixtures applied.'
