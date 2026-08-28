$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

function Read-Repo([string]$relative) {
    return [IO.File]::ReadAllText((Join-Path $root $relative))
}
function Write-Repo([string]$relative, [string]$text) {
    [IO.File]::WriteAllText((Join-Path $root $relative), $text, [Text.UTF8Encoding]::new($false))
}
function Replace-Exact([string]$text, [string]$old, [string]$new, [string]$label) {
    if (-not $text.Contains($old)) { throw "PATCH_ANCHOR_NOT_FOUND:$label" }
    return $text.Replace($old, $new)
}
function Replace-Once([string]$text, [string]$old, [string]$new, [string]$label) {
    $first = $text.IndexOf($old, [StringComparison]::Ordinal)
    if ($first -lt 0) { throw "PATCH_ANCHOR_NOT_FOUND:$label" }
    $second = $text.IndexOf($old, $first + $old.Length, [StringComparison]::Ordinal)
    if ($second -ge 0) { throw "PATCH_ANCHOR_NOT_UNIQUE:$label" }
    return $text.Substring(0, $first) + $new + $text.Substring($first + $old.Length)
}

# 1) Strict canonical conversation matching: runtime string must equal ConversationId.ToString() exactly.
$path = 'src/PCCExecutive.Infrastructure/BrowserAgentProviderAdapter.cs'
$text = Read-Repo $path
$old = @'
    private static bool SameConversationIdentity(string runtimeIdentity, ConversationId expected)
    {
        if (!Guid.TryParse(runtimeIdentity, out var runtimeGuid)) return false;
        return runtimeGuid == expected.Value;
    }
'@
$new = @'
    private static bool SameConversationIdentity(string runtimeIdentity, ConversationId expected) =>
        StringComparer.Ordinal.Equals(runtimeIdentity, expected.ToString());
'@
$text = Replace-Exact $text $old $new 'strict-conversation-identity'
Write-Repo $path $text

# 2) Manager initial + review: construct/validate domain ConversationId first, persist its canonical N string everywhere.
$path = 'src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs'
$text = Read-Repo $path
$old = @'
        var logicalConversation = runtime.ConversationIdentity;
        if (string.IsNullOrWhiteSpace(logicalConversation))
        {
            logicalConversation = StableGuid($"conversation:{run.Id}:manager:1").ToString();
            var bound = await _sessions.BindDispatchTargetAsync(runtime.RuntimeId, $"manager-plan:{run.Id}", logicalConversation, "NEW", cancellationToken).ConfigureAwait(false);
            if (!bound.Succeeded) throw new InvalidOperationException($"Manager conversation binding failed: {bound.Reason}.");
            await _store.SaveBrowserConversationAsync(new ConversationRecord { ConversationId = logicalConversation, LogicalAgentId = managerAgentId.ToString(), ProjectRunId = run.Id.ToString(), Sequence = 1, UrlOrProviderIdentity = "NEW", CreatedAt = DateTimeOffset.UtcNow, State = ConversationLifecycleState.Active }, cancellationToken).ConfigureAwait(false);
        }

        var baseline = await _baseline.BuildAsync(_projectControlId ?? throw new InvalidOperationException("Selected PCC project identity is unavailable."), cancellationToken).ConfigureAwait(false);
