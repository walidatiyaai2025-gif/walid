$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Read-Text([string]$Path) {
    [IO.File]::ReadAllText((Join-Path $root $Path))
}
function Write-Text([string]$Path, [string]$Text) {
    $full = Join-Path $root $Path
    $parent = Split-Path -Parent $full
    if (-not (Test-Path $parent)) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
    [IO.File]::WriteAllText($full, $Text, [Text.UTF8Encoding]::new($false))
}
function Replace-Once([string]$Path, [string]$Old, [string]$New) {
    $text = Read-Text $Path
    if ($text.Contains($New)) { return }
    if (-not $text.Contains($Old)) { throw "Expected anchor not found in ${Path}: ${Old}" }
    Write-Text $Path ($text.Replace($Old, $New))
}

# -----------------------------------------------------------------------------
# CI blocker: remove duplicate Browser.Acceptance Ownership member, never skip it.
# -----------------------------------------------------------------------------
$acceptancePath = 'tests/PCCExecutive.Browser.Acceptance/AcceptanceHarness.cs'
$acceptance = Read-Text $acceptancePath
$ownershipLine = '    public IOwnershipProofService Ownership => _ownership;'
$ownershipMatches = [regex]::Matches($acceptance, [regex]::Escape($ownershipLine)).Count
if ($ownershipMatches -gt 1) {
    $first = $acceptance.IndexOf($ownershipLine, [StringComparison]::Ordinal)
    $second = $acceptance.IndexOf($ownershipLine, $first + $ownershipLine.Length, [StringComparison]::Ordinal)
    $removeStart = $second
    if ($removeStart -gt 0 -and $acceptance[$removeStart - 1] -eq "`n") { $removeStart-- }
    $acceptance = $acceptance.Remove($removeStart, $ownershipLine.Length + ($second - $removeStart))
    Write-Text $acceptancePath $acceptance
}
if (([regex]::Matches((Read-Text $acceptancePath), [regex]::Escape($ownershipLine))).Count -ne 1) {
    throw 'ControlledBrowserAcceptanceHarness must expose exactly one Ownership property.'
}

# -----------------------------------------------------------------------------
# SEC-P0-001: stable caller identity over the full durable dispatch correlation.
# The durable reservation port is Application-owned; Infrastructure implements it.
# -----------------------------------------------------------------------------
$appFoundation = 'src/PCCExecutive.Application/Foundation.cs'
Replace-Once $appFoundation `
'public sealed record AgentRequest(ProjectRunId ProjectRunId, LogicalAgentId LogicalAgentId, ConversationId ConversationId, DispatchId DispatchId, string Content, string ContentHash, WorkerSlotId? WorkerSlotId = null, TaskId? TaskId = null, WaveId? WaveId = null);' `
@'public sealed record AgentRequest(ProjectRunId ProjectRunId, LogicalAgentId LogicalAgentId, ConversationId ConversationId, DispatchId DispatchId, string Content, string ContentHash, WorkerSlotId? WorkerSlotId = null, TaskId? TaskId = null, WaveId? WaveId = null, string? ProviderConversationId = null);
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

# Persist the remaining required correlation on the Domain dispatch itself. Optional
# trailing fields keep existing source/test construction compatible.
$domainPath = 'src/PCCExecutive.Domain/Foundation.cs'
Replace-Once $domainPath `
'public sealed record Dispatch(DispatchId Id, ProjectRunId ProjectRunId, WaveId WaveId, TaskId TaskId, LogicalAgentId LogicalAgentId, ConversationId ConversationId, string ContentHash, DateTimeOffset PreparedAt, DispatchState State, DateTimeOffset? SubmittedAt, DateTimeOffset? AcknowledgedAt, DateTimeOffset? CompletedAt, DispatchId? RetryOfDispatchId, string? ReconciliationEvidence);' `
'public sealed record Dispatch(DispatchId Id, ProjectRunId ProjectRunId, WaveId WaveId, TaskId TaskId, LogicalAgentId LogicalAgentId, ConversationId ConversationId, string ContentHash, DateTimeOffset PreparedAt, DispatchState State, DateTimeOffset? SubmittedAt, DateTimeOffset? AcknowledgedAt, DateTimeOffset? CompletedAt, DispatchId? RetryOfDispatchId, string? ReconciliationEvidence, WorkerSlotId? WorkerSlotId = null, string? ProviderConversationId = null);'

