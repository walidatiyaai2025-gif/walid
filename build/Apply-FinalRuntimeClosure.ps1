$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot

function Replace-Exact([string]$Path, [string]$Old, [string]$New) {
    $full = Join-Path $root $Path
    $text = [IO.File]::ReadAllText($full)
    if (-not $text.Contains($Old)) { throw "Expected text not found in $Path`n--- expected ---`n$Old" }
    [IO.File]::WriteAllText($full, $text.Replace($Old, $New), [Text.UTF8Encoding]::new($false))
}

# P0-001: canonical snapshot load/save always merges standalone pre-submit dispatch journal rows.
Replace-Exact 'src/PCCExecutive.Infrastructure/CrashConsistentOrchestrationStore.cs' @'
    public Task SaveAsync(OrchestrationRecoverySnapshot snapshot, CancellationToken cancellationToken = default) =>
        CommitAsync(snapshot, "ORCHESTRATION_SNAPSHOT", $"snapshot:{snapshot.ProjectRun.Id}:{snapshot.SavedAt.UtcDateTime.Ticks}", new NoCrashFaultInjector(), cancellationToken);

    public Task<OrchestrationRecoverySnapshot?> LoadAsync(ProjectRunId projectRunId, CancellationToken cancellationToken = default) =>
        new SqliteOrchestrationStateStore(_store).LoadAsync(projectRunId, cancellationToken);
'@ @'
    public async Task SaveAsync(OrchestrationRecoverySnapshot snapshot, CancellationToken cancellationToken = default)
    {
        var merged = await DispatchMergedOrchestrationStateStore.MergeAsync(_store, snapshot, cancellationToken).ConfigureAwait(false);
        await CommitAsync(merged, "ORCHESTRATION_SNAPSHOT", $"snapshot:{merged.ProjectRun.Id}:{merged.SavedAt.UtcDateTime.Ticks}", new NoCrashFaultInjector(), cancellationToken).ConfigureAwait(false);
    }

    public async Task<OrchestrationRecoverySnapshot?> LoadAsync(ProjectRunId projectRunId, CancellationToken cancellationToken = default)
    {
        var snapshot = await new SqliteOrchestrationStateStore(_store).LoadAsync(projectRunId, cancellationToken).ConfigureAwait(false);
        return snapshot is null ? null : await DispatchMergedOrchestrationStateStore.MergeAsync(_store, snapshot, cancellationToken).ConfigureAwait(false);
    }
'@

