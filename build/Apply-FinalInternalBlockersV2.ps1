$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Read-Utf8([string]$Path) { return [IO.File]::ReadAllText((Join-Path $root $Path)) }
function Write-Utf8([string]$Path, [string]$Text) {
    $full = Join-Path $root $Path
    $dir = Split-Path -Parent $full
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    [IO.File]::WriteAllText($full, $Text, [Text.UTF8Encoding]::new($false))
}
function Replace-Exact([string]$Path, [string]$Old, [string]$New) {
    $text = Read-Utf8 $Path
    if ($text.Contains($New)) { return }
    if (-not $text.Contains($Old)) { throw "Expected anchor not found in $Path" }
    Write-Utf8 $Path ($text.Replace($Old, $New))
}

# 1) Current Browser.Acceptance compile blocker.
$acceptancePath = 'tests/PCCExecutive.Browser.Acceptance/AcceptanceHarness.cs'
$acceptance = Read-Utf8 $acceptancePath
$ownershipLine = '    public IOwnershipProofService Ownership => _ownership;'
while (([regex]::Matches($acceptance, [regex]::Escape($ownershipLine))).Count -gt 1) {
    $first = $acceptance.IndexOf($ownershipLine, [StringComparison]::Ordinal)
    $second = $acceptance.IndexOf($ownershipLine, $first + $ownershipLine.Length, [StringComparison]::Ordinal)
    $acceptance = $acceptance.Remove($second, $ownershipLine.Length)
    if ($second -lt $acceptance.Length -and $acceptance[$second] -eq "`r") { $acceptance = $acceptance.Remove($second, 1) }
    if ($second -lt $acceptance.Length -and $acceptance[$second] -eq "`n") { $acceptance = $acceptance.Remove($second, 1) }
}
Write-Utf8 $acceptancePath $acceptance
if (([regex]::Matches((Read-Utf8 $acceptancePath), [regex]::Escape($ownershipLine))).Count -ne 1) { throw 'Acceptance Ownership member must exist exactly once.' }

# 2) Application correlation contract + deterministic identity.
$appPath = 'src/PCCExecutive.Application/Foundation.cs'
$oldAgent = 'public sealed record AgentRequest(ProjectRunId ProjectRunId, LogicalAgentId LogicalAgentId, ConversationId ConversationId, DispatchId DispatchId, string Content, string ContentHash, WorkerSlotId? WorkerSlotId = null, TaskId? TaskId = null, WaveId? WaveId = null);'
$newAgent = @'
public sealed record AgentRequest(ProjectRunId ProjectRunId, LogicalAgentId LogicalAgentId, ConversationId ConversationId, DispatchId DispatchId, string Content, string ContentHash, WorkerSlotId? WorkerSlotId = null, TaskId? TaskId = null, WaveId? WaveId = null, string? ProviderConversationId = null);
public sealed record DurableDispatchCorrelation(ProjectRunId ProjectRunId, LogicalAgentId LogicalAgentId, WorkerSlotId? WorkerSlotId, TaskId TaskId, WaveId WaveId, ConversationId LogicalConversationId, string ProviderConversationId, string ContentHash);
public interface ICanonicalDispatchReservationService
{
    Task<Dispatch> ReserveOrRecoverAsync(DurableDispatchCorrelation correlation, CancellationToken cancellationToken = default);
}
public static class CanonicalDispatchIdentity
{
    public static DispatchId Create(DurableDispatchCorrelation correlation) => new(StableGuid(string.Join("|",
        "dispatch-v2",
        correlation.ProjectRunId,
        correlation.LogicalAgentId,
        correlation.WorkerSlotId?.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "MANAGER",
        correlation.TaskId,
        correlation.WaveId,
        correlation.LogicalConversationId,
        Normalize(correlation.ProviderConversationId),
        Normalize(correlation.ContentHash))));

    public static TaskId StableTask(ProjectRunId runId, string runtimeTaskId) => new(StableGuid($"runtime-task:{runId}:{Normalize(runtimeTaskId)}"));
    public static WaveId StableWave(ProjectRunId runId, string runtimeTaskId) => new(StableGuid($"runtime-wave:{runId}:{Normalize(runtimeTaskId)}"));

