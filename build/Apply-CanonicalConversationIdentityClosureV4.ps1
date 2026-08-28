$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
function ReadRepo([string]$relative) { return [IO.File]::ReadAllText((Join-Path $root $relative)) }
function WriteRepo([string]$relative,[string]$text) { [IO.File]::WriteAllText((Join-Path $root $relative),$text,[Text.UTF8Encoding]::new($false)) }
function ReplaceOnce([string]$text,[string]$old,[string]$new,[string]$label) {
  $i=$text.IndexOf($old,[StringComparison]::Ordinal); if($i -lt 0){throw "ANCHOR_NOT_FOUND_${label}"}
  return $text.Substring(0,$i)+$new+$text.Substring($i+$old.Length)
}
function ReplaceRegexOnce([string]$text,[string]$pattern,[string]$new,[string]$label) {
  $r=[regex]::new($pattern,[Text.RegularExpressions.RegexOptions]::Singleline); $m=$r.Matches($text)
  if($m.Count -ne 1){throw "REGEX_ANCHOR_${label}_COUNT_$($m.Count)"}
  return $text.Substring(0,$m[0].Index)+$new+$text.Substring($m[0].Index+$m[0].Length)
}

# Strict conversation correlation at the provider adapter.
$p='src/PCCExecutive.Infrastructure/BrowserAgentProviderAdapter.cs'; $t=ReadRepo $p
$t=ReplaceRegexOnce $t '    private static bool SameConversationIdentity\(string runtimeIdentity, ConversationId expected\)\s*\{\s*if \(!Guid\.TryParse\(runtimeIdentity, out var runtimeGuid\)\) return false;\s*return runtimeGuid == expected\.Value;\s*\}' @'
    private static bool SameConversationIdentity(string runtimeIdentity, ConversationId expected) =>
        StringComparer.Ordinal.Equals(runtimeIdentity, expected.ToString());
'@ 'STRICT_CONVERSATION'
WriteRepo $p $t

# Manager first send and review use canonical ConversationId.ToString() at source boundaries.
$p='src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs'; $t=ReadRepo $p
$t=ReplaceOnce $t '            logicalConversation = StableGuid($"conversation:{run.Id}:manager:1").ToString();' @'
            var createdConversation = new ConversationId(StableGuid($"conversation:{run.Id}:manager:1"));
            logicalConversation = createdConversation.ToString();
'@ 'MANAGER_CREATE'
$t=ReplaceOnce $t @'
        }

        var baseline = await _baseline.BuildAsync(_projectControlId ?? throw new InvalidOperationException("Selected PCC project identity is unavailable."), cancellationToken).ConfigureAwait(false);
'@ @'
        }
        var managerConversation = new ConversationId(Guid.Parse(logicalConversation));
        if (!StringComparer.Ordinal.Equals(logicalConversation, managerConversation.ToString()))
            throw new InvalidOperationException("NON_CANONICAL_LOGICAL_CONVERSATION_IDENTITY: Manager runtime conversation must equal ConversationId.ToString().");

        var baseline = await _baseline.BuildAsync(_projectControlId ?? throw new InvalidOperationException("Selected PCC project identity is unavailable."), cancellationToken).ConfigureAwait(false);
'@ 'MANAGER_VALIDATE'
$t=ReplaceOnce $t '        await PersistAgentBindingAsync(managerAgentId, null, null, new ConversationId(Guid.Parse(logicalConversation)), cancellationToken).ConfigureAwait(false);' '        await PersistAgentBindingAsync(managerAgentId, null, null, managerConversation, cancellationToken).ConfigureAwait(false);' 'MANAGER_BIND'
$t=ReplaceOnce $t @'
        runtime = await _runtimeRegistry.GetAsync(runtime.RuntimeId, cancellationToken).ConfigureAwait(false) ?? runtime;
        var managerConversation = new ConversationId(Guid.Parse(logicalConversation));
'@ @'
        runtime = await _runtimeRegistry.GetAsync(runtime.RuntimeId, cancellationToken).ConfigureAwait(false) ?? runtime;
'@ 'MANAGER_DUPLICATE_PARSE'
$t=ReplaceOnce $t @'
        var logicalConversation = runtime.ConversationIdentity ?? throw new InvalidOperationException("Manager logical conversation is unavailable.");
        var providerConversation = runtime.ProviderConversationIdentity ?? throw new InvalidOperationException("Manager provider conversation is unavailable.");
'@ @'
        var logicalConversation = runtime.ConversationIdentity ?? throw new InvalidOperationException("Manager logical conversation is unavailable.");
        var managerReviewConversation = new ConversationId(Guid.Parse(logicalConversation));
        if (!StringComparer.Ordinal.Equals(logicalConversation, managerReviewConversation.ToString()))
            throw new InvalidOperationException("NON_CANONICAL_LOGICAL_CONVERSATION_IDENTITY: Manager review runtime conversation must equal ConversationId.ToString().");
        var providerConversation = runtime.ProviderConversationIdentity ?? throw new InvalidOperationException("Manager provider conversation is unavailable.");
