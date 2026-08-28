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

# Reconcile PR #34's automatic rollover composition with the CURRENT canonical
# lifecycle/store API instead of introducing a parallel rollover implementation.
$rolloverPath = Join-Path $root 'src/PCCExecutive.App/Presentation/AutonomousConversationRolloverRuntime.cs'
$rolloverText = [IO.File]::ReadAllText($rolloverPath)
$rolloverText = $rolloverText.Replace('    private readonly ConversationLifecycleManager _lifecycle;' + [Environment]::NewLine, '')
$rolloverText = $rolloverText.Replace('        _lifecycle = new ConversationLifecycleManager(_store);' + [Environment]::NewLine, '')
$oldObserve = @'
    private async Task<ConversationGrowthObservation> ObserveAsync(BrowserRuntimeRecord runtime, ConversationRecord active, CancellationToken cancellationToken)
    {
        var messages = 0;
        long characters = 0;
        var waveCount = 0;
        var slowOrStuck = 0;
        var contextLimit = false;
        var longComposerFailure = false;

        var checkpoints = await _store.ListCheckpointsAsync(active.ProjectRunId, cancellationToken).ConfigureAwait(false);
        foreach (var checkpoint in checkpoints.Where(x => StringComparer.Ordinal.Equals(x.ProjectRunId, active.ProjectRunId)))
        {
            if (checkpoint.Kind.Contains("manager", StringComparison.OrdinalIgnoreCase) || checkpoint.Kind.Contains("worker", StringComparison.OrdinalIgnoreCase))
            {
                messages++;
                characters += checkpoint.Payload?.Length ?? 0;
            }
            if (checkpoint.Kind.Contains("wave", StringComparison.OrdinalIgnoreCase)) waveCount++;
        }

        var runtimeCheckpoints = await _store.ListCheckpointsAsync(active.ProjectRunId, cancellationToken).ConfigureAwait(false);
        foreach (var checkpoint in runtimeCheckpoints.Where(x => x.Payload?.Contains(runtime.RuntimeId, StringComparison.Ordinal) == true))
        {
            if (checkpoint.Payload!.Contains("SLOW", StringComparison.OrdinalIgnoreCase) || checkpoint.Payload.Contains("STUCK", StringComparison.OrdinalIgnoreCase)) slowOrStuck++;
            if (checkpoint.Payload.Contains("CONTEXT_LIMIT", StringComparison.OrdinalIgnoreCase)) contextLimit = true;
            if (checkpoint.Payload.Contains("LONG_CONVERSATION_COMPOSER", StringComparison.OrdinalIgnoreCase)) longComposerFailure = true;
        }

        return new ConversationGrowthObservation(messages, characters, waveCount, DateTimeOffset.UtcNow - active.CreatedAt, slowOrStuck, contextLimit, longComposerFailure);
    }
'@
$newObserve = @'
    private async Task<ConversationGrowthObservation> ObserveAsync(BrowserRuntimeRecord runtime, ConversationRecord active, CancellationToken cancellationToken)
    {
        var age = DateTimeOffset.UtcNow - active.CreatedAt;
        if (string.IsNullOrWhiteSpace(runtime.TaskId) || string.IsNullOrWhiteSpace(runtime.ConversationIdentity) || string.IsNullOrWhiteSpace(runtime.ProviderConversationIdentity))
            return new ConversationGrowthObservation(0, 0, 0, age, runtime.State is BrowserSessionState.Degraded or BrowserSessionState.Recovering ? 1 : 0, false, false);

        var expected = new BrowserDispatchExpectation(runtime.ProjectRunId, runtime.LogicalAgentId, runtime.TaskId!, runtime.ConversationIdentity!, runtime.ProviderConversationIdentity!, runtime.WorkerSlotId);
        var semantic = await PccHostConversationAccess.BrowserAdapter(_host).InspectAsync(runtime, expected, cancellationToken).ConfigureAwait(false);
        var evidence = semantic.Input.Evidence
            .Concat(semantic.Generation.Evidence)
            .Concat(semantic.Auth.Evidence)
            .Concat(semantic.Conversation.Evidence)
            .Concat(semantic.Health.Evidence)
            .ToArray();
        var contextLimit = evidence.Any(x => x.Contains("CONTEXT_LIMIT", StringComparison.OrdinalIgnoreCase) || x.Contains("context limit", StringComparison.OrdinalIgnoreCase));
        var longComposerFailure = evidence.Any(x => x.Contains("LONG_CONVERSATION_COMPOSER", StringComparison.OrdinalIgnoreCase) || x.Contains("conversation too long", StringComparison.OrdinalIgnoreCase));
        var slowOrStuck = semantic.Health.State is PageHealth.Slow or PageHealth.TempError || semantic.Generation.State == GenerationState.Unknown ? 1 : 0;
        var capturedCharacters = semantic.CapturedResponseText?.Length ?? 0;
        return new ConversationGrowthObservation(semantic.AssistantMessageCount, capturedCharacters, 0, age, slowOrStuck, contextLimit, longComposerFailure);
    }
'@
if ($rolloverText.Contains($oldObserve.TrimEnd())) {
    $rolloverText = $rolloverText.Replace($oldObserve.TrimEnd(), $newObserve.TrimEnd())
}
elseif (-not $rolloverText.Contains('var semantic = await PccHostConversationAccess.BrowserAdapter(_host).InspectAsync(runtime, expected, cancellationToken)')) {
    throw 'PR34 rollover observation anchor no longer matches current source.'
}
$rolloverText = $rolloverText.Replace('        await _lifecycle.CommitRolloverAsync(archived, successor, checkpointId, cancellationToken).ConfigureAwait(false);', '        await _store.CommitRolloverAsync(archived, successor, checkpointId, cancellationToken).ConfigureAwait(false);')
if ($rolloverText.Contains('_lifecycle')) { throw 'Obsolete ConversationLifecycleManager direct usage remains in PR34 runtime adapter.' }
if ($rolloverText.Contains('ListCheckpointsAsync')) { throw 'Obsolete checkpoint-list API remains in PR34 runtime adapter.' }
[IO.File]::WriteAllText($rolloverPath, $rolloverText, [Text.UTF8Encoding]::new($false))

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
[void](Require-Text 'src/PCCExecutive.App/Presentation/AutonomousConversationRolloverRuntime.cs' 'PreventiveRolloverPolicy')
[void](Require-Text 'src/PCCExecutive.App/Presentation/AutonomousConversationRolloverRuntime.cs' '_store.CommitRolloverAsync')

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

Write-Host 'Canonical durable dispatch, current-API automatic rollover, recovery, ownership, Browser.Acceptance, and production-host E2E invariants verified.'