    private static string Normalize(string value) => string.IsNullOrWhiteSpace(value) ? "UNKNOWN" : value.Trim();
    private static Guid StableGuid(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        var guid = bytes[..16];
        guid[6] = (byte)((guid[6] & 0x0f) | 0x50);
        guid[8] = (byte)((guid[8] & 0x3f) | 0x80);
        return new Guid(guid);
    }
}
'@
Replace-Exact $appPath $oldAgent $newAgent.TrimEnd()

# Persist WorkerSlot + provider identity on the canonical Domain Dispatch.
$domainPath = 'src/PCCExecutive.Domain/Foundation.cs'
$oldDispatch = 'public sealed record Dispatch(DispatchId Id, ProjectRunId ProjectRunId, WaveId WaveId, TaskId TaskId, LogicalAgentId LogicalAgentId, ConversationId ConversationId, string ContentHash, DateTimeOffset PreparedAt, DispatchState State, DateTimeOffset? SubmittedAt, DateTimeOffset? AcknowledgedAt, DateTimeOffset? CompletedAt, DispatchId? RetryOfDispatchId, string? ReconciliationEvidence);'
$newDispatch = 'public sealed record Dispatch(DispatchId Id, ProjectRunId ProjectRunId, WaveId WaveId, TaskId TaskId, LogicalAgentId LogicalAgentId, ConversationId ConversationId, string ContentHash, DateTimeOffset PreparedAt, DispatchState State, DateTimeOffset? SubmittedAt, DateTimeOffset? AcknowledgedAt, DateTimeOffset? CompletedAt, DispatchId? RetryOfDispatchId, string? ReconciliationEvidence, WorkerSlotId? WorkerSlotId = null, string? ProviderConversationId = null);'
Replace-Exact $domainPath $oldDispatch $newDispatch

# Full-correlation matching in the existing journal.
$journalPath = 'src/PCCExecutive.Infrastructure/AutonomousDispatchSafety.cs'
$oldFind = @'
    public async Task<PCCExecutive.Domain.Dispatch?> FindEquivalentAsync(
        ProjectRunId projectRunId,
        LogicalAgentId logicalAgentId,
        TaskId taskId,
        ConversationId conversationId,
        string contentHash,
        CancellationToken cancellationToken = default)
    {
        var dispatches = await ListAsync(projectRunId, cancellationToken).ConfigureAwait(false);
        return dispatches
            .Where(x => x.LogicalAgentId == logicalAgentId && x.TaskId == taskId && x.ConversationId == conversationId && StringComparer.OrdinalIgnoreCase.Equals(x.ContentHash, contentHash))
            .OrderByDescending(x => x.PreparedAt)
            .FirstOrDefault();
    }
'@
$newFind = @'
    public async Task<PCCExecutive.Domain.Dispatch?> FindEquivalentAsync(
        ProjectRunId projectRunId,
        LogicalAgentId logicalAgentId,
        WorkerSlotId? workerSlotId,
        TaskId taskId,
        WaveId waveId,
        ConversationId conversationId,
        string providerConversationId,
        string contentHash,
        CancellationToken cancellationToken = default)
    {
        var dispatches = await ListAsync(projectRunId, cancellationToken).ConfigureAwait(false);
        return dispatches
            .Where(x => x.LogicalAgentId == logicalAgentId &&
                        x.TaskId == taskId &&
                        x.WaveId == waveId &&
                        x.ConversationId == conversationId &&
                        (x.WorkerSlotId == workerSlotId || x.WorkerSlotId is null) &&
                        (string.IsNullOrWhiteSpace(x.ProviderConversationId) ||
                         StringComparer.Ordinal.Equals(x.ProviderConversationId, providerConversationId) ||
                         StringComparer.OrdinalIgnoreCase.Equals(x.ProviderConversationId, "NEW")) &&
                        StringComparer.OrdinalIgnoreCase.Equals(x.ContentHash, contentHash))
            .OrderByDescending(x => x.PreparedAt)
            .FirstOrDefault();
    }
'@
Replace-Exact $journalPath $oldFind.TrimEnd() $newFind.TrimEnd()

# Canonical durable reservation service.
$reservationService = @'
using PCCExecutive.Application;
using PCCExecutive.Domain;

namespace PCCExecutive.Infrastructure;

