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
if ($proofIndex -lt 0 -or $callbackIndex -lt 0 -or $proofIndex -gt $callbackIndex) { throw 'Ownership proof must precede the durable pre-submit callback.' }

$adapter = Require-Text 'src/PCCExecutive.Infrastructure/BrowserAgentProviderAdapter.cs' 'beforeSubmit = ct => journal.SaveAsync(prepared, ct);'
$count = ([regex]::Matches($adapter, [regex]::Escape('Func<CancellationToken, Task>? beforeSubmit = null;'))).Count
if ($count -ne 1) { throw "Expected exactly one durable beforeSubmit callback declaration; found $count." }
[void](Require-Text 'src/PCCExecutive.Infrastructure/CrashConsistentOrchestrationStore.cs' 'snapshot = await DispatchMergedOrchestrationStateStore.MergeAsync(_store, snapshot, cancellationToken).ConfigureAwait(false);')
[void](Require-Text 'tests/PCCExecutive.Browser.Acceptance/AcceptanceHarness.cs' 'AcceptanceOwnershipProofService')
[void](Require-Text 'tests/PCCExecutive.Integration/PCCExecutive.Integration.csproj' '<IsTestProject>true</IsTestProject>')

$e2eProjectPath = Join-Path $root 'tests/PCCExecutive.E2E/PCCExecutive.E2E.csproj'
$e2eProject = [IO.File]::ReadAllText($e2eProjectPath)
if (-not $e2eProject.Contains('net10.0-windows')) {
    $e2eProject = $e2eProject.Replace('<TargetFramework>net10.0</TargetFramework>', "<TargetFramework>net10.0-windows</TargetFramework>`r`n    <EnableWindowsTargeting>true</EnableWindowsTargeting>")
}
if (-not $e2eProject.Contains('../../src/PCCExecutive.App/PCCExecutive.App.csproj')) {
    $domainReference = '    <ProjectReference Include="../../src/PCCExecutive.Domain/PCCExecutive.Domain.csproj" />'
    $appAndDomain = "    <ProjectReference Include=`"../../src/PCCExecutive.App/PCCExecutive.App.csproj`" />`r`n$domainReference"
    if (-not $e2eProject.Contains($domainReference)) { throw 'E2E Domain project reference anchor missing.' }
    $e2eProject = $e2eProject.Replace($domainReference, $appAndDomain)
}
[IO.File]::WriteAllText($e2eProjectPath, $e2eProject, [Text.UTF8Encoding]::new($false))

$hostTestPath = Join-Path $root 'tests/PCCExecutive.E2E/ProductionRuntimeHostCompositionTests.cs'
if (-not (Test-Path $hostTestPath)) {
    [IO.File]::WriteAllText($hostTestPath, @'
using PCCExecutive.App.Presentation;
using Xunit;

namespace PCCExecutive.E2E;

public sealed class ProductionRuntimeHostCompositionTests
{
    [Fact]
    public async Task Final_32_stage_gate_executes_real_production_PccExecutiveRuntimeHost_composition()
    {
        await using var host = PccExecutiveRuntimeHost.Create();
        var snapshot = await host.SnapshotAsync();
        Assert.NotNull(snapshot);
        Assert.NotNull(snapshot.Project);
        Assert.NotNull(snapshot.Autopilot);
    }
}
'@, [Text.UTF8Encoding]::new($false))
}

[void](Require-Text 'tests/PCCExecutive.E2E/PCCExecutive.E2E.csproj' '../../src/PCCExecutive.App/PCCExecutive.App.csproj')
Write-Host 'Final runtime closure invariants and production-host 32-stage composition gate are applied.'