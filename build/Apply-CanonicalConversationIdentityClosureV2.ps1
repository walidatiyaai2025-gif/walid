$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
function R([string]$p) { [IO.File]::ReadAllText((Join-Path $root $p)) }
function W([string]$p,[string]$t) { [IO.File]::WriteAllText((Join-Path $root $p),$t,[Text.UTF8Encoding]::new($false)) }
function X([string]$t,[string]$o,[string]$n,[string]$l) { if(-not $t.Contains($o)){throw "ANCHOR:$l"}; $t.Replace($o,$n) }
function RX([string]$t,[string]$p,[string]$n,[string]$l) { $r=[regex]::new($p,[Text.RegularExpressions.RegexOptions]::Singleline); $m=$r.Matches($t); if($m.Count -ne 1){throw "REGEX_ANCHOR:$l:$($m.Count)"}; $r.Replace($t,[Text.RegularExpressions.MatchEvaluator]{param($x) $n},1) }

# Strict canonical runtime lookup: never tolerate D/N formatting differences.
$p='src/PCCExecutive.Infrastructure/BrowserAgentProviderAdapter.cs'; $t=R $p
$t=RX $t '    private static bool SameConversationIdentity\(string runtimeIdentity, ConversationId expected\)\s*\{\s*if \(!Guid\.TryParse\(runtimeIdentity, out var runtimeGuid\)\) return false;\s*return runtimeGuid == expected\.Value;\s*\}' @'
    private static bool SameConversationIdentity(string runtimeIdentity, ConversationId expected) =>
        StringComparer.Ordinal.Equals(runtimeIdentity, expected.ToString());
'@ 'strict-runtime-conversation'; W $p $t

# Manager first/review send: domain ConversationId first, then its canonical string.
$p='src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs'; $t=R $p
$t=X $t '            logicalConversation = StableGuid($"conversation:{run.Id}:manager:1").ToString();' @'
            var createdConversation = new ConversationId(StableGuid($"conversation:{run.Id}:manager:1"));
            logicalConversation = createdConversation.ToString();
'@ 'manager-create'
$t=X $t @'
        }

        var baseline = await _baseline.BuildAsync(_projectControlId ?? throw new InvalidOperationException("Selected PCC project identity is unavailable."), cancellationToken).ConfigureAwait(false);
'@ @'
        }
        var managerConversation = new ConversationId(Guid.Parse(logicalConversation));
        if (!StringComparer.Ordinal.Equals(logicalConversation, managerConversation.ToString()))
            throw new InvalidOperationException("NON_CANONICAL_LOGICAL_CONVERSATION_IDENTITY: Manager runtime conversation must equal ConversationId.ToString().");

        var baseline = await _baseline.BuildAsync(_projectControlId ?? throw new InvalidOperationException("Selected PCC project identity is unavailable."), cancellationToken).ConfigureAwait(false);
'@ 'manager-validate'
$t=X $t '        await PersistAgentBindingAsync(managerAgentId, null, null, new ConversationId(Guid.Parse(logicalConversation)), cancellationToken).ConfigureAwait(false);' '        await PersistAgentBindingAsync(managerAgentId, null, null, managerConversation, cancellationToken).ConfigureAwait(false);' 'manager-first-persist'
$t=X $t '        var managerConversation = new ConversationId(Guid.Parse(logicalConversation));' '' 'manager-duplicate-remove'
$t=X $t @'
        var logicalConversation = runtime.ConversationIdentity ?? throw new InvalidOperationException("Manager logical conversation is unavailable.");
        var providerConversation = runtime.ProviderConversationIdentity ?? throw new InvalidOperationException("Manager provider conversation is unavailable.");
'@ @'
        var logicalConversation = runtime.ConversationIdentity ?? throw new InvalidOperationException("Manager logical conversation is unavailable.");
        var managerReviewConversation = new ConversationId(Guid.Parse(logicalConversation));
        if (!StringComparer.Ordinal.Equals(logicalConversation, managerReviewConversation.ToString()))
            throw new InvalidOperationException("NON_CANONICAL_LOGICAL_CONVERSATION_IDENTITY: Manager review runtime conversation must equal ConversationId.ToString().");
        var providerConversation = runtime.ProviderConversationIdentity ?? throw new InvalidOperationException("Manager provider conversation is unavailable.");
'@ 'manager-review-validate'
# Replace the remaining review parse after first-send replacement.
$t=X $t '        await PersistAgentBindingAsync(managerAgentId, null, null, new ConversationId(Guid.Parse(logicalConversation)), cancellationToken).ConfigureAwait(false);' '        await PersistAgentBindingAsync(managerAgentId, null, null, managerReviewConversation, cancellationToken).ConfigureAwait(false);' 'manager-review-persist'
W $p $t