public sealed class CanonicalDispatchReservationService : ICanonicalDispatchReservationService
{
    private readonly AutonomousDispatchJournal _journal;

    public CanonicalDispatchReservationService(SqliteStateStore store) => _journal = new AutonomousDispatchJournal(store);

    public async Task<Dispatch> ReserveOrRecoverAsync(DurableDispatchCorrelation correlation, CancellationToken cancellationToken = default)
    {
        var existing = await _journal.FindEquivalentAsync(
            correlation.ProjectRunId,
            correlation.LogicalAgentId,
            correlation.WorkerSlotId,
            correlation.TaskId,
            correlation.WaveId,
            correlation.LogicalConversationId,
            correlation.ProviderConversationId,
            correlation.ContentHash,
            cancellationToken).ConfigureAwait(false);

        if (existing is not null)
            return (await _journal.ReconcileAsync(existing, cancellationToken).ConfigureAwait(false)).Dispatch;

        var dispatch = new Dispatch(
            CanonicalDispatchIdentity.Create(correlation),
            correlation.ProjectRunId,
            correlation.WaveId,
            correlation.TaskId,
            correlation.LogicalAgentId,
            correlation.LogicalConversationId,
            correlation.ContentHash,
            DateTimeOffset.UtcNow,
            DispatchState.PREPARED,
            null, null, null, null,
            "canonical-durable-reservation",
            correlation.WorkerSlotId,
            correlation.ProviderConversationId);
        await _journal.SaveAsync(dispatch, cancellationToken).ConfigureAwait(false);
        return dispatch;
    }
}
'@
Write-Utf8 'src/PCCExecutive.Infrastructure/CanonicalDispatchReservationService.cs' $reservationService.TrimStart()

# Adapter consumes canonical reserve/recover after its own fresh ownership proof.
$adapterPath = 'src/PCCExecutive.Infrastructure/BrowserAgentProviderAdapter.cs'
$adapter = Read-Utf8 $adapterPath
$start = $adapter.IndexOf('        var effectiveDispatchId = request.DispatchId;', [StringComparison]::Ordinal)
$end = $adapter.IndexOf('        var browserRequest = new BrowserDispatchRequest(', $start, [StringComparison]::Ordinal)
if ($start -lt 0 -or $end -lt 0) { throw 'Adapter durable block anchors missing.' }
$newAdapterBlock = @'
        var effectiveDispatchId = request.DispatchId;
        PCCExecutive.Domain.Dispatch? domainDispatch = null;
        AutonomousDispatchJournal? journal = null;
        if (_durableStore is not null)
        {
            var taskId = request.TaskId ?? CanonicalDispatchIdentity.StableTask(request.ProjectRunId, runtime.TaskId!);
            var waveId = request.WaveId ?? CanonicalDispatchIdentity.StableWave(request.ProjectRunId, runtime.TaskId!);
            var providerConversationId = request.ProviderConversationId ?? runtime.ProviderConversationIdentity!;
            if (!StringComparer.Ordinal.Equals(runtime.ProviderConversationIdentity, providerConversationId) &&
                !StringComparer.OrdinalIgnoreCase.Equals(providerConversationId, "NEW"))
                return NotSent(request.DispatchId, $"runtime:{runtime.RuntimeId};provider-conversation:mismatch", "WRONG_PROVIDER_CONVERSATION_BINDING");

            var correlation = new DurableDispatchCorrelation(request.ProjectRunId, request.LogicalAgentId, request.WorkerSlotId, taskId, waveId, request.ConversationId, providerConversationId, request.ContentHash);
            domainDispatch = await new CanonicalDispatchReservationService(_durableStore).ReserveOrRecoverAsync(correlation, cancellationToken).ConfigureAwait(false);
            effectiveDispatchId = domainDispatch.Id;
            journal = new AutonomousDispatchJournal(_durableStore);
            var reconciled = await journal.ReconcileAsync(domainDispatch, cancellationToken).ConfigureAwait(false);
            domainDispatch = reconciled.Dispatch;
            if (reconciled.IsUncertain)
                return new(effectiveDispatchId, false, false, false, true, null, reconciled.Evidence, "SUBMITTED_UNKNOWN");
            if (reconciled.AlreadyAccepted)
                return new(effectiveDispatchId, true, domainDispatch.State == PCCExecutive.Domain.DispatchState.GENERATING, domainDispatch.State == PCCExecutive.Domain.DispatchState.COMPLETED, false, null, reconciled.Evidence, null);
            if (!reconciled.SafeToSubmit)
                return NotSent(effectiveDispatchId, reconciled.Evidence, $"DURABLE_DISPATCH_{domainDispatch.State}");
        }