'@
$new = @'
        var logicalConversation = runtime.ConversationIdentity;
        if (string.IsNullOrWhiteSpace(logicalConversation))
        {
            var createdConversation = new ConversationId(StableGuid($"conversation:{run.Id}:manager:1"));
            logicalConversation = createdConversation.ToString();
            var bound = await _sessions.BindDispatchTargetAsync(runtime.RuntimeId, $"manager-plan:{run.Id}", logicalConversation, "NEW", cancellationToken).ConfigureAwait(false);
            if (!bound.Succeeded) throw new InvalidOperationException($"Manager conversation binding failed: {bound.Reason}.");
            await _store.SaveBrowserConversationAsync(new ConversationRecord { ConversationId = logicalConversation, LogicalAgentId = managerAgentId.ToString(), ProjectRunId = run.Id.ToString(), Sequence = 1, UrlOrProviderIdentity = "NEW", CreatedAt = DateTimeOffset.UtcNow, State = ConversationLifecycleState.Active }, cancellationToken).ConfigureAwait(false);
        }
        var managerConversation = new ConversationId(Guid.Parse(logicalConversation));
        if (!StringComparer.Ordinal.Equals(logicalConversation, managerConversation.ToString()))
            throw new InvalidOperationException("NON_CANONICAL_LOGICAL_CONVERSATION_IDENTITY: Manager runtime conversation must equal ConversationId.ToString().");

        var baseline = await _baseline.BuildAsync(_projectControlId ?? throw new InvalidOperationException("Selected PCC project identity is unavailable."), cancellationToken).ConfigureAwait(false);