# Canonical durable reserve/recover service: exact full-correlation match, with only
# one permitted provider transition (NEW -> established provider id) for crash-after-Enter.
$servicePath = 'src/PCCExecutive.Infrastructure/CanonicalDispatchReservationService.cs'
$serviceText = @'
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
        {
            var reconciled = await _journal.ReconcileAsync(existing, cancellationToken).ConfigureAwait(false);
            return reconciled.Dispatch;
        }

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
            null,
            null,
            null,
            null,
            "canonical-durable-reservation",
            correlation.WorkerSlotId,
            correlation.ProviderConversationId);
        await _journal.SaveAsync(dispatch, cancellationToken).ConfigureAwait(false);
        return dispatch;
    }
}
'@
Write-Text $servicePath $serviceText

# Full correlation match in the existing journal. Legacy rows without the two newly
# persisted fields remain recoverable; a pre-submit NEW provider can mature to the
# established provider conversation after Enter without causing a replacement id.
$journalPath = 'src/PCCExecutive.Infrastructure/AutonomousDispatchSafety.cs'
$journal = Read-Text $journalPath
$oldJournalMethod = @'
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
$newJournalMethod = @'
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
if ($journal.Contains($oldJournalMethod)) { $journal = $journal.Replace($oldJournalMethod, $newJournalMethod); Write-Text $journalPath $journal }
elseif (-not $journal.Contains('WorkerSlotId? workerSlotId')) { throw 'AutonomousDispatchJournal correlation method anchor not found.' }