'@ 'MANAGER_REVIEW_VALIDATE'
$t=ReplaceOnce $t '        await PersistAgentBindingAsync(managerAgentId, null, null, new ConversationId(Guid.Parse(logicalConversation)), cancellationToken).ConfigureAwait(false);' '        await PersistAgentBindingAsync(managerAgentId, null, null, managerReviewConversation, cancellationToken).ConfigureAwait(false);' 'MANAGER_REVIEW_BIND'
WriteRepo $p $t

# Rollover successor is a domain ConversationId first; Worker continuation preserves the current Task and Wave.
$p='src/PCCExecutive.App/Presentation/AutonomousConversationRolloverRuntime.cs'; $t=ReadRepo $p
$t=ReplaceOnce $t '        var candidateConversationId = Guid.NewGuid().ToString();' @'
        var candidateConversation = ConversationId.New();
        var candidateConversationId = candidateConversation.ToString();
'@ 'ROLLOVER_CREATE'
$t=ReplaceRegexOnce $t '        var providerIdentity = candidateRuntime\.ProviderConversationIdentity \?\? "NEW";.*?        var request = new PCCExecutive\.Application\.AgentRequest\([^\r\n]+\);' @'
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
'@ 'ROLLOVER_CORRELATION'
if(-not $t.Contains('internal static extern ref Wave? CurrentWave(PccExecutiveRuntimeHost host);')) {
$t=ReplaceOnce $t @'
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_run")]
    internal static extern ref ProjectRun? Run(PccExecutiveRuntimeHost host);
'@ @'
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_run")]
    internal static extern ref ProjectRun? Run(PccExecutiveRuntimeHost host);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_currentWave")]
    internal static extern ref Wave? CurrentWave(PccExecutiveRuntimeHost host);
'@ 'CURRENT_WAVE_ACCESS'
}
WriteRepo $p $t

# Hostile same-GUID/noncanonical-D runtime identity must remain blocked.
$p='tests/PCCExecutive.E2E/ProductionRuntimeSecurityNegativeTests.cs'; $t=ReadRepo $p
$t=ReplaceOnce $t @'
        var mismatchedConversation = ConversationId.New();
        await ((IBrowserRuntimeRegistry)h.Store).UpsertAsync(managerRuntime with { ConversationIdentity = mismatchedConversation.ToString() });
'@ @'
        var nonCanonicalManagerConversation = correctManagerConversation.Value.ToString("D");
        Assert.False(StringComparer.Ordinal.Equals(correctManagerConversation.ToString(), nonCanonicalManagerConversation));
        await ((IBrowserRuntimeRegistry)h.Store).UpsertAsync(managerRuntime with { ConversationIdentity = nonCanonicalManagerConversation });
'@ 'IDENTITY_TAMPER'
WriteRepo $p $t

# Ordered success path proves canonical identities before/after restart and rollover.
$p='tests/PCCExecutive.E2E/ProductionRuntime32StageAcceptanceTests.cs'; $t=ReadRepo $p
$t=ReplaceOnce $t @'
        var managerDispatches = await new AutonomousDispatchJournal(h.Store).ListAsync(runId);
        Assert.Single(managerDispatches, x => x.LogicalAgentId == h.ManagerAgentId);
        Assert.Equal(1, h.Adapter.EnterCount(managerRuntime.RuntimeId));
'@ @'
        var managerDispatches = await new AutonomousDispatchJournal(h.Store).ListAsync(runId);
        var managerDispatch = Assert.Single(managerDispatches, x => x.LogicalAgentId == h.ManagerAgentId);
        var managerLogical = await h.Store.LoadLogicalAgentAsync(h.ManagerAgentId);
        var managerConversationRecord = await h.ActiveBrowserConversationAsync(h.ManagerAgentId);
        Assert.NotNull(managerLogical?.CurrentConversationId);
        Assert.True(StringComparer.Ordinal.Equals(managerRuntime.ConversationIdentity, managerLogical!.CurrentConversationId!.Value.ToString()));
        Assert.True(StringComparer.Ordinal.Equals(managerRuntime.ConversationIdentity, managerConversationRecord.ConversationId));
        Assert.True(StringComparer.Ordinal.Equals(managerRuntime.ConversationIdentity, managerDispatch.ConversationId.ToString()));
        Assert.Equal(1, h.Adapter.EnterCount(managerRuntime.RuntimeId));