'@
$adapter = $adapter.Substring(0, $start) + $newAdapterBlock + $adapter.Substring($end)
$adapter = $adapter.Replace('        var result = await _provider.SendAsync(runtime.RuntimeId, browserRequest, cancellationToken, beforeSubmit).ConfigureAwait(false);', '        var result = await _provider.SendAsync(runtime.RuntimeId, browserRequest, cancellationToken).ConfigureAwait(false);')
if ($adapter.Contains('beforeSubmit = ct => journal.SaveAsync(prepared, ct);')) { throw 'Deferred durable reservation still exists in adapter.' }
Write-Utf8 $adapterPath $adapter

# Worker orchestrator: reserve before provider send, and never manufacture a random DispatchId.
$orchestratorPath = 'src/PCCExecutive.Application/ManagerWorkerOrchestration.cs'
Replace-Exact $orchestratorPath 'public sealed record WorkerExecutionBinding(WorkerSlotId SlotId, LogicalAgentId LogicalAgentId, ConversationId ConversationId);' 'public sealed record WorkerExecutionBinding(WorkerSlotId SlotId, LogicalAgentId LogicalAgentId, ConversationId ConversationId, string? ProviderConversationId = null);'
$orch = Read-Utf8 $orchestratorPath
if (-not $orch.Contains('private readonly ICanonicalDispatchReservationService? _dispatchReservations;')) {
    $orch = $orch.Replace('    private readonly IWorkerHandoffValidator _handoffValidator;' + [Environment]::NewLine + '    private readonly TimeSpan _baseDispatchInterval;', '    private readonly IWorkerHandoffValidator _handoffValidator;' + [Environment]::NewLine + '    private readonly ICanonicalDispatchReservationService? _dispatchReservations;' + [Environment]::NewLine + '    private readonly TimeSpan _baseDispatchInterval;')
    $orch = $orch.Replace('        IWorkerHandoffValidator? handoffValidator = null,' + [Environment]::NewLine + '        TimeSpan? baseDispatchInterval = null)', '        IWorkerHandoffValidator? handoffValidator = null,' + [Environment]::NewLine + '        TimeSpan? baseDispatchInterval = null,' + [Environment]::NewLine + '        ICanonicalDispatchReservationService? dispatchReservations = null)')
    $orch = $orch.Replace('        _handoffValidator = handoffValidator ?? new WorkerHandoffValidator();' + [Environment]::NewLine + '        _baseDispatchInterval = baseDispatchInterval ?? TimeSpan.FromSeconds(10);', '        _handoffValidator = handoffValidator ?? new WorkerHandoffValidator();' + [Environment]::NewLine + '        _dispatchReservations = dispatchReservations;' + [Environment]::NewLine + '        _baseDispatchInterval = baseDispatchInterval ?? TimeSpan.FromSeconds(10);')
}
$oldWorkerLine = '            var request = new AgentRequest(projectRunId, binding.LogicalAgentId, binding.ConversationId, DispatchId.New(), content, hash, binding.SlotId, task.Id, plan.WaveId);'
$newWorkerLines = @'
            var providerConversationId = binding.ProviderConversationId ?? binding.ConversationId.ToString();
            var correlation = new DurableDispatchCorrelation(projectRunId, binding.LogicalAgentId, binding.SlotId, task.Id, plan.WaveId, binding.ConversationId, providerConversationId, hash);
            var dispatchId = CanonicalDispatchIdentity.Create(correlation);
            if (_dispatchReservations is not null)
                dispatchId = (await _dispatchReservations.ReserveOrRecoverAsync(correlation, cancellationToken).ConfigureAwait(false)).Id;
            var request = new AgentRequest(projectRunId, binding.LogicalAgentId, binding.ConversationId, dispatchId, content, hash, binding.SlotId, task.Id, plan.WaveId, providerConversationId);