# Adapter consumes the same canonical reservation service; final Browser ownership
# proof remains before the actual provider SubmitAsync boundary.
$adapterPath = 'src/PCCExecutive.Infrastructure/BrowserAgentProviderAdapter.cs'
$adapter = Read-Text $adapterPath
$adapter = $adapter.Replace(
'        var effectiveDispatchId = request.DispatchId;
        PCCExecutive.Domain.Dispatch? domainDispatch = null;
        AutonomousDispatchJournal? journal = null;
        Func<CancellationToken, Task>? beforeSubmit = null;
        if (_durableStore is not null)
        {
            journal = new AutonomousDispatchJournal(_durableStore);
            var taskId = request.TaskId ?? new TaskId(StableGuid($"runtime-task:{request.ProjectRunId}:{runtime.TaskId}"));
            var waveId = request.WaveId ?? new WaveId(StableGuid($"runtime-wave:{request.ProjectRunId}:{runtime.TaskId}"));
            var existing = await journal.FindEquivalentAsync(request.ProjectRunId, request.LogicalAgentId, taskId, request.ConversationId, request.ContentHash, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                var reconciled = await journal.ReconcileAsync(existing, cancellationToken).ConfigureAwait(false);
                domainDispatch = reconciled.Dispatch;
                effectiveDispatchId = domainDispatch.Id;
                if (reconciled.IsUncertain)
                    return new(effectiveDispatchId, false, false, false, true, null, reconciled.Evidence, "SUBMITTED_UNKNOWN");
                if (reconciled.AlreadyAccepted)
                    return new(effectiveDispatchId, true, domainDispatch.State == PCCExecutive.Domain.DispatchState.GENERATING, domainDispatch.State == PCCExecutive.Domain.DispatchState.COMPLETED, false, null, reconciled.Evidence, null);
                if (!reconciled.SafeToSubmit)
                    return NotSent(effectiveDispatchId, reconciled.Evidence, $"DURABLE_DISPATCH_{domainDispatch.State}");
            }
            else
            {
                domainDispatch = new PCCExecutive.Domain.Dispatch(
                    effectiveDispatchId,
                    request.ProjectRunId,
                    waveId,
                    taskId,
                    request.LogicalAgentId,
                    request.ConversationId,
                    request.ContentHash,
                    DateTimeOffset.UtcNow,
                    PCCExecutive.Domain.DispatchState.PREPARED,
                    null,
                    null,
                    null,
                    null,
                    $"runtime-task:{runtime.TaskId};worker-slot:{expectedSlot ?? "MANAGER"}");
                var prepared = domainDispatch;
                beforeSubmit = ct => journal.SaveAsync(prepared, ct);
            }
        }
',
'        var effectiveDispatchId = request.DispatchId;
        PCCExecutive.Domain.Dispatch? domainDispatch = null;
        AutonomousDispatchJournal? journal = null;
        if (_durableStore is not null)
        {
            var taskId = request.TaskId ?? CanonicalDispatchIdentity.StableTask(request.ProjectRunId, runtime.TaskId!);
            var waveId = request.WaveId ?? CanonicalDispatchIdentity.StableWave(request.ProjectRunId, runtime.TaskId!);
            var providerConversationId = request.ProviderConversationId ?? runtime.ProviderConversationIdentity!;
            if (!StringComparer.Ordinal.Equals(runtime.ProviderConversationIdentity, providerConversationId) && !StringComparer.OrdinalIgnoreCase.Equals(providerConversationId, "NEW"))
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
')
$adapter = $adapter.Replace('        var result = await _provider.SendAsync(runtime.RuntimeId, browserRequest, cancellationToken, beforeSubmit).ConfigureAwait(false);','        var result = await _provider.SendAsync(runtime.RuntimeId, browserRequest, cancellationToken).ConfigureAwait(false);')
if ($adapter.Contains('beforeSubmit = ct => journal.SaveAsync(prepared, ct);')) { throw 'Unsafe deferred adapter reservation remains.' }
Write-Text $adapterPath $adapter

# Worker orchestrator receives the provider conversation and reserves/reuses the
# canonical Domain Dispatch before it calls the provider.
$orchestratorPath = 'src/PCCExecutive.Application/ManagerWorkerOrchestration.cs'
Replace-Once $orchestratorPath `
'public sealed record WorkerExecutionBinding(WorkerSlotId SlotId, LogicalAgentId LogicalAgentId, ConversationId ConversationId);' `
'public sealed record WorkerExecutionBinding(WorkerSlotId SlotId, LogicalAgentId LogicalAgentId, ConversationId ConversationId, string? ProviderConversationId = null);'
$orchestrator = Read-Text $orchestratorPath
if (-not $orchestrator.Contains('private readonly ICanonicalDispatchReservationService? _dispatchReservations;')) {
    $orchestrator = $orchestrator.Replace('    private readonly IWorkerHandoffValidator _handoffValidator;\n    private readonly TimeSpan _baseDispatchInterval;', '    private readonly IWorkerHandoffValidator _handoffValidator;\n    private readonly ICanonicalDispatchReservationService? _dispatchReservations;\n    private readonly TimeSpan _baseDispatchInterval;')
    $orchestrator = $orchestrator.Replace('        IWorkerHandoffValidator? handoffValidator = null,\n        TimeSpan? baseDispatchInterval = null)', '        IWorkerHandoffValidator? handoffValidator = null,\n        TimeSpan? baseDispatchInterval = null,\n        ICanonicalDispatchReservationService? dispatchReservations = null)')
    $orchestrator = $orchestrator.Replace('        _handoffValidator = handoffValidator ?? new WorkerHandoffValidator();\n        _baseDispatchInterval = baseDispatchInterval ?? TimeSpan.FromSeconds(10);', '        _handoffValidator = handoffValidator ?? new WorkerHandoffValidator();\n        _dispatchReservations = dispatchReservations;\n        _baseDispatchInterval = baseDispatchInterval ?? TimeSpan.FromSeconds(10);')
}
$oldWorkerRequest = '            var request = new AgentRequest(projectRunId, binding.LogicalAgentId, binding.ConversationId, DispatchId.New(), content, hash, binding.SlotId, task.Id, plan.WaveId);'
$newWorkerRequest = @'
            var providerConversationId = binding.ProviderConversationId ?? binding.ConversationId.ToString();
            var correlation = new DurableDispatchCorrelation(projectRunId, binding.LogicalAgentId, binding.SlotId, task.Id, plan.WaveId, binding.ConversationId, providerConversationId, hash);
            var dispatchId = CanonicalDispatchIdentity.Create(correlation);
            if (_dispatchReservations is not null)
                dispatchId = (await _dispatchReservations.ReserveOrRecoverAsync(correlation, cancellationToken).ConfigureAwait(false)).Id;
            var request = new AgentRequest(projectRunId, binding.LogicalAgentId, binding.ConversationId, dispatchId, content, hash, binding.SlotId, task.Id, plan.WaveId, providerConversationId);
'@
if ($orchestrator.Contains($oldWorkerRequest)) { $orchestrator = $orchestrator.Replace($oldWorkerRequest, $newWorkerRequest) }
if ($orchestrator.Contains('DispatchId.New()')) { throw 'Worker orchestrator still contains caller-side DispatchId.New().' }
Write-Text $orchestratorPath $orchestrator

# Runtime host: Manager initial/review and Worker production orchestration all consume
# the same reservation service. No Manager/Worker caller-side random DispatchId remains.
$hostPath = 'src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs'
$host = Read-Text $hostPath
if (-not $host.Contains('private readonly ICanonicalDispatchReservationService _dispatchReservations;')) {
    $host = $host.Replace('    private readonly CrashConsistentOrchestrationStore _orchestrationStore;','    private readonly CrashConsistentOrchestrationStore _orchestrationStore;\n    private readonly ICanonicalDispatchReservationService _dispatchReservations;\n    private AutonomousConversationRolloverRuntime? _rolloverRuntime;')
    $host = $host.Replace('        _orchestrationStore = new CrashConsistentOrchestrationStore(store);','        _orchestrationStore = new CrashConsistentOrchestrationStore(store);\n        _dispatchReservations = new CanonicalDispatchReservationService(store);')
}
$oldManager = '        var request = new AgentRequest(run.Id, managerAgentId, new ConversationId(Guid.Parse(logicalConversation)), DispatchId.New(), prompt, hash);'
$newManager = @'
        runtime = await _runtimeRegistry.GetAsync(runtime.RuntimeId, cancellationToken).ConfigureAwait(false) ?? runtime;
        var managerConversation = new ConversationId(Guid.Parse(logicalConversation));
        var managerTaskKey = runtime.TaskId ?? $"manager-plan:{run.Id}";
        var managerTaskId = CanonicalDispatchIdentity.StableTask(run.Id, managerTaskKey);
        var managerWaveId = CanonicalDispatchIdentity.StableWave(run.Id, managerTaskKey);
        var managerProviderConversation = runtime.ProviderConversationIdentity ?? "NEW";
        var managerCorrelation = new DurableDispatchCorrelation(run.Id, managerAgentId, null, managerTaskId, managerWaveId, managerConversation, managerProviderConversation, hash);
        var managerDispatch = await _dispatchReservations.ReserveOrRecoverAsync(managerCorrelation, cancellationToken).ConfigureAwait(false);
        var request = new AgentRequest(run.Id, managerAgentId, managerConversation, managerDispatch.Id, prompt, hash, null, managerTaskId, managerWaveId, managerProviderConversation);
'@
if ($host.Contains($oldManager)) { $host = $host.Replace($oldManager, $newManager) }
$oldReview = '        var request = new AgentRequest(run.Id, managerAgentId, new ConversationId(Guid.Parse(logicalConversation)), DispatchId.New(), prompt, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(prompt))).ToLowerInvariant());'
$newReview = @'
        var reviewHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(prompt))).ToLowerInvariant();
        runtime = await _runtimeRegistry.GetAsync(runtime.RuntimeId, cancellationToken).ConfigureAwait(false) ?? runtime;
        var reviewConversation = new ConversationId(Guid.Parse(logicalConversation));
        var reviewTaskKey = runtime.TaskId ?? $"manager-review:{review.WaveId}";
        var reviewTaskId = CanonicalDispatchIdentity.StableTask(run.Id, reviewTaskKey);
        var reviewWaveId = CanonicalDispatchIdentity.StableWave(run.Id, reviewTaskKey);
        var reviewProviderConversation = runtime.ProviderConversationIdentity ?? providerConversation;
        var reviewCorrelation = new DurableDispatchCorrelation(run.Id, managerAgentId, null, reviewTaskId, reviewWaveId, reviewConversation, reviewProviderConversation, reviewHash);
        var reviewDispatch = await _dispatchReservations.ReserveOrRecoverAsync(reviewCorrelation, cancellationToken).ConfigureAwait(false);
        var request = new AgentRequest(run.Id, managerAgentId, reviewConversation, reviewDispatch.Id, prompt, reviewHash, null, reviewTaskId, reviewWaveId, reviewProviderConversation);