'@
$text = Replace-Exact $text $old $new 'manager-initial-canonical'
$old = @'
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(prompt))).ToLowerInvariant();
        await PersistAgentBindingAsync(managerAgentId, null, null, new ConversationId(Guid.Parse(logicalConversation)), cancellationToken).ConfigureAwait(false);
        await _orchestrationStore.SaveAsync(new OrchestrationRecoverySnapshot(run, _currentWave, _runtimeTasks, _assignments, [], null, OrchestrationPhase.ManagerPlanning, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
        runtime = await _runtimeRegistry.GetAsync(runtime.RuntimeId, cancellationToken).ConfigureAwait(false) ?? runtime;
        var managerConversation = new ConversationId(Guid.Parse(logicalConversation));
'@
$new = @'
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(prompt))).ToLowerInvariant();
        await PersistAgentBindingAsync(managerAgentId, null, null, managerConversation, cancellationToken).ConfigureAwait(false);
        await _orchestrationStore.SaveAsync(new OrchestrationRecoverySnapshot(run, _currentWave, _runtimeTasks, _assignments, [], null, OrchestrationPhase.ManagerPlanning, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
        runtime = await _runtimeRegistry.GetAsync(runtime.RuntimeId, cancellationToken).ConfigureAwait(false) ?? runtime;
'@
$text = Replace-Exact $text $old $new 'manager-initial-domain-use'
$old = @'
        var logicalConversation = runtime.ConversationIdentity ?? throw new InvalidOperationException("Manager logical conversation is unavailable.");
        var providerConversation = runtime.ProviderConversationIdentity ?? throw new InvalidOperationException("Manager provider conversation is unavailable.");
'@
$new = @'
        var logicalConversation = runtime.ConversationIdentity ?? throw new InvalidOperationException("Manager logical conversation is unavailable.");
        var managerReviewConversation = new ConversationId(Guid.Parse(logicalConversation));
        if (!StringComparer.Ordinal.Equals(logicalConversation, managerReviewConversation.ToString()))
            throw new InvalidOperationException("NON_CANONICAL_LOGICAL_CONVERSATION_IDENTITY: Manager review runtime conversation must equal ConversationId.ToString().");
        var providerConversation = runtime.ProviderConversationIdentity ?? throw new InvalidOperationException("Manager provider conversation is unavailable.");
'@
$text = Replace-Exact $text $old $new 'manager-review-canonical'
$old = @'
        await _sessions.BindDispatchTargetAsync(runtime.RuntimeId, $"manager-review:{review.WaveId}", logicalConversation, providerConversation, cancellationToken).ConfigureAwait(false);
        await PersistAgentBindingAsync(managerAgentId, null, null, new ConversationId(Guid.Parse(logicalConversation)), cancellationToken).ConfigureAwait(false);
'@
$new = @'
        await _sessions.BindDispatchTargetAsync(runtime.RuntimeId, $"manager-review:{review.WaveId}", logicalConversation, providerConversation, cancellationToken).ConfigureAwait(false);
        await PersistAgentBindingAsync(managerAgentId, null, null, managerReviewConversation, cancellationToken).ConfigureAwait(false);
'@
$text = Replace-Exact $text $old $new 'manager-review-domain-use'
Write-Repo $path $text

# 3) Rollover: domain ConversationId first; worker continuation preserves the actual TaskId and current WaveId.
$path = 'src/PCCExecutive.App/Presentation/AutonomousConversationRolloverRuntime.cs'
$text = Read-Repo $path
$old = @'
        var candidateConversationId = Guid.NewGuid().ToString();
        var candidateRuntime = await PccHostConversationAccess.Sessions(_host).CreateAsync(new BrowserSessionRequest(
'@
$new = @'
        var candidateConversation = ConversationId.New();
        var candidateConversationId = candidateConversation.ToString();
        var candidateRuntime = await PccHostConversationAccess.Sessions(_host).CreateAsync(new BrowserSessionRequest(
'@
$text = Replace-Exact $text $old $new 'rollover-canonical-create'
$old = @'
        var providerIdentity = candidateRuntime.ProviderConversationIdentity ?? "NEW";
        var logicalConversation = new ConversationId(Guid.Parse(candidate.ConversationId));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(packet))).ToLowerInvariant();
        var taskKey = candidateRuntime.TaskId ?? $"rollover:{predecessor.LogicalAgentId}";
        var taskId = PCCExecutive.Application.CanonicalDispatchIdentity.StableTask(new ProjectRunId(Guid.Parse(predecessor.ProjectRunId)), taskKey);
        var waveId = PCCExecutive.Application.CanonicalDispatchIdentity.StableWave(new ProjectRunId(Guid.Parse(predecessor.ProjectRunId)), taskKey);
        var correlation = new PCCExecutive.Application.DurableDispatchCorrelation(new ProjectRunId(Guid.Parse(predecessor.ProjectRunId)), new LogicalAgentId(Guid.Parse(predecessor.LogicalAgentId)), candidateRuntime.WorkerSlotId is null ? null : new WorkerSlotId(int.Parse(candidateRuntime.WorkerSlotId)), taskId, waveId, logicalConversation, providerIdentity, hash);
        var dispatch = await new CanonicalDispatchReservationService(_store).ReserveOrRecoverAsync(correlation, cancellationToken).ConfigureAwait(false);
        var request = new PCCExecutive.Application.AgentRequest(correlation.ProjectRunId, correlation.LogicalAgentId, logicalConversation, dispatch.Id, packet, hash, correlation.WorkerSlotId, candidateRuntime.WorkerSlotId is null ? null : taskId, candidateRuntime.WorkerSlotId is null ? null : waveId, providerIdentity);
'@
$new = @'
        var providerIdentity = candidateRuntime.ProviderConversationIdentity ?? "NEW";
        var logicalConversation = candidateConversation;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(packet))).ToLowerInvariant();
        var projectRunId = new ProjectRunId(Guid.Parse(predecessor.ProjectRunId));
        var logicalAgentId = new LogicalAgentId(Guid.Parse(predecessor.LogicalAgentId));
        var workerSlotId = candidateRuntime.WorkerSlotId is null ? null : new WorkerSlotId(int.Parse(candidateRuntime.WorkerSlotId));
        TaskId taskId;
        WaveId waveId;
        if (workerSlotId is not null)
        {
            if (!Guid.TryParse(candidateRuntime.TaskId, out var currentTaskGuid))
            {
                await RollbackCandidateAsync(predecessor, candidate, candidateRuntime, checkpointId, "ROLLOVER_WORKER_TASK_IDENTITY_INVALID", cancellationToken).ConfigureAwait(false);
                return;
            }
            var currentWave = PccHostRecoveryAccess.CurrentWave(_host);
            if (currentWave is null)
            {
                await RollbackCandidateAsync(predecessor, candidate, candidateRuntime, checkpointId, "ROLLOVER_WORKER_WAVE_IDENTITY_MISSING", cancellationToken).ConfigureAwait(false);
                return;
            }
            taskId = new TaskId(currentTaskGuid);
            waveId = currentWave.Id;
        }
        else
        {
            var taskKey = candidateRuntime.TaskId ?? $"rollover:{predecessor.LogicalAgentId}";
            taskId = PCCExecutive.Application.CanonicalDispatchIdentity.StableTask(projectRunId, taskKey);
            waveId = PCCExecutive.Application.CanonicalDispatchIdentity.StableWave(projectRunId, taskKey);
        }
        var correlation = new PCCExecutive.Application.DurableDispatchCorrelation(projectRunId, logicalAgentId, workerSlotId, taskId, waveId, logicalConversation, providerIdentity, hash);
        var dispatch = await new CanonicalDispatchReservationService(_store).ReserveOrRecoverAsync(correlation, cancellationToken).ConfigureAwait(false);
        var request = new PCCExecutive.Application.AgentRequest(correlation.ProjectRunId, correlation.LogicalAgentId, logicalConversation, dispatch.Id, packet, hash, workerSlotId, workerSlotId is null ? null : taskId, workerSlotId is null ? null : waveId, providerIdentity);
'@
$text = Replace-Exact $text $old $new 'rollover-worker-task-wave'
$old = @'
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_run")]
    internal static extern ref ProjectRun? Run(PccExecutiveRuntimeHost host);
'@
$new = @'
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_run")]
    internal static extern ref ProjectRun? Run(PccExecutiveRuntimeHost host);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_currentWave")]
    internal static extern ref Wave? CurrentWave(PccExecutiveRuntimeHost host);
'@
$text = Replace-Exact $text $old $new 'rollover-current-wave-access'
Write-Repo $path $text

# 4) Hostile identity test: same GUID, deliberately non-canonical D string must still fail strict recovery.
$path = 'tests/PCCExecutive.E2E/ProductionRuntimeSecurityNegativeTests.cs'
$text = Read-Repo $path
$old = @'
        var mismatchedConversation = ConversationId.New();
        await ((IBrowserRuntimeRegistry)h.Store).UpsertAsync(managerRuntime with { ConversationIdentity = mismatchedConversation.ToString() });
'@
$new = @'
        var nonCanonicalManagerConversation = correctManagerConversation.Value.ToString("D");
        Assert.False(StringComparer.Ordinal.Equals(correctManagerConversation.ToString(), nonCanonicalManagerConversation));
        await ((IBrowserRuntimeRegistry)h.Store).UpsertAsync(managerRuntime with { ConversationIdentity = nonCanonicalManagerConversation });
'@
$text = Replace-Exact $text $old $new 'hostile-d-format-tamper'
Write-Repo $path $text

# 5) Ordered success path: prove exact canonical identity before and after restart, and for rollover successors.
$path = 'tests/PCCExecutive.E2E/ProductionRuntime32StageAcceptanceTests.cs'
$text = Read-Repo $path
$old = @'
        var managerDispatches = await new AutonomousDispatchJournal(h.Store).ListAsync(runId);
        Assert.Single(managerDispatches, x => x.LogicalAgentId == h.ManagerAgentId);
        Assert.Equal(1, h.Adapter.EnterCount(managerRuntime.RuntimeId));
        Stage(9);
'@
$new = @'
        var managerDispatches = await new AutonomousDispatchJournal(h.Store).ListAsync(runId);
        var managerDispatch = Assert.Single(managerDispatches, x => x.LogicalAgentId == h.ManagerAgentId);
        var managerLogical = await h.Store.LoadLogicalAgentAsync(h.ManagerAgentId);
        var managerConversationRecord = await h.ActiveBrowserConversationAsync(h.ManagerAgentId);
        Assert.NotNull(managerLogical?.CurrentConversationId);
        Assert.True(StringComparer.Ordinal.Equals(managerRuntime.ConversationIdentity, managerLogical!.CurrentConversationId!.Value.ToString()));
        Assert.True(StringComparer.Ordinal.Equals(managerRuntime.ConversationIdentity, managerConversationRecord.ConversationId));
        Assert.True(StringComparer.Ordinal.Equals(managerRuntime.ConversationIdentity, managerDispatch.ConversationId.ToString()));
        Assert.Equal(1, h.Adapter.EnterCount(managerRuntime.RuntimeId));
        Stage(9);
'@
$text = Replace-Exact $text $old $new 'manager-identity-acceptance'
$old = @'
            Assert.Equal(slotNumber.ToString(), runtime.WorkerSlotId);
            Assert.Equal(expectedTask.ToString(), runtime.TaskId);
            Assert.Equal(1, h.Adapter.EnterCount(runtime.RuntimeId));
'@
$new = @'
            Assert.Equal(slotNumber.ToString(), runtime.WorkerSlotId);
            Assert.Equal(expectedTask.ToString(), runtime.TaskId);
            var workerLogical = await h.Store.LoadLogicalAgentAsync(workerIds[slotNumber - 1]);
            var workerConversationRecord = await h.ActiveBrowserConversationAsync(workerIds[slotNumber - 1]);
            Assert.NotNull(workerLogical?.CurrentConversationId);
            Assert.True(StringComparer.Ordinal.Equals(runtime.ConversationIdentity, workerLogical!.CurrentConversationId!.Value.ToString()));
            Assert.True(StringComparer.Ordinal.Equals(runtime.ConversationIdentity, workerConversationRecord.ConversationId));
            Assert.Equal(1, h.Adapter.EnterCount(runtime.RuntimeId));
'@
$text = Replace-Exact $text $old $new 'worker-identity-acceptance'
$old = @'
        var preRestartRun = h.Run.Id;
        var preRestartWave = h.CurrentWave!.Id;
        var preRestartAssignment = h.Assignments.Single();
        var preRestartAgents = new[] { h.ManagerAgentId }.Concat(h.WorkerAgentIds).ToArray();
        await h.PauseAsync();
'@
$new = @'
        var preRestartRun = h.Run.Id;
        var preRestartWave = h.CurrentWave!.Id;
        var preRestartAssignment = h.Assignments.Single();
        var preRestartAgents = new[] { h.ManagerAgentId }.Concat(h.WorkerAgentIds).ToArray();
        var preRestartIdentity = new Dictionary<LogicalAgentId, (string? Slot, string? Task, string Conversation, string? Provider)>();
        foreach (var agent in preRestartAgents)
        {
            var runtime = await h.RuntimeForAsync(agent);
            var logical = await h.Store.LoadLogicalAgentAsync(agent);
            Assert.NotNull(logical?.CurrentConversationId);
            Assert.True(StringComparer.Ordinal.Equals(runtime.ConversationIdentity, logical!.CurrentConversationId!.Value.ToString()));
            preRestartIdentity[agent] = (runtime.WorkerSlotId, runtime.TaskId, runtime.ConversationIdentity!, runtime.ProviderConversationIdentity);
        }
        await h.PauseAsync();
'@
$text = Replace-Exact $text $old $new 'restart-capture-identity'
$old = @'
        foreach (var agent in preRestartAgents)
            Assert.NotNull(await h.Store.LoadLogicalAgentAsync(agent));
        await h.ResumeAsync();
'@
$new = @'
        foreach (var agent in preRestartAgents)
        {
            var logical = await h.Store.LoadLogicalAgentAsync(agent);
            var runtime = await h.RuntimeForAsync(agent);
            Assert.NotNull(logical);
            Assert.Equal(BrowserReconciliationKind.MATCHED, new BrowserSessionReconciliationService().Reconcile(logical!, runtime).Outcome);
            var before = preRestartIdentity[agent];
            Assert.True(StringComparer.Ordinal.Equals(before.Slot, runtime.WorkerSlotId));
            Assert.True(StringComparer.Ordinal.Equals(before.Task, runtime.TaskId));
            Assert.True(StringComparer.Ordinal.Equals(before.Conversation, runtime.ConversationIdentity));
            Assert.True(StringComparer.Ordinal.Equals(before.Provider, runtime.ProviderConversationIdentity));
        }
        await h.ResumeAsync();
'@
$text = Replace-Exact $text $old $new 'restart-matched-identity'
$old = @'
        var managerLineage = await h.BrowserConversationsAsync(h.ManagerAgentId);
        Assert.Single(managerLineage, x => x.State == ConversationLifecycleState.Active);
        Assert.Contains(managerLineage, x => x.ConversationId == managerPredecessor.ConversationId && x.State == ConversationLifecycleState.Archived);
'@
$new = @'
        var managerLineage = await h.BrowserConversationsAsync(h.ManagerAgentId);
        var managerSuccessor = Assert.Single(managerLineage, x => x.State == ConversationLifecycleState.Active);
        Assert.True(StringComparer.Ordinal.Equals(managerSuccessor.ConversationId, new ConversationId(Guid.Parse(managerSuccessor.ConversationId)).ToString()));
        Assert.Contains(managerLineage, x => x.ConversationId == managerPredecessor.ConversationId && x.State == ConversationLifecycleState.Archived);
'@
$text = Replace-Exact $text $old $new 'manager-rollover-canonical'
$old = @'
        var workerLineage = await h.BrowserConversationsAsync(workerOneAgent);
        Assert.Single(workerLineage, x => x.State == ConversationLifecycleState.Active);
        Assert.Contains(workerLineage, x => x.ConversationId == workerPredecessor.ConversationId && x.State == ConversationLifecycleState.Archived);
'@
$new = @'
        var workerLineage = await h.BrowserConversationsAsync(workerOneAgent);
        var workerSuccessor = Assert.Single(workerLineage, x => x.State == ConversationLifecycleState.Active);
        Assert.True(StringComparer.Ordinal.Equals(workerSuccessor.ConversationId, new ConversationId(Guid.Parse(workerSuccessor.ConversationId)).ToString()));
        Assert.Contains(workerLineage, x => x.ConversationId == workerPredecessor.ConversationId && x.State == ConversationLifecycleState.Archived);
'@
$text = Replace-Exact $text $old $new 'worker-rollover-canonical'
Write-Repo $path $text

# Permanent source-safety assertions are enforced by the product/E2E matrix; also fail this patch immediately if unsafe creation/comparison remains.
$infra = Read-Repo 'src/PCCExecutive.Infrastructure/BrowserAgentProviderAdapter.cs'
if ($infra.Contains('Guid.TryParse(runtimeIdentity')) { throw 'UNSAFE_DN_INSENSITIVE_RUNTIME_IDENTITY_REMAINS' }
if (-not $infra.Contains('StringComparer.Ordinal.Equals(runtimeIdentity, expected.ToString())')) { throw 'STRICT_CANONICAL_RUNTIME_IDENTITY_MISSING' }
$host = Read-Repo 'src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs'
if ($host.Contains('logicalConversation = StableGuid($"conversation:')) { throw 'UNSAFE_MANAGER_GUID_STRING_CREATION_REMAINS' }
$rollover = Read-Repo 'src/PCCExecutive.App/Presentation/AutonomousConversationRolloverRuntime.cs'
if ($rollover.Contains('candidateConversationId = Guid.NewGuid().ToString()')) { throw 'UNSAFE_ROLLOVER_GUID_STRING_CREATION_REMAINS' }
if (-not $rollover.Contains('var candidateConversation = ConversationId.New();')) { throw 'CANONICAL_ROLLOVER_CONVERSATION_CREATION_MISSING' }

Write-Host 'CANONICAL_CONVERSATION_IDENTITY_CLOSURE_APPLIED'
