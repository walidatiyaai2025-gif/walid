$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot

function Replace-Exact([string]$Path, [string]$Old, [string]$New) {
    $full = Join-Path $root $Path
    $text = [IO.File]::ReadAllText($full)
    if (-not $text.Contains($Old)) { throw "Expected text not found in $Path`n--- expected ---`n$Old" }
    [IO.File]::WriteAllText($full, $text.Replace($Old, $New), [Text.UTF8Encoding]::new($false))
}

$hostFile = 'src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs'

# Durable global-health and loop state fields.
Replace-Exact $hostFile @'
    private readonly Queue<string> _recentPlanFingerprints = new();
    private readonly Queue<decimal> _recentVerifiedCompletion = new();
    private IReadOnlyList<ConversationHistorySummary> _conversationHistory = [];
'@ @'
    private readonly Queue<string> _recentPlanFingerprints = new();
    private readonly Queue<decimal> _recentVerifiedCompletion = new();
    private string? _runtimeHealthFault;
    private string? _runtimeErrorFingerprint;
    private int _runtimeErrorCount;
    private IReadOnlyList<ConversationHistorySummary> _conversationHistory = [];
'@

# Restore runtime health and loop guard before AutoResume.
Replace-Exact $hostFile @'
            if (pause is not null && pause.Payload.Contains("\"paused\":true", StringComparison.Ordinal))
            {
                _autopilot = "PAUSED";
                _newSendPause.PauseNewSendsAsync("Restored persisted operator pause.").GetAwaiter().GetResult();
            }
        }
        Snapshot = BuildSnapshot(Array.Empty<BrowserRuntimeRecord>(), new HashSet<string>(StringComparer.Ordinal));
'@ @'
            if (pause is not null && pause.Payload.Contains("\"paused\":true", StringComparison.Ordinal))
            {
                _autopilot = "PAUSED";
                _newSendPause.PauseNewSendsAsync("Restored persisted operator pause.").GetAwaiter().GetResult();
            }
            var healthCheckpoint = store.LoadCheckpointAsync($"runtime-health:{run.Id}").GetAwaiter().GetResult();
            if (healthCheckpoint is not null)
            {
                var health = JsonSerializer.Deserialize<DurableRuntimeHealth>(healthCheckpoint.Payload);
                if (health?.Active == true)
                {
                    _runtimeHealthFault = health.State;
                    var now = DateTimeOffset.UtcNow;
                    var cooldown = health.ResumeNotBefore is null ? (TimeSpan?)null : health.ResumeNotBefore <= now ? TimeSpan.Zero : health.ResumeNotBefore.Value - now;
                    _sendGate.Apply(new ResilienceDecision(ParseResilienceState(health.State), FaultScope.Global, true, health.RequiresHumanAction, health.Reason), now, cooldown);
                    if (_autopilot != "PAUSED") _autopilot = health.State;
                }
            }
            var loopCheckpoint = store.LoadCheckpointAsync($"loop-guard:{run.Id}").GetAwaiter().GetResult();
            if (loopCheckpoint is not null)
            {
                var loop = JsonSerializer.Deserialize<DurableLoopGuard>(loopCheckpoint.Payload);
                if (loop is not null)
                {
                    foreach (var fingerprint in loop.PlanFingerprints.TakeLast(3)) _recentPlanFingerprints.Enqueue(fingerprint);
                    foreach (var completion in loop.VerifiedCompletion.TakeLast(3)) _recentVerifiedCompletion.Enqueue(completion);
                    _runtimeErrorFingerprint = loop.RuntimeErrorFingerprint;
                    _runtimeErrorCount = loop.RuntimeErrorCount;
                    if (loop.AutoStopped)
                    {
                        _run = _run is null ? null : _run with { State = ProjectRunState.StalledAutoStopped };
                        _autopilot = "STALLED";
                    }
                }
            }
        }
        Snapshot = BuildSnapshot(Array.Empty<BrowserRuntimeRecord>(), new HashSet<string>(StringComparer.Ordinal));