'@
if ($host.Contains($oldReview)) { $host = $host.Replace($oldReview, $newReview) }
$host = $host.Replace('            bindings.Add(new WorkerExecutionBinding(slot, agentId, conversationId));','            bindings.Add(new WorkerExecutionBinding(slot, agentId, conversationId, runtime.ProviderConversationIdentity ?? "NEW"));')
$host = $host.Replace('        var result = await new ManagerWorkerOrchestrator(_agentProvider, baseDispatchInterval: TimeSpan.FromSeconds(_settings.BaseDispatchIntervalSeconds))','        var result = await new ManagerWorkerOrchestrator(_agentProvider, baseDispatchInterval: TimeSpan.FromSeconds(_settings.BaseDispatchIntervalSeconds), dispatchReservations: _dispatchReservations)')
if ($host.Contains('DispatchId.New()')) { throw 'Runtime host still contains caller-side DispatchId.New().' }
Write-Text $hostPath $host

# -----------------------------------------------------------------------------
# Automatic governed rollover: consume PR #34's crash-safe composition, adapt it
# directly to the current canonical host, and repair interrupted lineage before
# the host can start AutoResume.
# -----------------------------------------------------------------------------
$recoveryRef = 'worker/pcc-final-recovery-completion'
& git fetch origin $recoveryRef --quiet
if ($LASTEXITCODE -ne 0) { throw "Unable to fetch ${recoveryRef}." }
$rolloverSource = (& git show "origin/${recoveryRef}:src/PCCExecutive.App/Presentation/AutonomousConversationRolloverRuntime.cs" | Out-String)
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($rolloverSource)) { throw 'Unable to load PR #34 rollover source.' }
$rolloverSource = $rolloverSource.Replace('    private readonly RecoveryCompletionPresentationGateway _gateway;' + "`r`n", '')
$rolloverSource = $rolloverSource.Replace('    private readonly RecoveryCompletionPresentationGateway _gateway;' + "`n", '')
$rolloverSource = $rolloverSource.Replace('    private AutonomousConversationRolloverRuntime(RecoveryCompletionPresentationGateway gateway)', '    private AutonomousConversationRolloverRuntime(PccExecutiveRuntimeHost host)')
$rolloverSource = $rolloverSource.Replace('        _gateway = gateway;' + "`r`n" + '        _host = RecoveryGatewayRolloverAccess.Inner(gateway);', '        _host = host;')
$rolloverSource = $rolloverSource.Replace('        _gateway = gateway;' + "`n" + '        _host = RecoveryGatewayRolloverAccess.Inner(gateway);', '        _host = host;')
$rolloverSource = $rolloverSource.Replace('    public static AutonomousConversationRolloverRuntime Attach(RecoveryCompletionPresentationGateway gateway) => new(gateway);', '    public static AutonomousConversationRolloverRuntime Attach(PccExecutiveRuntimeHost host) => new(host);')
$rolloverSource = [regex]::Replace($rolloverSource, '(?s)\r?\ninternal static class RecoveryGatewayRolloverAccess\s*\{.*?\r?\n\}\r?\n\r?\ninternal static class PccHostConversationAccess', "`r`ninternal static class PccHostConversationAccess")
if ($rolloverSource.Contains('RecoveryCompletionPresentationGateway') -or $rolloverSource.Contains('RecoveryGatewayRolloverAccess')) { throw 'PR #34 rollover adaptation still depends on obsolete recovery gateway.' }
$recoveryAccess = @'

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
    internal static extern ref INewSendPausePort NewSendPause(PccExecutiveRuntimeHost host);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_autopilot")]
    internal static extern ref string Autopilot(PccExecutiveRuntimeHost host);
}
'@
$conversationAccessIndex = $rolloverSource.IndexOf('internal static class PccHostConversationAccess', [StringComparison]::Ordinal)
if ($conversationAccessIndex -lt 0) { throw 'PR #34 rollover access anchor missing.' }
$rolloverSource = $rolloverSource.Insert($conversationAccessIndex, $recoveryAccess + "`r`n")
Write-Text 'src/PCCExecutive.App/Presentation/AutonomousConversationRolloverRuntime.cs' $rolloverSource