'@
if ($orch.Contains($oldWorkerLine)) { $orch = $orch.Replace($oldWorkerLine, $newWorkerLines.TrimEnd()) }
if ($orch.Contains('DispatchId.New()')) { throw 'Worker orchestrator still contains DispatchId.New().' }
if (-not $orch.Contains('_dispatchReservations = dispatchReservations;')) { throw 'Worker reservation service injection failed.' }
Write-Utf8 $orchestratorPath $orch

# Runtime host: one canonical reservation service for Manager + Manager-review + Workers.
$hostPath = 'src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs'
$host = Read-Utf8 $hostPath
$nl = [Environment]::NewLine
if (-not $host.Contains('private readonly ICanonicalDispatchReservationService _dispatchReservations;')) {
    $host = $host.Replace('    private readonly CrashConsistentOrchestrationStore _orchestrationStore;', '    private readonly CrashConsistentOrchestrationStore _orchestrationStore;' + $nl + '    private readonly ICanonicalDispatchReservationService _dispatchReservations;' + $nl + '    private AutonomousConversationRolloverRuntime? _rolloverRuntime;')
    $host = $host.Replace('        _orchestrationStore = new CrashConsistentOrchestrationStore(store);', '        _orchestrationStore = new CrashConsistentOrchestrationStore(store);' + $nl + '        _dispatchReservations = new CanonicalDispatchReservationService(store);')
}
$oldManagerLine = '        var request = new AgentRequest(run.Id, managerAgentId, new ConversationId(Guid.Parse(logicalConversation)), DispatchId.New(), prompt, hash);'
$newManagerLines = @'
        runtime = await _runtimeRegistry.GetAsync(runtime.RuntimeId, cancellationToken).ConfigureAwait(false) ?? runtime;
        var managerConversation = new ConversationId(Guid.Parse(logicalConversation));
        var managerTaskKey = runtime.TaskId ?? $"manager-plan:{run.Id}";
        var managerTaskId = CanonicalDispatchIdentity.StableTask(run.Id, managerTaskKey);
        var managerWaveId = CanonicalDispatchIdentity.StableWave(run.Id, managerTaskKey);
        var managerProviderConversation = runtime.ProviderConversationIdentity ?? "NEW";
        var managerCorrelation = new DurableDispatchCorrelation(run.Id, managerAgentId, null, managerTaskId, managerWaveId, managerConversation, managerProviderConversation, hash);
        var managerDispatch = await _dispatchReservations.ReserveOrRecoverAsync(managerCorrelation, cancellationToken).ConfigureAwait(false);
        var request = new AgentRequest(run.Id, managerAgentId, managerConversation, managerDispatch.Id, prompt, hash, null, null, null, managerProviderConversation);
'@
if ($host.Contains($oldManagerLine)) { $host = $host.Replace($oldManagerLine, $newManagerLines.TrimEnd()) }
$oldReviewLine = '        var request = new AgentRequest(run.Id, managerAgentId, new ConversationId(Guid.Parse(logicalConversation)), DispatchId.New(), prompt, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(prompt))).ToLowerInvariant());'
$newReviewLines = @'
        var reviewHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(prompt))).ToLowerInvariant();
        runtime = await _runtimeRegistry.GetAsync(runtime.RuntimeId, cancellationToken).ConfigureAwait(false) ?? runtime;
        var reviewConversation = new ConversationId(Guid.Parse(logicalConversation));
        var reviewTaskKey = runtime.TaskId ?? $"manager-review:{review.WaveId}";
        var reviewTaskId = CanonicalDispatchIdentity.StableTask(run.Id, reviewTaskKey);
        var reviewWaveId = CanonicalDispatchIdentity.StableWave(run.Id, reviewTaskKey);
        var reviewProviderConversation = runtime.ProviderConversationIdentity ?? providerConversation;
        var reviewCorrelation = new DurableDispatchCorrelation(run.Id, managerAgentId, null, reviewTaskId, reviewWaveId, reviewConversation, reviewProviderConversation, reviewHash);
        var reviewDispatch = await _dispatchReservations.ReserveOrRecoverAsync(reviewCorrelation, cancellationToken).ConfigureAwait(false);
        var request = new AgentRequest(run.Id, managerAgentId, reviewConversation, reviewDispatch.Id, prompt, reviewHash, null, null, null, reviewProviderConversation);