# Equivalent dispatch reuse must include task and logical conversation correlation, not just content hash.
Replace-Exact 'src/PCCExecutive.Infrastructure/AutonomousDispatchSafety.cs' @'
    public async Task<PCCExecutive.Domain.Dispatch?> FindEquivalentAsync(
        ProjectRunId projectRunId,
        LogicalAgentId logicalAgentId,
        string contentHash,
        CancellationToken cancellationToken = default)
    {
        var dispatches = await ListAsync(projectRunId, cancellationToken).ConfigureAwait(false);
        return dispatches
            .Where(x => x.LogicalAgentId == logicalAgentId && StringComparer.OrdinalIgnoreCase.Equals(x.ContentHash, contentHash))
'@ @'
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
'@

Replace-Exact 'src/PCCExecutive.Infrastructure/BrowserAgentProviderAdapter.cs' @'
        if (_durableStore is not null)
        {
            journal = new AutonomousDispatchJournal(_durableStore);
            var existing = await journal.FindEquivalentAsync(request.ProjectRunId, request.LogicalAgentId, request.ContentHash, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
'@ @'
        if (_durableStore is not null)
        {
            journal = new AutonomousDispatchJournal(_durableStore);
            var taskId = request.TaskId ?? new TaskId(StableGuid($"runtime-task:{request.ProjectRunId}:{runtime.TaskId}"));
            var waveId = request.WaveId ?? new WaveId(StableGuid($"runtime-wave:{request.ProjectRunId}:{runtime.TaskId}"));
            var existing = await journal.FindEquivalentAsync(request.ProjectRunId, request.LogicalAgentId, taskId, request.ConversationId, request.ContentHash, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
'@
Replace-Exact 'src/PCCExecutive.Infrastructure/BrowserAgentProviderAdapter.cs' @'
            else
            {
                var taskId = request.TaskId ?? new TaskId(StableGuid($"runtime-task:{request.ProjectRunId}:{runtime.TaskId}"));
                var waveId = request.WaveId ?? new WaveId(StableGuid($"runtime-wave:{request.ProjectRunId}:{runtime.TaskId}"));
                domainDispatch = new PCCExecutive.Domain.Dispatch(
'@ @'
            else
            {
                domainDispatch = new PCCExecutive.Domain.Dispatch(
'@

# P0-003: wire fresh ownership proof both before durable dispatch intent and at the final Browser submit boundary.
Replace-Exact 'src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs' @'
            var browserProvider = new PCCExecutive.Browser.BrowserChatProvider(registry, adapter, store, new WrongChatGuard(), sendGate);
            IAgentProvider agentProvider = new BrowserAgentProviderAdapter(registry, browserProvider);
'@ @'
            var browserProvider = new PCCExecutive.Browser.BrowserChatProvider(registry, adapter, store, new WrongChatGuard(), sendGate, ownership);
            IAgentProvider agentProvider = new BrowserAgentProviderAdapter(registry, browserProvider, ownership);
'@

# P0-004: never clobber durable logical-agent task/conversation bindings on restart.
Replace-Exact 'src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs' @'
    private static void PersistLogicalAgents(SqliteStateStore store, ProjectRunId runId, LogicalAgentId managerAgentId, IReadOnlyList<LogicalAgentId> workerAgentIds)
    {
        var manager = new LogicalAgentSession(managerAgentId, runId, AgentRole.Manager, null, null, null, LogicalSessionState.Ready);
        store.SaveLogicalAgentAsync(manager).GetAwaiter().GetResult();
        for (var i = 0; i < workerAgentIds.Count; i++)
        {
            var worker = new LogicalAgentSession(workerAgentIds[i], runId, AgentRole.Worker, new WorkerSlotId(i + 1), null, null, LogicalSessionState.Ready);
            store.SaveLogicalAgentAsync(worker).GetAwaiter().GetResult();
        }
    }
'@ @'
    private static void PersistLogicalAgents(SqliteStateStore store, ProjectRunId runId, LogicalAgentId managerAgentId, IReadOnlyList<LogicalAgentId> workerAgentIds)
    {
        if (store.LoadLogicalAgentAsync(managerAgentId).GetAwaiter().GetResult() is null)
            store.SaveLogicalAgentAsync(new LogicalAgentSession(managerAgentId, runId, AgentRole.Manager, null, null, null, LogicalSessionState.Ready)).GetAwaiter().GetResult();
        for (var i = 0; i < workerAgentIds.Count; i++)
        {
            var workerId = workerAgentIds[i];
            if (store.LoadLogicalAgentAsync(workerId).GetAwaiter().GetResult() is null)
                store.SaveLogicalAgentAsync(new LogicalAgentSession(workerId, runId, AgentRole.Worker, new WorkerSlotId(i + 1), null, null, LogicalSessionState.Ready)).GetAwaiter().GetResult();
        }
    }
'@

# Recovery fence/reconstruction occurs before constructor state is exposed.
Replace-Exact 'src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs' @'
            var recovered = _orchestrationStore.LoadAsync(run.Id).GetAwaiter().GetResult();
            if (recovered is not null)
'@ @'
            var startupRecovery = new DurableStartupRecoveryService(store, _orchestrationStore);
            var startupKind = startupRecovery.BeginStartupAsync(run.Id).GetAwaiter().GetResult();
            var recovered = startupRecovery.ReconstructAsync(run.Id).GetAwaiter().GetResult();
            _recovery.Insert(0, new RecoveryEventSummary(DateTimeOffset.UtcNow, startupKind.ToString(), "Durable startup recovery and dispatch-fence reconciliation completed before AutoResume.", true));
            if (recovered is not null)
'@

# Reconcile/recover durable browser sessions before AutoResume.
Replace-Exact 'src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs' @'
            gateway.RefreshLocalSnapshotAsync().GetAwaiter().GetResult();
            if (run is not null && gateway._settings.AutoResume && gateway._autopilot != "PAUSED") gateway.EnsureAutopilotLoop();
'@ @'
            if (run is not null) gateway.RecoverStartupBrowserStateAsync().GetAwaiter().GetResult();
            gateway.RefreshLocalSnapshotAsync().GetAwaiter().GetResult();
            if (run is not null && gateway._settings.AutoResume && gateway._autopilot != "PAUSED" && gateway._autopilot != "RECOVERY_REQUIRED") gateway.EnsureAutopilotLoop();
'@

# Manager send has a durable orchestration snapshot before submission, allowing standalone dispatch merge after crash.
Replace-Exact 'src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs' @'
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(prompt))).ToLowerInvariant();
        var request = new AgentRequest(run.Id, managerAgentId, new ConversationId(Guid.Parse(logicalConversation)), DispatchId.New(), prompt, hash);
'@ @'
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(prompt))).ToLowerInvariant();
        await PersistAgentBindingAsync(managerAgentId, null, null, new ConversationId(Guid.Parse(logicalConversation)), cancellationToken).ConfigureAwait(false);
        await _orchestrationStore.SaveAsync(new OrchestrationRecoverySnapshot(run, _currentWave, _runtimeTasks, _assignments, [], null, OrchestrationPhase.ManagerPlanning, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
        var request = new AgentRequest(run.Id, managerAgentId, new ConversationId(Guid.Parse(logicalConversation)), DispatchId.New(), prompt, hash);
'@

# Worker ownership proof precedes dispatch binding/conversation persistence, and durable logical bindings preserve slot/task/conversation.
Replace-Exact 'src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs' @'
            if (runtime is null)
                runtime = await _sessions.CreateAsync(new BrowserSessionRequest(run.Id.ToString(), agentId.ToString(), slot.Value.ToString(), proposal.Task.Id.ToString(), conversationId.ToString(), "NEW", BrowserVisibility.Hidden), cancellationToken).ConfigureAwait(false);
            else
                await _sessions.BindDispatchTargetAsync(runtime.RuntimeId, proposal.Task.Id.ToString(), conversationId.ToString(), runtime.ProviderConversationIdentity ?? "NEW", cancellationToken).ConfigureAwait(false);
            await _store.SaveBrowserConversationAsync(new ConversationRecord { ConversationId = conversationId.ToString(), LogicalAgentId = agentId.ToString(), ProjectRunId = run.Id.ToString(), Sequence = 1, UrlOrProviderIdentity = runtime.ProviderConversationIdentity ?? "NEW", CreatedAt = DateTimeOffset.UtcNow, State = ConversationLifecycleState.Active }, cancellationToken).ConfigureAwait(false);
            bindings.Add(new WorkerExecutionBinding(slot, agentId, conversationId));
'@ @'
            if (runtime is null)
                runtime = await _sessions.CreateAsync(new BrowserSessionRequest(run.Id.ToString(), agentId.ToString(), slot.Value.ToString(), proposal.Task.Id.ToString(), conversationId.ToString(), "NEW", BrowserVisibility.Hidden), cancellationToken).ConfigureAwait(false);
            var ownership = await _ownership.ProveAsync(runtime, cancellationToken).ConfigureAwait(false);
            if (!ownership.IsProven) throw new InvalidOperationException($"Worker {slot.Value} send refused before binding: {ownership.Reason}.");
            if (!StringComparer.Ordinal.Equals(runtime.WorkerSlotId, slot.Value.ToString())) throw new InvalidOperationException($"Worker slot correlation failed before send for slot {slot.Value}.");
            if (!StringComparer.Ordinal.Equals(runtime.TaskId, proposal.Task.Id.ToString()) || !StringComparer.Ordinal.Equals(runtime.ConversationIdentity, conversationId.ToString()))
            {
                var bound = await _sessions.BindDispatchTargetAsync(runtime.RuntimeId, proposal.Task.Id.ToString(), conversationId.ToString(), runtime.ProviderConversationIdentity ?? "NEW", cancellationToken).ConfigureAwait(false);
                if (!bound.Succeeded) throw new InvalidOperationException($"Worker {slot.Value} dispatch binding failed: {bound.Reason}.");
                runtime = bound.Runtime ?? runtime;
            }
            await PersistAgentBindingAsync(agentId, slot, proposal.Task.Id, conversationId, cancellationToken).ConfigureAwait(false);
            await _store.SaveBrowserConversationAsync(new ConversationRecord { ConversationId = conversationId.ToString(), LogicalAgentId = agentId.ToString(), ProjectRunId = run.Id.ToString(), Sequence = 1, UrlOrProviderIdentity = runtime.ProviderConversationIdentity ?? "NEW", CreatedAt = DateTimeOffset.UtcNow, State = ConversationLifecycleState.Active }, cancellationToken).ConfigureAwait(false);
            bindings.Add(new WorkerExecutionBinding(slot, agentId, conversationId));
'@

# Worker semantic reconciliation carries WorkerSlot through the final guard expectation.
Replace-Exact 'src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs' @'
            var expected = new BrowserDispatchExpectation(run.Id.ToString(), agentId.ToString(), proposal.Task.Id.ToString(), runtime.ConversationIdentity, runtime.ProviderConversationIdentity);
'@ @'
            var expected = new BrowserDispatchExpectation(run.Id.ToString(), agentId.ToString(), proposal.Task.Id.ToString(), runtime.ConversationIdentity, runtime.ProviderConversationIdentity, slot.Value.ToString());
'@

# Manager review ownership proof occurs before rebinding and any durable dispatch side effect.
Replace-Exact 'src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs' @'
        var logicalConversation = runtime.ConversationIdentity ?? throw new InvalidOperationException("Manager logical conversation is unavailable.");
        var providerConversation = runtime.ProviderConversationIdentity ?? throw new InvalidOperationException("Manager provider conversation is unavailable.");
        await _sessions.BindDispatchTargetAsync(runtime.RuntimeId, $"manager-review:{review.WaveId}", logicalConversation, providerConversation, cancellationToken).ConfigureAwait(false);
'@ @'
        var logicalConversation = runtime.ConversationIdentity ?? throw new InvalidOperationException("Manager logical conversation is unavailable.");
        var providerConversation = runtime.ProviderConversationIdentity ?? throw new InvalidOperationException("Manager provider conversation is unavailable.");
        var ownership = await _ownership.ProveAsync(runtime, cancellationToken).ConfigureAwait(false);
        if (!ownership.IsProven) throw new InvalidOperationException($"Manager review send refused before binding: {ownership.Reason}.");
        await _sessions.BindDispatchTargetAsync(runtime.RuntimeId, $"manager-review:{review.WaveId}", logicalConversation, providerConversation, cancellationToken).ConfigureAwait(false);
        await PersistAgentBindingAsync(managerAgentId, null, null, new ConversationId(Guid.Parse(logicalConversation)), cancellationToken).ConfigureAwait(false);
'@

# P0-002: Manager CLOSE only requests final verification. It never promotes 100 itself.
Replace-Exact 'src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs' @'
        if (parsed.Plan.Tasks.Count == 0)
        {
            if (string.Equals(parsed.Plan.ProjectDecision, "CLOSE", StringComparison.OrdinalIgnoreCase) && run.VerifiedCompletion.Percent >= 99m && EvidenceReadyForClosure(baselineResult.Value))
            {
                _run = run with { State = ProjectRunState.VerifiedComplete, ManagerEstimate = parsed.Plan.ManagerEstimate, VerifiedCompletion = new VerifiedCompletion(100m), CompletionMode = ProjectCompletionMode.VerifiedComplete };
                _currentPlan = parsed.Plan;
                _currentWave = _currentWave is null ? null : _currentWave with { State = WaveState.Completed };
                await _orchestrationStore.SaveAsync(new OrchestrationRecoverySnapshot(_run, _currentWave, _runtimeTasks, _assignments, [], null, OrchestrationPhase.VerifiedComplete, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
                _autopilot = "DONE";
                _latestManagerHandoff = "100% VERIFIED — Manager requested CLOSE and all current PCC/GitHub evidence gates are satisfied.";
                await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            throw new InvalidOperationException("A zero-task Manager response must request CLOSE with 99% evidence-backed completion, or identify a real blocker.");
        }
'@ @'
        if (parsed.Plan.Tasks.Count == 0)
        {
            if (string.Equals(parsed.Plan.ProjectDecision, "CLOSE", StringComparison.OrdinalIgnoreCase) && run.VerifiedCompletion.Percent >= 99m)
            {
                _run = run with { State = ProjectRunState.ClosureMode, ManagerEstimate = parsed.Plan.ManagerEstimate, VerifiedCompletion = new VerifiedCompletion(Math.Min(99m, run.VerifiedCompletion.Percent)), CompletionMode = ProjectCompletionMode.ClosureMode };
                _currentPlan = parsed.Plan;
                _currentWave = _currentWave is null ? null : _currentWave with { State = WaveState.Completed };
                await _store.SaveCheckpointAsync(new DurableCheckpoint($"final-verification-request:{run.Id}", run.Id.ToString(), "final-verification-request-v1", JsonSerializer.Serialize(new { RequestedAt = DateTimeOffset.UtcNow, ManagerEstimate = parsed.Plan.ManagerEstimate.Percent }), DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
                await _orchestrationStore.SaveAsync(new OrchestrationRecoverySnapshot(_run, _currentWave, _runtimeTasks, _assignments, [], null, OrchestrationPhase.ClosureMode, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
                _autopilot = "CLOSURE_VERIFY";
                _latestManagerHandoff = "Manager requested CLOSE. Terminal 100% remains blocked until an independent fresh PCC/GitHub evidence reconciliation passes.";
                await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            throw new InvalidOperationException("A zero-task Manager response must request CLOSE with 99% evidence-backed completion, or identify a real blocker.");
        }
'@

# Independent final-verification authority; Manager text is not an input to this promotion method.
Replace-Exact 'src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs' @'
    private static bool EvidenceReadyForClosure(ProjectBaselineSnapshot live) =>
        live.Freshness == EvidenceFreshness.Current &&
        live.KnownBlockers.Count == 0 &&
        (string.Equals(live.CiState, "success", StringComparison.OrdinalIgnoreCase) || string.Equals(live.CiState, "green", StringComparison.OrdinalIgnoreCase)) &&
        live.CanonicalTasks.Count > 0 && live.CanonicalTasks.All(x => IsTerminalCanonicalState(x.State));

    private static bool IsTerminalCanonicalState(string state) => state.ToUpperInvariant() is "DONE" or "COMPLETE" or "COMPLETED" or "VERIFIED" or "MERGED" or "CLOSED" or "ACCEPTED";
'@ @'
    private static bool EvidenceReadyForClosure(ProjectBaselineSnapshot live) =>
        live.Freshness == EvidenceFreshness.Current &&
        live.KnownBlockers.Count == 0 &&
        (string.Equals(live.CiState, "success", StringComparison.OrdinalIgnoreCase) || string.Equals(live.CiState, "green", StringComparison.OrdinalIgnoreCase)) &&
        live.CanonicalTasks.Count > 0 && live.CanonicalTasks.All(x => IsTerminalCanonicalState(x.State));

    private async Task<bool> RunIndependentFinalVerificationAsync(CancellationToken cancellationToken)
    {
        var run = RequireActiveRun();
        if (run.CompletionMode != ProjectCompletionMode.ClosureMode || run.VerifiedCompletion.Percent < 99m) return false;
        if (await _store.LoadCheckpointAsync($"final-verification-request:{run.Id}", cancellationToken).ConfigureAwait(false) is null) return false;
        var fresh = await _baseline.BuildAsync(_projectControlId ?? throw new InvalidOperationException("Selected PCC project identity is unavailable."), cancellationToken).ConfigureAwait(false);
        if (!fresh.IsSuccess || fresh.Value is null || !EvidenceReadyForClosure(fresh.Value))
        {
            _autopilot = "CLOSURE_VERIFY";
            _latestManagerHandoff = "Closure verification remains at 99% or below: fresh authoritative evidence is missing, stale, blocked, or non-green.";
            return false;
        }
        _run = run with { State = ProjectRunState.VerifiedComplete, VerifiedCompletion = new VerifiedCompletion(100m), CompletionMode = ProjectCompletionMode.VerifiedComplete };
        await _orchestrationStore.SaveAsync(new OrchestrationRecoverySnapshot(_run, _currentWave, _runtimeTasks, _assignments, [], null, OrchestrationPhase.VerifiedComplete, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
        _autopilot = "DONE";
        _latestManagerHandoff = "100% VERIFIED — independent fresh PCC/GitHub/test evidence reconciliation passed all terminal gates.";
        return true;
    }

    private static bool IsTerminalCanonicalState(string state) => state.ToUpperInvariant() is "DONE" or "COMPLETE" or "COMPLETED" or "VERIFIED" or "MERGED" or "CLOSED" or "ACCEPTED";
'@

# Autopilot performs independent closure verification on a later cycle.
Replace-Exact 'src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs' @'
                    else if (_autopilot is "PLANNING" or "MANAGER_REVIEW")
                        await ReconcileManagerResponseAsync(cancellationToken).ConfigureAwait(false);
                    if (_settings.DispatchMode == DispatchMode.AutomaticStaged.ToString() && _currentWave?.State == WaveState.Ready && _currentPlan is not null)
'@ @'
                    else if (_autopilot is "PLANNING" or "MANAGER_REVIEW")
                        await ReconcileManagerResponseAsync(cancellationToken).ConfigureAwait(false);
                    else if (_autopilot == "CLOSURE_VERIFY")
                        await RunIndependentFinalVerificationAsync(cancellationToken).ConfigureAwait(false);
                    if (_settings.DispatchMode == DispatchMode.AutomaticStaged.ToString() && _currentWave?.State == WaveState.Ready && _currentPlan is not null)
'@

# Helpers for binding preservation and startup Browser/session lineage reconciliation.
Replace-Exact 'src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs' @'
    private ProjectRun RequireActiveRun() =>
        _run ?? throw new InvalidOperationException("Select and resolve a project before using project runtime controls.");

    private static PccExecutiveSettings ParseSettings(string? target, PccExecutiveSettings current)
'@ @'
    private ProjectRun RequireActiveRun() =>
        _run ?? throw new InvalidOperationException("Select and resolve a project before using project runtime controls.");

    private async Task PersistAgentBindingAsync(LogicalAgentId agentId, WorkerSlotId? slot, TaskId? taskId, ConversationId conversationId, CancellationToken cancellationToken)
    {
        var run = RequireActiveRun();
        var role = slot is null ? AgentRole.Manager : AgentRole.Worker;
        var existing = await _store.LoadLogicalAgentAsync(agentId, cancellationToken).ConfigureAwait(false);
        var session = existing is null
            ? new LogicalAgentSession(agentId, run.Id, role, slot, taskId, conversationId, LogicalSessionState.Active)
            : existing with { WorkerSlotId = slot ?? existing.WorkerSlotId, CurrentTaskId = taskId ?? existing.CurrentTaskId, CurrentConversationId = conversationId, State = LogicalSessionState.Active };
        await _store.SaveLogicalAgentAsync(session, cancellationToken).ConfigureAwait(false);
    }

    private async Task RecoverStartupBrowserStateAsync(CancellationToken cancellationToken = default)
    {
        if (_run is null) return;
        var orphans = await _sessions.DetectOrphansAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        foreach (var orphan in orphans.Where(x => StringComparer.Ordinal.Equals(x.ProjectRunId, _run.Id.ToString())))
        {
            var recovered = await _sessions.RecoverOrphanAsync(orphan.RuntimeId, cancellationToken).ConfigureAwait(false);
            _recovery.Insert(0, new RecoveryEventSummary(DateTimeOffset.UtcNow, recovered.Succeeded ? "RECOVERED" : "RECOVERY_REQUIRED", $"{orphan.RuntimeId}: {recovered.Reason}", recovered.Succeeded));
            if (!recovered.Succeeded) _autopilot = "RECOVERY_REQUIRED";
        }
        var runtimes = await _runtimeRegistry.ListAsync(cancellationToken).ConfigureAwait(false);
        var reconciler = new BrowserSessionReconciliationService();
        foreach (var agentId in new[] { _managerAgentId!.Value }.Concat(_workerAgentIds))
        {
            var session = await _store.LoadLogicalAgentAsync(agentId, cancellationToken).ConfigureAwait(false);
            if (session is null) continue;
            var runtime = runtimes.FirstOrDefault(x => !x.IsArchived && StringComparer.Ordinal.Equals(x.ProjectRunId, _run.Id.ToString()) && StringComparer.Ordinal.Equals(x.LogicalAgentId, agentId.ToString()));
            var result = reconciler.Reconcile(session, runtime);
            if (result.Outcome is BrowserReconciliationKind.IDENTITY_MISMATCH or BrowserReconciliationKind.UNKNOWN || (result.Outcome == BrowserReconciliationKind.MISSING_RUNTIME && session.CurrentConversationId is not null))
            {
                await _newSendPause.PauseNewSendsAsync($"STARTUP_BROWSER_RECONCILIATION:{result.Reason}", cancellationToken).ConfigureAwait(false);
                _autopilot = "RECOVERY_REQUIRED";
                _recovery.Insert(0, new RecoveryEventSummary(DateTimeOffset.UtcNow, "RECOVERY_REQUIRED", result.Reason, false));
            }
        }
    }

    private static PccExecutiveSettings ParseSettings(string? target, PccExecutiveSettings current)
'@

# Safe normal shutdown: pause new sends, persist merged snapshot/checkpoint, WAL flush, clean marker.
Replace-Exact 'src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs' @'
    public async ValueTask DisposeAsync()
    {
        _autopilotCancellation.Cancel();
        if (_autopilotTask is not null)
        {
            try { await _autopilotTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _autopilotOperation.Dispose();
        _autopilotCancellation.Dispose();
        _pccHttp.Dispose();
        _githubHttp.Dispose();
        await _store.DisposeAsync().ConfigureAwait(false);
        _projectLock?.Dispose();
    }
'@ @'
    public async ValueTask DisposeAsync()
    {
        _autopilotCancellation.Cancel();
        if (_autopilotTask is not null)
        {
            try { await _autopilotTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        if (_run is not null)
        {
            var durable = await _orchestrationStore.LoadAsync(_run.Id).ConfigureAwait(false);
            var snapshot = new OrchestrationRecoverySnapshot(
                _run,
                _currentWave,
                _runtimeTasks,
                _assignments,
                durable?.Dispatches ?? Array.Empty<PCCExecutive.Domain.Dispatch>(),
                durable?.ManagerReview,
                CurrentOrchestrationPhase(),
                DateTimeOffset.UtcNow);
            var startup = new DurableStartupRecoveryService(_store, _orchestrationStore);
            var shutdown = new SafeShutdownCoordinator(_newSendPause, new RecoveryCheckpointService(_store), startup, _orchestrationStore, _store);
            await shutdown.ShutdownAsync(snapshot, "0.1.0").ConfigureAwait(false);
        }
        _autopilotOperation.Dispose();
        _autopilotCancellation.Dispose();
        _pccHttp.Dispose();
        _githubHttp.Dispose();
        await _store.DisposeAsync().ConfigureAwait(false);
        _projectLock?.Dispose();
    }

    private OrchestrationPhase CurrentOrchestrationPhase() => _run?.State switch
    {
        ProjectRunState.ManagerPlanning => OrchestrationPhase.ManagerPlanning,
        ProjectRunState.WaveReady => OrchestrationPhase.WaveValidation,
        ProjectRunState.Dispatching => OrchestrationPhase.Dispatching,
        ProjectRunState.WaveRunning => OrchestrationPhase.WaveRunning,
        ProjectRunState.Reconciling => OrchestrationPhase.Reconciling,
        ProjectRunState.ManagerReview => OrchestrationPhase.ManagerReview,
        ProjectRunState.ClosureMode => OrchestrationPhase.ClosureMode,
        ProjectRunState.VerifiedComplete => OrchestrationPhase.VerifiedComplete,
        ProjectRunState.BlockedExternal => OrchestrationPhase.BlockedExternal,
        ProjectRunState.StalledAutoStopped => OrchestrationPhase.StalledAutoStopped,
        ProjectRunState.StoppedByOperator => OrchestrationPhase.StoppedByOperator,
        _ => OrchestrationPhase.Initializing
    };
'@

Write-Host 'Production P0 runtime closure patch applied.'