# Attach before AutoResume and dispose before SafeShutdown/store disposal.
$host = Read-Text $hostPath
$attachAnchor = '            if (run is not null) gateway.RecoverStartupBrowserStateAsync().GetAwaiter().GetResult();\n            gateway.RefreshLocalSnapshotAsync().GetAwaiter().GetResult();'
$attachReplacement = '            if (run is not null) gateway.RecoverStartupBrowserStateAsync().GetAwaiter().GetResult();\n            gateway._rolloverRuntime = AutonomousConversationRolloverRuntime.Attach(gateway);\n            gateway.RefreshLocalSnapshotAsync().GetAwaiter().GetResult();'
if (-not $host.Contains('gateway._rolloverRuntime = AutonomousConversationRolloverRuntime.Attach(gateway);')) {
    if (-not $host.Contains($attachAnchor)) { throw 'Runtime rollover startup attachment anchor missing.' }
    $host = $host.Replace($attachAnchor, $attachReplacement)
}
$disposeAnchor = '        _autopilotCancellation.Cancel();\n        if (_autopilotTask is not null)'
$disposeReplacement = '        _autopilotCancellation.Cancel();\n        if (_rolloverRuntime is not null)\n            await _rolloverRuntime.DisposeAsync().ConfigureAwait(false);\n        if (_autopilotTask is not null)'
if (-not $host.Contains('await _rolloverRuntime.DisposeAsync().ConfigureAwait(false);')) {
    if (-not $host.Contains($disposeAnchor)) { throw 'Runtime rollover disposal anchor missing.' }
    $host = $host.Replace($disposeAnchor, $disposeReplacement)
}
Write-Text $hostPath $host