'@
if ($host.Contains($oldReviewLine)) { $host = $host.Replace($oldReviewLine, $newReviewLines.TrimEnd()) }
$host = $host.Replace('            bindings.Add(new WorkerExecutionBinding(slot, agentId, conversationId));', '            bindings.Add(new WorkerExecutionBinding(slot, agentId, conversationId, runtime.ProviderConversationIdentity ?? "NEW"));')
$host = $host.Replace('        var result = await new ManagerWorkerOrchestrator(_agentProvider, baseDispatchInterval: TimeSpan.FromSeconds(_settings.BaseDispatchIntervalSeconds))', '        var result = await new ManagerWorkerOrchestrator(_agentProvider, baseDispatchInterval: TimeSpan.FromSeconds(_settings.BaseDispatchIntervalSeconds), dispatchReservations: _dispatchReservations)')
if ($host.Contains('DispatchId.New()')) { throw 'Runtime host still contains DispatchId.New().' }
if (-not $host.Contains('_dispatchReservations = new CanonicalDispatchReservationService(store);')) { throw 'Host reservation service injection failed.' }
Write-Utf8 $hostPath $host

# 3) Consume PR #34's actual crash-safe rollover implementation, adapting only its
# wrapper dependency so it attaches directly to the current canonical host.
& git fetch origin worker/pcc-final-recovery-completion --quiet
if ($LASTEXITCODE -ne 0) { throw 'Unable to fetch recovery-completion branch.' }
$rollover = (& git show 'origin/worker/pcc-final-recovery-completion:src/PCCExecutive.App/Presentation/AutonomousConversationRolloverRuntime.cs' | Out-String)
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($rollover)) { throw 'Unable to read PR34 rollover source.' }
$rollover = $rollover.Replace('    private readonly RecoveryCompletionPresentationGateway _gateway;' + $nl, '')
$rollover = $rollover.Replace('    private AutonomousConversationRolloverRuntime(RecoveryCompletionPresentationGateway gateway)', '    private AutonomousConversationRolloverRuntime(PccExecutiveRuntimeHost host)')
$rollover = $rollover.Replace('        _gateway = gateway;' + $nl + '        _host = RecoveryGatewayRolloverAccess.Inner(gateway);', '        _host = host;')
$rollover = $rollover.Replace('    public static AutonomousConversationRolloverRuntime Attach(RecoveryCompletionPresentationGateway gateway) => new(gateway);', '    public static AutonomousConversationRolloverRuntime Attach(PccExecutiveRuntimeHost host) => new(host);')
$rollover = [regex]::Replace($rollover, '(?s)\r?\ninternal static class RecoveryGatewayRolloverAccess\s*\{.*?\r?\n\}\r?\n\r?\ninternal static class PccHostConversationAccess', $nl + 'internal static class PccHostConversationAccess')
if ($rollover.Contains('RecoveryCompletionPresentationGateway') -or $rollover.Contains('RecoveryGatewayRolloverAccess')) { throw 'Rollover adaptation still references obsolete wrapper.' }
$minimalRecoveryAccess = @'

internal static class PccHostRecoveryAccess
{
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_store")]
    internal static extern ref SqliteStateStore Store(PccExecutiveRuntimeHost host);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_run")]
    internal static extern ref ProjectRun? Run(PccExecutiveRuntimeHost host);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_runtimeHealthFault")]
    internal static extern ref string? RuntimeHealthFault(PccExecutiveRuntimeHost host);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_sendGate")]
    internal static extern ref GlobalBrowserSendGate SendGate(PccExecutiveRuntimeHost host);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_newSendPause")]
    internal static extern ref PCCExecutive.Application.INewSendPausePort NewSendPause(PccExecutiveRuntimeHost host);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_autopilot")]
    internal static extern ref string Autopilot(PccExecutiveRuntimeHost host);
}
'@
$accessIndex = $rollover.IndexOf('internal static class PccHostConversationAccess', [StringComparison]::Ordinal)
if ($accessIndex -lt 0) { throw 'Rollover host access anchor missing.' }
$rollover = $rollover.Insert($accessIndex, $minimalRecoveryAccess + $nl)
Write-Utf8 'src/PCCExecutive.App/Presentation/AutonomousConversationRolloverRuntime.cs' $rollover