'@ 'MANAGER_IDENTITY_PROOF'
$t=ReplaceOnce $t @'
            Assert.Equal(slotNumber.ToString(), runtime.WorkerSlotId);
            Assert.Equal(expectedTask.ToString(), runtime.TaskId);
            Assert.Equal(1, h.Adapter.EnterCount(runtime.RuntimeId));
'@ @'
            Assert.Equal(slotNumber.ToString(), runtime.WorkerSlotId);
            Assert.Equal(expectedTask.ToString(), runtime.TaskId);
            var workerLogical = await h.Store.LoadLogicalAgentAsync(workerIds[slotNumber - 1]);
            var workerConversationRecord = await h.ActiveBrowserConversationAsync(workerIds[slotNumber - 1]);
            Assert.NotNull(workerLogical?.CurrentConversationId);
            Assert.True(StringComparer.Ordinal.Equals(runtime.ConversationIdentity, workerLogical!.CurrentConversationId!.Value.ToString()));
            Assert.True(StringComparer.Ordinal.Equals(runtime.ConversationIdentity, workerConversationRecord.ConversationId));
            Assert.Equal(1, h.Adapter.EnterCount(runtime.RuntimeId));
'@ 'WORKER_IDENTITY_PROOF'
$t=ReplaceOnce $t @'
        var preRestartAssignment = h.Assignments.Single();
        var preRestartAgents = new[] { h.ManagerAgentId }.Concat(h.WorkerAgentIds).ToArray();
        await h.PauseAsync();
'@ @'
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
'@ 'RESTART_CAPTURE'
$t=ReplaceOnce $t @'
        foreach (var agent in preRestartAgents)
            Assert.NotNull(await h.Store.LoadLogicalAgentAsync(agent));
        await h.ResumeAsync();
'@ @'
        foreach (var agent in preRestartAgents)
        {
            var logical = await h.Store.LoadLogicalAgentAsync(agent);
            var runtime = await h.RuntimeForAsync(agent);
            Assert.NotNull(logical);
            var reconciliation = new BrowserSessionReconciliationService().Reconcile(logical!, runtime);
            Assert.Equal(BrowserSessionReconciliationOutcome.Matched, reconciliation.Outcome);
            var before = preRestartIdentity[agent];
            Assert.True(StringComparer.Ordinal.Equals(before.Slot, runtime.WorkerSlotId));
            Assert.True(StringComparer.Ordinal.Equals(before.Task, runtime.TaskId));
            Assert.True(StringComparer.Ordinal.Equals(before.Conversation, runtime.ConversationIdentity));
            Assert.True(StringComparer.Ordinal.Equals(before.Provider, runtime.ProviderConversationIdentity));
        }
        await h.ResumeAsync();
'@ 'RESTART_MATCH'
$t=ReplaceOnce $t @'
        var managerLineage = await h.BrowserConversationsAsync(h.ManagerAgentId);
        Assert.Single(managerLineage, x => x.State == ConversationLifecycleState.Active);
'@ @'
        var managerLineage = await h.BrowserConversationsAsync(h.ManagerAgentId);
        var managerSuccessor = Assert.Single(managerLineage, x => x.State == ConversationLifecycleState.Active);
        Assert.True(StringComparer.Ordinal.Equals(managerSuccessor.ConversationId, new ConversationId(Guid.Parse(managerSuccessor.ConversationId)).ToString()));
'@ 'MANAGER_ROLLOVER_PROOF'
$t=ReplaceOnce $t @'
        var workerLineage = await h.BrowserConversationsAsync(workerOneAgent);
        Assert.Single(workerLineage, x => x.State == ConversationLifecycleState.Active);
'@ @'
        var workerLineage = await h.BrowserConversationsAsync(workerOneAgent);
        var workerSuccessor = Assert.Single(workerLineage, x => x.State == ConversationLifecycleState.Active);
        Assert.True(StringComparer.Ordinal.Equals(workerSuccessor.ConversationId, new ConversationId(Guid.Parse(workerSuccessor.ConversationId)).ToString()));
'@ 'WORKER_ROLLOVER_PROOF'
WriteRepo $p $t

$i=ReadRepo 'src/PCCExecutive.Infrastructure/BrowserAgentProviderAdapter.cs'; if($i.Contains('Guid.TryParse(runtimeIdentity')){throw 'D_N_TOLERANCE_REMAINS'}
$h=ReadRepo 'src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs'; if($h.Contains('logicalConversation = StableGuid($"conversation:')){throw 'MANAGER_D_FORMAT_CREATION_REMAINS'}
$r=ReadRepo 'src/PCCExecutive.App/Presentation/AutonomousConversationRolloverRuntime.cs'; if($r.Contains('candidateConversationId = Guid.NewGuid().ToString()')){throw 'ROLLOVER_D_FORMAT_CREATION_REMAINS'}
Write-Host 'CANONICAL_CONVERSATION_IDENTITY_CLOSURE_V4_APPLIED'
