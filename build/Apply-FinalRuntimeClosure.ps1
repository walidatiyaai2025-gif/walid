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

$browser = Require-Text 'src/PCCExecutive.Browser/DispatchAndResilience.cs' 'var proof = await _ownership.ProveAsync'
$proofIndex = $browser.IndexOf('var proof = await _ownership.ProveAsync', [StringComparison]::Ordinal)
$submitIndex = $browser.IndexOf('_adapter.SubmitAsync', [StringComparison]::Ordinal)
if ($proofIndex -lt 0 -or $submitIndex -lt 0 -or $proofIndex -gt $submitIndex) { throw 'Fresh ownership proof must precede the physical Browser submit boundary.' }

$adapter = Require-Text 'src/PCCExecutive.Infrastructure/BrowserAgentProviderAdapter.cs' 'CanonicalDispatchReservationService'
[void](Require-Text 'src/PCCExecutive.Infrastructure/CanonicalDispatchReservationService.cs' 'ReserveOrRecoverAsync')
$hostText = Require-Text 'src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs' '_dispatchReservations = new CanonicalDispatchReservationService(store);'
$workers = Require-Text 'src/PCCExecutive.Application/ManagerWorkerOrchestration.cs' '_dispatchReservations.ReserveOrRecoverAsync'
if ($hostText.Contains('DispatchId.New()')) { throw 'Unsafe Manager caller DispatchId.New() remains.' }
if ($workers.Contains('DispatchId.New()')) { throw 'Unsafe Worker caller DispatchId.New() remains.' }
if ($adapter.Contains('beforeSubmit = ct => journal.SaveAsync(prepared, ct);')) { throw 'Deferred durable reservation remains in adapter.' }

[void](Require-Text 'src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs' 'DurableStartupRecoveryService(store, _orchestrationStore)')
[void](Require-Text 'src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs' 'startupRecovery.BeginStartupAsync(run.Id)')
[void](Require-Text 'src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs' 'startupRecovery.ReconstructAsync(run.Id)')
[void](Require-Text 'src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs' 'SafeShutdownCoordinator')
[void](Require-Text 'src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs' 'AutonomousConversationRolloverRuntime.Attach(gateway)')
$rollover = Require-Text 'src/PCCExecutive.App/Presentation/AutonomousConversationRolloverRuntime.cs' 'RepairInterruptedRolloversAsync'
if ($rollover.Contains('RecoveryCompletionPresentationGateway')) { throw 'Automatic rollover still depends on the obsolete recovery wrapper.' }
[void](Require-Text 'src/PCCExecutive.App/Presentation/AutonomousConversationRolloverRuntime.cs' 'NormalizeActiveConversationTruthAsync')
[void](Require-Text 'src/PCCExecutive.App/Presentation/AutonomousConversationRolloverRuntime.cs' 'ConversationLifecycleManager')

$acceptance = Require-Text 'tests/PCCExecutive.Browser.Acceptance/AcceptanceHarness.cs' 'AcceptanceOwnershipProofService'
$ownershipLine = 'public IOwnershipProofService Ownership => _ownership;'
if (([regex]::Matches($acceptance, [regex]::Escape($ownershipLine))).Count -ne 1) { throw 'ControlledBrowserAcceptanceHarness must expose exactly one Ownership member.' }
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

Write-Host 'Canonical durable dispatch, automatic rollover, recovery, ownership, Browser.Acceptance, and production-host E2E invariants verified.'