# Attach rollover after startup recovery/Browser reconciliation, before AutoResume.
$host = Read-Utf8 $hostPath
$startupOld = '            if (run is not null) gateway.RecoverStartupBrowserStateAsync().GetAwaiter().GetResult();' + $nl + '            gateway.RefreshLocalSnapshotAsync().GetAwaiter().GetResult();'
$startupNew = '            if (run is not null) gateway.RecoverStartupBrowserStateAsync().GetAwaiter().GetResult();' + $nl + '            gateway._rolloverRuntime = AutonomousConversationRolloverRuntime.Attach(gateway);' + $nl + '            gateway.RefreshLocalSnapshotAsync().GetAwaiter().GetResult();'
if (-not $host.Contains('gateway._rolloverRuntime = AutonomousConversationRolloverRuntime.Attach(gateway);')) {
    if (-not $host.Contains($startupOld)) { throw 'Rollover startup attachment anchor missing.' }
    $host = $host.Replace($startupOld, $startupNew)
}
$disposeOld = '        _autopilotCancellation.Cancel();' + $nl + '        if (_autopilotTask is not null)'
$disposeNew = '        _autopilotCancellation.Cancel();' + $nl + '        if (_rolloverRuntime is not null)' + $nl + '            await _rolloverRuntime.DisposeAsync().ConfigureAwait(false);' + $nl + '        if (_autopilotTask is not null)'
if (-not $host.Contains('await _rolloverRuntime.DisposeAsync().ConfigureAwait(false);')) {
    if (-not $host.Contains($disposeOld)) { throw 'Rollover disposal anchor missing.' }
    $host = $host.Replace($disposeOld, $disposeNew)
}
Write-Utf8 $hostPath $host

# Focused deterministic tests.
$identityTests = @'
using PCCExecutive.Application;
using PCCExecutive.Domain;
using Xunit;

namespace PCCExecutive.Application.Tests;

public sealed class CanonicalDispatchIdentityTests
{
    [Fact]
    public void Manager_initial_crash_after_enter_reuses_same_dispatch_id()
    {
        var c = Manager("manager-initial");
        Assert.Equal(CanonicalDispatchIdentity.Create(c), CanonicalDispatchIdentity.Create(c));
    }

    [Fact]
    public void Manager_review_crash_after_enter_reuses_same_dispatch_id()
    {
        var c = Manager("manager-review");
        Assert.Equal(CanonicalDispatchIdentity.Create(c), CanonicalDispatchIdentity.Create(c));
    }

    [Fact]
    public void Worker_crash_after_enter_reuses_same_dispatch_id_and_full_correlation_matters()
    {
        var run = ProjectRunId.New(); var agent = LogicalAgentId.New(); var task = TaskId.New(); var wave = WaveId.New(); var conversation = ConversationId.New();
        var c = new DurableDispatchCorrelation(run, agent, new WorkerSlotId(1), task, wave, conversation, "provider-1", "hash");
        Assert.Equal(CanonicalDispatchIdentity.Create(c), CanonicalDispatchIdentity.Create(c with { }));
        Assert.NotEqual(CanonicalDispatchIdentity.Create(c), CanonicalDispatchIdentity.Create(c with { WorkerSlotId = new WorkerSlotId(2) }));
        Assert.NotEqual(CanonicalDispatchIdentity.Create(c), CanonicalDispatchIdentity.Create(c with { ProviderConversationId = "provider-2" }));
    }

    private static DurableDispatchCorrelation Manager(string key)
    {
        var run = ProjectRunId.New(); var agent = LogicalAgentId.New(); var conversation = ConversationId.New();
        return new(run, agent, null, CanonicalDispatchIdentity.StableTask(run, key), CanonicalDispatchIdentity.StableWave(run, key), conversation, "provider-manager", "hash");
    }
}
'@
Write-Utf8 'tests/PCCExecutive.Application.Tests/CanonicalDispatchIdentityTests.cs' $identityTests.TrimStart()

$reservationTests = @'
using PCCExecutive.Application;
using PCCExecutive.Browser;
using PCCExecutive.Domain;
using PCCExecutive.Infrastructure;
using Xunit;

namespace PCCExecutive.Infrastructure.Tests;