# Rollover: canonical ConversationId at creation and preserve worker Task/Wave identity.
$p='src/PCCExecutive.App/Presentation/AutonomousConversationRolloverRuntime.cs'; $t=R $p
$t=X $t '        var candidateConversationId = Guid.NewGuid().ToString();' @'
        var candidateConversation = ConversationId.New();
        var candidateConversationId = candidateConversation.ToString();
'@ 'rollover-create'
$pattern='        var providerIdentity = candidateRuntime\.ProviderConversationIdentity \?\? "NEW";.*?        var request = new PCCExecutive\.Application\.AgentRequest\([^\r\n]+\);'
$replacement=@'
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
$t=RX $t $pattern $replacement 'rollover-correlation'
if(-not $t.Contains('internal static extern ref Wave? CurrentWave(PccExecutiveRuntimeHost host);')){
$t=X $t @'
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_run")]
    internal static extern ref ProjectRun? Run(PccExecutiveRuntimeHost host);
'@ @'
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_run")]
    internal static extern ref ProjectRun? Run(PccExecutiveRuntimeHost host);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_currentWave")]
    internal static extern ref Wave? CurrentWave(PccExecutiveRuntimeHost host);
'@ 'wave-accessor'
}
W $p $t

# Hostile negative: same GUID with deliberately D-formatted runtime string must remain an identity mismatch.
$p='tests/PCCExecutive.E2E/ProductionRuntimeSecurityNegativeTests.cs'; $t=R $p
$t=X $t @'
        var mismatchedConversation = ConversationId.New();
        await ((IBrowserRuntimeRegistry)h.Store).UpsertAsync(managerRuntime with { ConversationIdentity = mismatchedConversation.ToString() });
'@ @'
        var nonCanonicalManagerConversation = correctManagerConversation.Value.ToString("D");
        Assert.False(StringComparer.Ordinal.Equals(correctManagerConversation.ToString(), nonCanonicalManagerConversation));
        await ((IBrowserRuntimeRegistry)h.Store).UpsertAsync(managerRuntime with { ConversationIdentity = nonCanonicalManagerConversation });
'@ 'hostile-d-format'
W $p $t

# Ordered success-path identity proofs.
$p='tests/PCCExecutive.E2E/ProductionRuntime32StageAcceptanceTests.cs'; $t=R $p
$t=X $t @'
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
'@ 'manager-identity-proof'
$t=X $t @'
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
'@ 'worker-identity-proof'
$t=X $t @'
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
'@ 'restart-capture'
$t=X $t @'
        foreach (var agent in preRestartAgents)
            Assert.NotNull(await h.Store.LoadLogicalAgentAsync(agent));
        await h.ResumeAsync();
'@ @'
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
'@ 'restart-match'
$t=X $t @'
        var managerLineage = await h.BrowserConversationsAsync(h.ManagerAgentId);
        Assert.Single(managerLineage, x => x.State == ConversationLifecycleState.Active);
'@ @'
        var managerLineage = await h.BrowserConversationsAsync(h.ManagerAgentId);
        var managerSuccessor = Assert.Single(managerLineage, x => x.State == ConversationLifecycleState.Active);
        Assert.True(StringComparer.Ordinal.Equals(managerSuccessor.ConversationId, new ConversationId(Guid.Parse(managerSuccessor.ConversationId)).ToString()));
'@ 'manager-rollover-proof'
$t=X $t @'
        var workerLineage = await h.BrowserConversationsAsync(workerOneAgent);
        Assert.Single(workerLineage, x => x.State == ConversationLifecycleState.Active);
'@ @'
        var workerLineage = await h.BrowserConversationsAsync(workerOneAgent);
        var workerSuccessor = Assert.Single(workerLineage, x => x.State == ConversationLifecycleState.Active);
        Assert.True(StringComparer.Ordinal.Equals(workerSuccessor.ConversationId, new ConversationId(Guid.Parse(workerSuccessor.ConversationId)).ToString()));
'@ 'worker-rollover-proof'
W $p $t

# Fail closed if any formatting workaround/source-of-truth bug remains.
$i=R 'src/PCCExecutive.Infrastructure/BrowserAgentProviderAdapter.cs'; if($i.Contains('Guid.TryParse(runtimeIdentity')){throw 'D_N_TOLERANCE_REMAINS'}
$h=R 'src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs'; if($h.Contains('logicalConversation = StableGuid($"conversation:')){throw 'MANAGER_D_FORMAT_CREATION_REMAINS'}
$r=R 'src/PCCExecutive.App/Presentation/AutonomousConversationRolloverRuntime.cs'; if($r.Contains('candidateConversationId = Guid.NewGuid().ToString()')){throw 'ROLLOVER_D_FORMAT_CREATION_REMAINS'}
Write-Host 'CANONICAL_CONVERSATION_IDENTITY_CLOSURE_V2_APPLIED'