# -----------------------------------------------------------------------------
# Deterministic closure tests: caller identities, SUBMITTED_UNKNOWN recovery, no
# random production paths, and production automatic rollover composition.
# -----------------------------------------------------------------------------
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
        var correlation = Manager("manager-initial");
        Assert.Equal(CanonicalDispatchIdentity.Create(correlation), CanonicalDispatchIdentity.Create(correlation));
    }

    [Fact]
    public void Manager_review_crash_after_enter_reuses_same_dispatch_id()
    {
        var correlation = Manager("manager-review");
        Assert.Equal(CanonicalDispatchIdentity.Create(correlation), CanonicalDispatchIdentity.Create(correlation));
    }

    [Fact]
    public void Worker_crash_after_enter_reuses_same_dispatch_id_and_full_correlation_matters()
    {
        var run = ProjectRunId.New(); var agent = LogicalAgentId.New(); var task = TaskId.New(); var wave = WaveId.New(); var conversation = ConversationId.New();
        var one = new DurableDispatchCorrelation(run, agent, new WorkerSlotId(1), task, wave, conversation, "provider-1", "hash");
        var same = one with { };
        var wrongSlot = one with { WorkerSlotId = new WorkerSlotId(2) };
        var wrongProvider = one with { ProviderConversationId = "provider-2" };
        Assert.Equal(CanonicalDispatchIdentity.Create(one), CanonicalDispatchIdentity.Create(same));
        Assert.NotEqual(CanonicalDispatchIdentity.Create(one), CanonicalDispatchIdentity.Create(wrongSlot));
        Assert.NotEqual(CanonicalDispatchIdentity.Create(one), CanonicalDispatchIdentity.Create(wrongProvider));
    }

    private static DurableDispatchCorrelation Manager(string key)
    {
        var run = ProjectRunId.New(); var agent = LogicalAgentId.New(); var conversation = ConversationId.New();
        var task = CanonicalDispatchIdentity.StableTask(run, key); var wave = CanonicalDispatchIdentity.StableWave(run, key);
        return new(run, agent, null, task, wave, conversation, "provider-manager", "hash");
    }
}
'@
Write-Text 'tests/PCCExecutive.Application.Tests/CanonicalDispatchIdentityTests.cs' $identityTests

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
    public async Task Restart_submitted_unknown_recovers_same_dispatch_id_and_never_allocates_replacement()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pcc-dispatch-reservation-{Guid.NewGuid():N}.db");
        try
        {
            DispatchId firstId;
            var run = ProjectRunId.New(); var agent = LogicalAgentId.New(); var task = TaskId.New(); var wave = WaveId.New(); var conversation = ConversationId.New();
            var correlation = new DurableDispatchCorrelation(run, agent, new WorkerSlotId(1), task, wave, conversation, "NEW", "hash");
            await using (var store = new SqliteStateStore(path))
            {
                await store.InitializeAsync();
                var service = new CanonicalDispatchReservationService(store);
                var first = await service.ReserveOrRecoverAsync(correlation);
                firstId = first.Id;
                await store.ReserveAsync(first.Id.ToString(), first.ContentHash);
                await store.UpdateAsync(first.Id.ToString(), PCCExecutive.Browser.DispatchState.Submitting, "crash-after-enter");
                await store.SaveDispatchAsync(first with { State = PCCExecutive.Domain.DispatchState.SUBMITTED_UNKNOWN });
            }
            await using (var reopened = new SqliteStateStore(path))
            {
                await reopened.InitializeAsync();
                var service = new CanonicalDispatchReservationService(reopened);
                var recovered = await service.ReserveOrRecoverAsync(correlation with { ProviderConversationId = "provider-established" });
                Assert.Equal(firstId, recovered.Id);
                Assert.Equal(PCCExecutive.Domain.DispatchState.SUBMITTED_UNKNOWN, recovered.State);
                Assert.Single((await new AutonomousDispatchJournal(reopened).ListAsync(run)).Where(x => x.ContentHash == "hash"));
            }
        }
        finally { try { File.Delete(path); } catch { } }
    }
}
'@
Write-Text 'tests/PCCExecutive.Infrastructure.Tests/CanonicalDispatchReservationServiceTests.cs' $reservationTests