'@

# Operator Resume cannot override a still-active semantic global fault.
Replace-Exact $hostFile @'
            case UiAction.ResumeAi:
                var resumedRun = RequireActiveRun();
                await _newSendPause.ResumeNewSendsAsync("Operator resumed AI from PCC Executive.", cancellationToken).ConfigureAwait(false);
                await _store.SaveCheckpointAsync(new DurableCheckpoint($"autopilot-pause:{resumedRun.Id}", resumedRun.Id.ToString(), "autopilot-pause-v1", "{\"paused\":false}", DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
                await _store.SaveCheckpointAsync(new DurableCheckpoint($"dispatch-pause:{resumedRun.Id}", resumedRun.Id.ToString(), "dispatch-pause-v1", "{\"paused\":false}", DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
                _autopilot = "READY";
                await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
                break;
'@ @'
            case UiAction.ResumeAi:
                var resumedRun = RequireActiveRun();
                if (_runtimeHealthFault is not null && !await TryResumeAfterFreshSemanticHealthAsync(cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException("Global Browser sends remain blocked because fresh semantic health is not proven safe.");
                await _newSendPause.ResumeNewSendsAsync("Operator resumed AI from PCC Executive.", cancellationToken).ConfigureAwait(false);
                await _store.SaveCheckpointAsync(new DurableCheckpoint($"autopilot-pause:{resumedRun.Id}", resumedRun.Id.ToString(), "autopilot-pause-v1", "{\"paused\":false}", DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
                await _store.SaveCheckpointAsync(new DurableCheckpoint($"dispatch-pause:{resumedRun.Id}", resumedRun.Id.ToString(), "dispatch-pause-v1", "{\"paused\":false}", DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
                _autopilot = "READY";
                await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
                break;
'@

# Manager LOGIN/CHALLENGE and global faults persist and close the canonical global new-send gate.
Replace-Exact $hostFile @'
        if (semantic.Auth.State is AuthState.LoginRequired or AuthState.Challenge)
        {
            CaptureProviderAttention(semantic.Auth.State == AuthState.Challenge ? "CHALLENGE" : "LOGIN_REQUIRED", runtime.RuntimeId, "Manager ChatGPT session");
            await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        if (resilience.Scope == FaultScope.Global && resilience.PauseUnsafeNewSends)
        {
            var cooldown = resilience.State == ChatGptResilienceState.RateLimited ? new ConservativeCooldownPolicy().GetCooldown(1) : TimeSpan.FromSeconds(30);
            _sendGate.Apply(resilience, DateTimeOffset.UtcNow, cooldown);
            _autopilot = resilience.State.ToString().ToUpperInvariant();
            await _store.SaveCheckpointAsync(new DurableCheckpoint($"runtime-health:{run.Id}", run.Id.ToString(), "runtime-health-v1", JsonSerializer.Serialize(new { resilience.State, resilience.Reason, ResumeNotBefore = _sendGate.Snapshot.ResumeNotBefore }), DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
            await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
'@ @'
        if (semantic.Auth.State is AuthState.LoginRequired or AuthState.Challenge)
        {
            CaptureProviderAttention(semantic.Auth.State == AuthState.Challenge ? "CHALLENGE" : "LOGIN_REQUIRED", runtime.RuntimeId, "Manager ChatGPT session");
            await PersistGlobalHealthPauseAsync(resilience, runtime.RuntimeId, cancellationToken).ConfigureAwait(false);
            await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        if (resilience.Scope == FaultScope.Global && resilience.PauseUnsafeNewSends)
        {
            await PersistGlobalHealthPauseAsync(resilience, runtime.RuntimeId, cancellationToken).ConfigureAwait(false);
            await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
'@

# Durable repeated Manager fingerprints survive restart.
Replace-Exact $hostFile @'
        _recentPlanFingerprints.Enqueue(planFingerprint);
        while (_recentPlanFingerprints.Count > 3) _recentPlanFingerprints.Dequeue();
        if (_recentPlanFingerprints.Count == 3 && _recentPlanFingerprints.Distinct(StringComparer.Ordinal).Count() == 1)
'@ @'
        _recentPlanFingerprints.Enqueue(planFingerprint);
        while (_recentPlanFingerprints.Count > 3) _recentPlanFingerprints.Dequeue();
        await PersistLoopGuardAsync(false, cancellationToken).ConfigureAwait(false);
        if (_recentPlanFingerprints.Count == 3 && _recentPlanFingerprints.Distinct(StringComparer.Ordinal).Count() == 1)
'@

# Worker LOGIN/CHALLENGE, RATE_LIMIT and OFFLINE use the same durable global gate.
Replace-Exact $hostFile @'
            if (semantic.Auth.State is AuthState.LoginRequired or AuthState.Challenge)
            {
                CaptureProviderAttention(semantic.Auth.State == AuthState.Challenge ? "CHALLENGE" : "LOGIN_REQUIRED", runtime.RuntimeId, $"Worker {slot.Value} ChatGPT session");
                continue;
            }
            var resilience = new ChatGptResilienceClassifier().Classify(semantic, DateTimeOffset.UtcNow - runtime.LastActivityAt);
            if (resilience.Scope == FaultScope.Global && resilience.PauseUnsafeNewSends)
            {
                _sendGate.Apply(resilience, DateTimeOffset.UtcNow, new ConservativeCooldownPolicy().GetCooldown(1));
                _autopilot = resilience.State.ToString().ToUpperInvariant();
                continue;
            }
'@ @'
            var resilience = new ChatGptResilienceClassifier().Classify(semantic, DateTimeOffset.UtcNow - runtime.LastActivityAt);
            if (semantic.Auth.State is AuthState.LoginRequired or AuthState.Challenge)
            {
                CaptureProviderAttention(semantic.Auth.State == AuthState.Challenge ? "CHALLENGE" : "LOGIN_REQUIRED", runtime.RuntimeId, $"Worker {slot.Value} ChatGPT session");
                await PersistGlobalHealthPauseAsync(resilience, runtime.RuntimeId, cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (resilience.Scope == FaultScope.Global && resilience.PauseUnsafeNewSends)
            {
                await PersistGlobalHealthPauseAsync(resilience, runtime.RuntimeId, cancellationToken).ConfigureAwait(false);
                continue;
            }
'@

# Durable completion stagnation survives restart.
Replace-Exact $hostFile @'
        _recentVerifiedCompletion.Enqueue(_run.VerifiedCompletion.Percent);
        while (_recentVerifiedCompletion.Count > 3) _recentVerifiedCompletion.Dequeue();
        if (_recentVerifiedCompletion.Count == 3 && _recentVerifiedCompletion.Distinct().Count() == 1 && _run.VerifiedCompletion.Percent < 99m)
'@ @'
        _recentVerifiedCompletion.Enqueue(_run.VerifiedCompletion.Percent);
        while (_recentVerifiedCompletion.Count > 3) _recentVerifiedCompletion.Dequeue();
        await PersistLoopGuardAsync(false, cancellationToken).ConfigureAwait(false);
        if (_recentVerifiedCompletion.Count == 3 && _recentVerifiedCompletion.Distinct().Count() == 1 && _run.VerifiedCompletion.Percent < 99m)
'@

# AutoResume never opens the global gate on cooldown alone; fresh semantic proof is mandatory.
Replace-Exact $hostFile @'
                if (_sendGate.Snapshot is { IsPaused: true, ResumeNotBefore: not null } gate && gate.ResumeNotBefore <= DateTimeOffset.UtcNow && _settings.AutoResume)
                {
                    await _newSendPause.ResumeNewSendsAsync("Conservative runtime cooldown elapsed; reconciling before sends resume.", cancellationToken).ConfigureAwait(false);
                    _autopilot = _currentWave?.State == WaveState.Running ? "WAITING_WORKERS" : "RECOVERING";
                }
'@ @'
                if (_sendGate.Snapshot is { IsPaused: true, ResumeNotBefore: not null } gate && gate.ResumeNotBefore <= DateTimeOffset.UtcNow && _settings.AutoResume)
                {
                    if (await TryResumeAfterFreshSemanticHealthAsync(cancellationToken).ConfigureAwait(false))
                        _autopilot = _currentWave?.State == WaveState.Running ? "WAITING_WORKERS" : "RECOVERING";
                }
'@

# Repeated identical runtime errors are durable and finite across restart.
Replace-Exact $hostFile @'
            catch (InvalidOperationException)
            {
                // Incomplete generation, login, offline and stale evidence remain observable runtime states;
                // the loop polls conservatively and never retries a dispatch here.
            }
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
'@ @'
            catch (InvalidOperationException ex)
            {
                await RecordRuntimeLoopErrorAsync(ex, cancellationToken).ConfigureAwait(false);
            }
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
'@

# Surface durable loop-stop state rather than hardcoding NORMAL.
Replace-Exact $hostFile @'
            LoopGuardState: run is null ? "WAITING_FOR_PROJECT" : "NORMAL",
'@ @'
            LoopGuardState: run is null ? "WAITING_FOR_PROJECT" : run.State == ProjectRunState.StalledAutoStopped ? "STALLED_AUTO_STOPPED" : _runtimeErrorCount > 0 ? $"WATCH:{_runtimeErrorCount}" : "NORMAL",
'@

# Central durable global health / semantic reopen / loop persistence helpers.
Replace-Exact $hostFile @'
    private async Task PersistAgentBindingAsync(LogicalAgentId agentId, WorkerSlotId? slot, TaskId? taskId, ConversationId conversationId, CancellationToken cancellationToken)
'@ @'
    private async Task PersistGlobalHealthPauseAsync(ResilienceDecision resilience, string runtimeId, CancellationToken cancellationToken)
    {
        var run = RequireActiveRun();
        var cooldown = resilience.RequiresHumanAction ? (TimeSpan?)null : resilience.State == ChatGptResilienceState.RateLimited ? new ConservativeCooldownPolicy().GetCooldown(1) : TimeSpan.FromSeconds(30);
        _sendGate.Apply(resilience with { Scope = FaultScope.Global, PauseUnsafeNewSends = true }, DateTimeOffset.UtcNow, cooldown);
        _runtimeHealthFault = resilience.State.ToString().ToUpperInvariant();
        _autopilot = _runtimeHealthFault;
        var durable = new DurableRuntimeHealth(true, _runtimeHealthFault, resilience.Reason, _sendGate.Snapshot.ResumeNotBefore, resilience.RequiresHumanAction, runtimeId);
        await _store.SaveCheckpointAsync(new DurableCheckpoint($"runtime-health:{run.Id}", run.Id.ToString(), "runtime-health-v2", JsonSerializer.Serialize(durable), DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> TryResumeAfterFreshSemanticHealthAsync(CancellationToken cancellationToken)
    {
        var run = RequireActiveRun();
        if (_runtimeHealthFault is null) return true;
        var gate = _sendGate.Snapshot;
        if (gate.ResumeNotBefore is not null && gate.ResumeNotBefore > DateTimeOffset.UtcNow) return false;
        var runtimes = (await _runtimeRegistry.ListAsync(cancellationToken).ConfigureAwait(false))
            .Where(x => !x.IsArchived && x.State is not BrowserSessionState.Killed && StringComparer.Ordinal.Equals(x.ProjectRunId, run.Id.ToString()) && !string.IsNullOrWhiteSpace(x.TaskId) && !string.IsNullOrWhiteSpace(x.ConversationIdentity) && !string.IsNullOrWhiteSpace(x.ProviderConversationIdentity))
            .ToArray();
        if (runtimes.Length == 0) return false;
        foreach (var runtime in runtimes)
        {
            var expected = new BrowserDispatchExpectation(run.Id.ToString(), runtime.LogicalAgentId, runtime.TaskId!, runtime.ConversationIdentity!, runtime.ProviderConversationIdentity!, runtime.WorkerSlotId);
            var semantic = await _browserAdapter.InspectAsync(runtime, expected, cancellationToken).ConfigureAwait(false);
            if (semantic.Auth.State != AuthState.Authenticated || semantic.Health.State != PageHealth.Healthy) return false;
        }
        await _newSendPause.ResumeNewSendsAsync("Fresh semantic Browser health proved safe after durable global fault.", cancellationToken).ConfigureAwait(false);
        _runtimeHealthFault = null;
        await _store.SaveCheckpointAsync(new DurableCheckpoint($"runtime-health:{run.Id}", run.Id.ToString(), "runtime-health-v2", JsonSerializer.Serialize(new DurableRuntimeHealth(false, "READY", "Fresh semantic health proven.", null, false, null)), DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task PersistLoopGuardAsync(bool autoStopped, CancellationToken cancellationToken)
    {
        if (_run is null) return;
        var state = new DurableLoopGuard(_recentPlanFingerprints.ToArray(), _recentVerifiedCompletion.ToArray(), _runtimeErrorFingerprint, _runtimeErrorCount, autoStopped);
        await _store.SaveCheckpointAsync(new DurableCheckpoint($"loop-guard:{_run.Id}", _run.Id.ToString(), "loop-guard-v2", JsonSerializer.Serialize(state), DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
    }

    private async Task RecordRuntimeLoopErrorAsync(InvalidOperationException error, CancellationToken cancellationToken)
    {
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{error.GetType().FullName}|{error.Message}"))).ToLowerInvariant();
        if (StringComparer.Ordinal.Equals(_runtimeErrorFingerprint, fingerprint)) _runtimeErrorCount++;
        else
        {
            _runtimeErrorFingerprint = fingerprint;
            _runtimeErrorCount = 1;
        }
        if (_runtimeErrorCount >= 3 && _run is not null)
        {
            _run = _run with { State = ProjectRunState.StalledAutoStopped };
            _autopilot = "STALLED";
            _latestManagerHandoff = "STALLED_AUTO_STOPPED — the same runtime error repeated three times across durable loop state.";
            await _orchestrationStore.SaveAsync(new OrchestrationRecoverySnapshot(_run, _currentWave, _runtimeTasks, _assignments, [], null, OrchestrationPhase.StalledAutoStopped, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
            await PersistLoopGuardAsync(true, cancellationToken).ConfigureAwait(false);
            return;
        }
        await PersistLoopGuardAsync(false, cancellationToken).ConfigureAwait(false);
    }

    private async Task PersistAgentBindingAsync(LogicalAgentId agentId, WorkerSlotId? slot, TaskId? taskId, ConversationId conversationId, CancellationToken cancellationToken)
'@

# Durable records + parser.
Replace-Exact $hostFile @'
    private sealed record SelectedProjectState(string ProjectControlId, string ProjectIdentity, string DisplayName, string Repository, Guid ProjectRunId);

    private sealed class EmptyCompletedTaskIndex : ICompletedTaskIndex
'@ @'
    private sealed record SelectedProjectState(string ProjectControlId, string ProjectIdentity, string DisplayName, string Repository, Guid ProjectRunId);
    private sealed record DurableRuntimeHealth(bool Active, string State, string Reason, DateTimeOffset? ResumeNotBefore, bool RequiresHumanAction, string? RuntimeId);
    private sealed record DurableLoopGuard(IReadOnlyList<string> PlanFingerprints, IReadOnlyList<decimal> VerifiedCompletion, string? RuntimeErrorFingerprint, int RuntimeErrorCount, bool AutoStopped);

    private static ChatGptResilienceState ParseResilienceState(string value) => Enum.TryParse<ChatGptResilienceState>(value, true, out var state) ? state : ChatGptResilienceState.Paused;

    private sealed class EmptyCompletedTaskIndex : ICompletedTaskIndex
'@

Write-Host 'Durable runtime P1 safety patch applied.'