public sealed class CanonicalDispatchReservationServiceTests
{
    [Fact]
    public async Task Restart_submitted_unknown_recovers_same_dispatch_id_without_replacement()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pcc-dispatch-{Guid.NewGuid():N}.db");
        try
        {
            var run = ProjectRunId.New(); var agent = LogicalAgentId.New(); var task = TaskId.New(); var wave = WaveId.New(); var conversation = ConversationId.New();
            var correlation = new DurableDispatchCorrelation(run, agent, new WorkerSlotId(1), task, wave, conversation, "NEW", "hash");
            DispatchId id;
            await using (var store = new SqliteStateStore(path))
            {
                await store.InitializeAsync();
                var first = await new CanonicalDispatchReservationService(store).ReserveOrRecoverAsync(correlation);
                id = first.Id;
                await store.ReserveAsync(id.ToString(), first.ContentHash);
                await store.UpdateAsync(id.ToString(), PCCExecutive.Browser.DispatchState.Submitting, "crash-after-enter");
                await store.SaveDispatchAsync(first with { State = PCCExecutive.Domain.DispatchState.SUBMITTED_UNKNOWN });
            }
            await using (var reopened = new SqliteStateStore(path))
            {
                await reopened.InitializeAsync();
                var recovered = await new CanonicalDispatchReservationService(reopened).ReserveOrRecoverAsync(correlation with { ProviderConversationId = "provider-established" });
                Assert.Equal(id, recovered.Id);
                Assert.Equal(PCCExecutive.Domain.DispatchState.SUBMITTED_UNKNOWN, recovered.State);
                Assert.Single((await new AutonomousDispatchJournal(reopened).ListAsync(run)).Where(x => x.ContentHash == "hash"));
            }
        }
        finally { try { File.Delete(path); } catch { } }
    }
}
'@
Write-Utf8 'tests/PCCExecutive.Infrastructure.Tests/CanonicalDispatchReservationServiceTests.cs' $reservationTests.TrimStart()

$sourceSafety = @'
using Xunit;

namespace PCCExecutive.E2E;

public sealed class FinalRuntimeSourceSafetyTests
{
    [Fact]
    public void Production_callers_use_canonical_dispatch_and_automatic_rollover()
    {
        var root = FindRepositoryRoot();
        var host = File.ReadAllText(Path.Combine(root, "src", "PCCExecutive.App", "Presentation", "IntegratedPresentationGateway.cs"));
        var workers = File.ReadAllText(Path.Combine(root, "src", "PCCExecutive.Application", "ManagerWorkerOrchestration.cs"));
        var rollover = File.ReadAllText(Path.Combine(root, "src", "PCCExecutive.App", "Presentation", "AutonomousConversationRolloverRuntime.cs"));
        Assert.DoesNotContain("DispatchId.New()", host, StringComparison.Ordinal);
        Assert.DoesNotContain("DispatchId.New()", workers, StringComparison.Ordinal);
        Assert.Contains("CanonicalDispatchReservationService", host, StringComparison.Ordinal);
        Assert.Contains("AutonomousConversationRolloverRuntime.Attach(gateway)", host, StringComparison.Ordinal);
        Assert.Contains("ConversationLifecycleManager", rollover, StringComparison.Ordinal);
        Assert.Contains("RepairInterruptedRolloversAsync", rollover, StringComparison.Ordinal);
        Assert.Contains("NormalizeActiveConversationTruthAsync", rollover, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "PCCExecutive.sln"))) return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
'@
Write-Utf8 'tests/PCCExecutive.E2E/FinalRuntimeSourceSafetyTests.cs' $sourceSafety.TrimStart()

# Requested source invariants must hold before build begins.
if ((Read-Utf8 $hostPath).Contains('DispatchId.New()')) { throw 'Unsafe Manager caller DispatchId.New remains.' }
if ((Read-Utf8 $orchestratorPath).Contains('DispatchId.New()')) { throw 'Unsafe Worker caller DispatchId.New remains.' }
if (-not (Read-Utf8 $hostPath).Contains('AutonomousConversationRolloverRuntime.Attach(gateway)')) { throw 'Automatic rollover is not attached.' }
if (([regex]::Matches((Read-Utf8 $acceptancePath), [regex]::Escape($ownershipLine))).Count -ne 1) { throw 'Acceptance Ownership compile blocker remains.' }

Write-Host 'Final internal blocker V2 patch applied.'