$compositionTests = @'
using Xunit;

namespace PCCExecutive.E2E;

public sealed class FinalRuntimeSourceSafetyTests
{
    [Fact]
    public void Production_Manager_Worker_callers_have_no_random_dispatch_id_and_rollover_is_automatic()
    {
        var root = FindRepositoryRoot();
        var host = File.ReadAllText(Path.Combine(root, "src", "PCCExecutive.App", "Presentation", "IntegratedPresentationGateway.cs"));
        var orchestrator = File.ReadAllText(Path.Combine(root, "src", "PCCExecutive.Application", "ManagerWorkerOrchestration.cs"));
        var rollover = File.ReadAllText(Path.Combine(root, "src", "PCCExecutive.App", "Presentation", "AutonomousConversationRolloverRuntime.cs"));
        Assert.DoesNotContain("DispatchId.New()", host, StringComparison.Ordinal);
        Assert.DoesNotContain("DispatchId.New()", orchestrator, StringComparison.Ordinal);
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
Write-Text 'tests/PCCExecutive.E2E/FinalRuntimeSourceSafetyTests.cs' $compositionTests

# Final hard assertions for the requested three blockers.
$hostFinal = Read-Text $hostPath
$workerFinal = Read-Text $orchestratorPath
if ($hostFinal.Contains('DispatchId.New()') -or $workerFinal.Contains('DispatchId.New()')) { throw 'Unsafe caller-side DispatchId.New() remains.' }
if (-not $hostFinal.Contains('AutonomousConversationRolloverRuntime.Attach(gateway)')) { throw 'Automatic rollover is not attached to production runtime startup.' }
if (([regex]::Matches((Read-Text $acceptancePath), [regex]::Escape($ownershipLine))).Count -ne 1) { throw 'Browser.Acceptance Ownership compile blocker remains.' }

Write-Host 'Final internal runtime blockers patch applied: SEC-P0-001 stable durable dispatch, automatic rollover, Browser.Acceptance compile repair.'
