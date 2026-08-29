from pathlib import Path

source_path = Path("src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs")
test_path = Path("tests/PCCExecutive.App.Tests/AutopilotLivelockRecoveryContractTests.cs")
source = source_path.read_text(encoding="utf-8")

# 1. Durable orchestration phases must be restored into the runtime command vocabulary.
old_restore = '_autopilot = recovered.Phase.ToString().ToUpperInvariant();'
if old_restore not in source:
    raise SystemExit("raw recovered phase assignment not found")
source = source.replace(old_restore, '_autopilot = MapRecoveredPhaseToAutopilot(recovered.Phase, recovered.CurrentWave);', 1)

# 2. Normalize once during construction and before every autonomous routing decision.
snapshot_anchor = '        Snapshot = BuildSnapshot(Array.Empty<BrowserRuntimeRecord>(), new HashSet<string>(StringComparer.Ordinal));'
if snapshot_anchor not in source:
    raise SystemExit("initial snapshot anchor not found")
source = source.replace(snapshot_anchor, '        NormalizeRecoveredAutopilotState();\n' + snapshot_anchor, 1)

pause_anchor = '                if (_autopilot == "PAUSED" || _run?.State is ProjectRunState.VerifiedComplete or ProjectRunState.BlockedExternal or ProjectRunState.StalledAutoStopped or ProjectRunState.StoppedByOperator)'
if pause_anchor not in source:
    raise SystemExit("autopilot pause/state gate anchor not found")
source = source.replace(pause_anchor, '                NormalizeRecoveredAutopilotState();\n' + pause_anchor, 1)

# 3. The legacy implementation counted every poll/reparse of one response as a new plan.
fp_decl = '        var planFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", parsed.Plan.Tasks.Select(x => x.Task.Fingerprint))))).ToLowerInvariant();\n'
fp_start = source.find(fp_decl)
routing_start = source.find('        var routingResult = await _pcc.ResolveProjectAsync', fp_start)
if fp_start < 0 or routing_start < 0:
    raise SystemExit("manager fingerprint/routing block not found")
source = source[:fp_start] + fp_decl + source[routing_start:]

validation_anchor = '''        if (!validation.IsValid)
            throw new InvalidOperationException($"Manager wave rejected: {string.Join("; ", validation.Findings.Select(x => $"{x.Code}:{x.Message}"))}");
'''
if validation_anchor not in source:
    raise SystemExit("fresh manager validation anchor not found")
accepted_guard = validation_anchor + '''
        // Count repetition only after a fresh wave is accepted. Re-reading one already-
        // received response must never manufacture three Manager plans and self-stall.
        _recentPlanFingerprints.Enqueue(planFingerprint);
        while (_recentPlanFingerprints.Count > 3) _recentPlanFingerprints.Dequeue();
        if (_recentPlanFingerprints.Count == 3 && _recentPlanFingerprints.Distinct(StringComparer.Ordinal).Count() == 1)
        {
            _run = run with { State = ProjectRunState.StalledAutoStopped };
            _autopilot = "STALLED";
            _latestManagerHandoff = "STALLED_AUTO_STOPPED — Manager repeated the identical accepted task fingerprint across three Manager waves.";
            await PersistLoopGuardAsync(true, cancellationToken).ConfigureAwait(false);
            await _orchestrationStore.SaveAsync(new OrchestrationRecoverySnapshot(_run, _currentWave, _runtimeTasks, _assignments, [], null, OrchestrationPhase.StalledAutoStopped, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
            await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        await PersistLoopGuardAsync(false, cancellationToken).ConfigureAwait(false);
'''
source = source.replace(validation_anchor, accepted_guard, 1)

# 4. A received Manager response validation failure is recoverable by reparsing, never by resend.
recovery_anchor = '''                            var prePlanRecovery = PrePlanAutoRecoveryPolicy.Classify(loop.RuntimeErrorFingerprint);
                            _autopilot = prePlanRecovery == PrePlanAutoRecoveryMode.ExistingManagerResponse ? "PLANNING" : "RECOVERING";
'''
if recovery_anchor not in source:
    raise SystemExit("pre-plan recovery classifier anchor not found")
source = source.replace(recovery_anchor, '''                            var hasReceivedManagerResponseFailure =
                                loop.RuntimeErrorFingerprint?.Contains("Manager response rejected:", StringComparison.OrdinalIgnoreCase) == true ||
                                loop.RuntimeErrorFingerprint?.Contains("Manager wave rejected:", StringComparison.OrdinalIgnoreCase) == true ||
                                loop.RuntimeErrorFingerprint?.Contains("MANAGER_PLAN_", StringComparison.OrdinalIgnoreCase) == true;
                            var prePlanRecovery = hasReceivedManagerResponseFailure
                                ? PrePlanAutoRecoveryMode.ExistingManagerResponse
                                : PrePlanAutoRecoveryPolicy.Classify(loop.RuntimeErrorFingerprint);
                            _autopilot = prePlanRecovery == PrePlanAutoRecoveryMode.ExistingManagerResponse ? "PLANNING" : "RECOVERING";
''', 1)

method_anchor = '    private async Task PersistLoopGuardAsync(bool autoStopped, CancellationToken cancellationToken)'
if method_anchor not in source:
    raise SystemExit("loop guard method anchor not found")
methods = r'''    private static string MapRecoveredPhaseToAutopilot(OrchestrationPhase phase, Wave? wave) => phase switch
    {
        OrchestrationPhase.Initializing => "READY",
        OrchestrationPhase.ManagerPlanning => "PLANNING",
        OrchestrationPhase.WaveValidation => wave?.State == WaveState.Ready ? "READY_TO_DISPATCH" : "PLANNING",
        OrchestrationPhase.Dispatching => wave?.State == WaveState.Running ? "WAITING_WORKERS" : wave?.State == WaveState.Ready ? "READY_TO_DISPATCH" : "RECOVERING",
        OrchestrationPhase.WaveRunning => "WAITING_WORKERS",
        OrchestrationPhase.Reconciling => "WAITING_WORKERS",
        OrchestrationPhase.ManagerReview => "MANAGER_REVIEW",
        OrchestrationPhase.ClosureMode => "CLOSURE_VERIFY",
        OrchestrationPhase.VerifiedComplete => "VERIFIED_COMPLETE",
        OrchestrationPhase.BlockedExternal => "RECOVERING",
        OrchestrationPhase.StalledAutoStopped => "STALLED",
        OrchestrationPhase.StoppedByOperator => "PAUSED",
        _ => "RECOVERING"
    };

    private void NormalizeRecoveredAutopilotState()
    {
        if (_run is null) return;

        _autopilot = _autopilot switch
        {
            "INITIALIZING" => MapRecoveredPhaseToAutopilot(OrchestrationPhase.Initializing, _currentWave),
            "MANAGERPLANNING" => MapRecoveredPhaseToAutopilot(OrchestrationPhase.ManagerPlanning, _currentWave),
            "WAVEVALIDATION" => MapRecoveredPhaseToAutopilot(OrchestrationPhase.WaveValidation, _currentWave),
            "DISPATCHING" => MapRecoveredPhaseToAutopilot(OrchestrationPhase.Dispatching, _currentWave),
            "WAVERUNNING" => MapRecoveredPhaseToAutopilot(OrchestrationPhase.WaveRunning, _currentWave),
            "RECONCILING" => MapRecoveredPhaseToAutopilot(OrchestrationPhase.Reconciling, _currentWave),
            "MANAGERREVIEW" => MapRecoveredPhaseToAutopilot(OrchestrationPhase.ManagerReview, _currentWave),
            "CLOSUREMODE" => MapRecoveredPhaseToAutopilot(OrchestrationPhase.ClosureMode, _currentWave),
            "VERIFIEDCOMPLETE" => MapRecoveredPhaseToAutopilot(OrchestrationPhase.VerifiedComplete, _currentWave),
            "BLOCKEDEXTERNAL" => MapRecoveredPhaseToAutopilot(OrchestrationPhase.BlockedExternal, _currentWave),
            "STALLEDAUTOSTOPPED" => MapRecoveredPhaseToAutopilot(OrchestrationPhase.StalledAutoStopped, _currentWave),
            "STOPPEDBYOPERATOR" => MapRecoveredPhaseToAutopilot(OrchestrationPhase.StoppedByOperator, _currentWave),
            _ => _autopilot
        };

        if (_autopilot == "READY")
        {
            _autopilot = _run.State switch
            {
                ProjectRunState.Initializing => "READY",
                ProjectRunState.ManagerPlanning => "PLANNING",
                ProjectRunState.WaveReady when _currentPlan is not null && _currentWave is { State: WaveState.Ready } => "READY_TO_DISPATCH",
                ProjectRunState.Dispatching when _currentWave is { State: WaveState.Running or WaveState.Dispatching } => "WAITING_WORKERS",
                ProjectRunState.WaveRunning => "WAITING_WORKERS",
                ProjectRunState.Reconciling => "WAITING_WORKERS",
                ProjectRunState.ManagerReview => "MANAGER_REVIEW",
                ProjectRunState.ClosureMode => "CLOSURE_VERIFY",
                ProjectRunState.VerifiedComplete => "VERIFIED_COMPLETE",
                ProjectRunState.BlockedExternal => "RECOVERING",
                ProjectRunState.StalledAutoStopped => RecoverStalledManagerResponseState(),
                ProjectRunState.StoppedByOperator => "PAUSED",
                _ => _autopilot
            };
        }

        var durablePlanWaveGap =
            (_currentPlan is not null && _currentWave is null) ||
            (_currentPlan is null && _currentWave is { State: WaveState.Ready });
        if (durablePlanWaveGap && (_autopilot is "READY" or "RECOVERING" or "READY_TO_DISPATCH"))
        {
            _autopilot = "PLANNING";
            _latestManagerHandoff = "RECOVERING_MANAGER_RESPONSE — durable Manager plan/wave state is incomplete; PCC is re-reading and revalidating the already-received Manager response without sending a duplicate prompt.";
        }
    }

    private string RecoverStalledManagerResponseState()
    {
        if (!_settings.AutoResume) return "STALLED";
        if (_currentPlan is not null && _currentWave is { State: WaveState.Ready }) return "READY_TO_DISPATCH";
        if (_currentPlan is not null && _currentWave is { State: WaveState.Running or WaveState.Dispatching }) return "WAITING_WORKERS";

        var managerResponseFailure =
            _runtimeErrorFingerprint?.Contains("Manager response rejected:", StringComparison.OrdinalIgnoreCase) == true ||
            _runtimeErrorFingerprint?.Contains("Manager wave rejected:", StringComparison.OrdinalIgnoreCase) == true ||
            _runtimeErrorFingerprint?.Contains("MANAGER_PLAN_", StringComparison.OrdinalIgnoreCase) == true;

        // Builds before this fix persisted the same unaccepted response three times and
        // then marked the run STALLED. No accepted plan checkpoint exists in that case.
        var legacyUnacceptedResponseSelfStall =
            _currentPlan is null &&
            _recentPlanFingerprints.Count >= 3 &&
            _recentPlanFingerprints.Distinct(StringComparer.Ordinal).Count() == 1;

        if (!managerResponseFailure && !legacyUnacceptedResponseSelfStall) return "STALLED";

        if (legacyUnacceptedResponseSelfStall)
            _recentPlanFingerprints.Clear();
        _run = _run is null ? null : _run with { State = ProjectRunState.ManagerPlanning };
        _runtimeErrorFingerprint = null;
        _runtimeErrorCount = 0;
        _latestManagerHandoff = "RECOVERING_MANAGER_RESPONSE — retrying the already-received Manager response after recovery; no duplicate Manager prompt will be sent.";
        return "PLANNING";
    }

'''
source = source.replace(method_anchor, methods + method_anchor, 1)
source_path.write_text(source, encoding="utf-8")

# Add a focused source-contract test file without relying on brittle insertion points.
test_path.write_text(r'''using Xunit;

namespace PCCExecutive.App.Tests;

public sealed class AutopilotLivelockRecoveryContractTests
{
    private static string Source()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PCCExecutive.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory!.FullName, "src", "PCCExecutive.App", "Presentation", "IntegratedPresentationGateway.cs"));
    }

    [Fact]
    public void Restart_uses_actionable_autopilot_vocabulary()
    {
        var source = Source();
        Assert.DoesNotContain("_autopilot = recovered.Phase.ToString().ToUpperInvariant();", source, StringComparison.Ordinal);
        Assert.Contains("MapRecoveredPhaseToAutopilot(recovered.Phase, recovered.CurrentWave)", source, StringComparison.Ordinal);
        Assert.Contains("OrchestrationPhase.ManagerPlanning => \"PLANNING\"", source, StringComparison.Ordinal);
        Assert.Contains("OrchestrationPhase.WaveValidation => wave?.State == WaveState.Ready ? \"READY_TO_DISPATCH\"", source, StringComparison.Ordinal);
        Assert.Contains("ProjectRunState.StalledAutoStopped => RecoverStalledManagerResponseState()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Same_unaccepted_response_is_not_counted_as_three_manager_waves()
    {
        var source = Source();
        var validation = source.IndexOf("if (!validation.IsValid)", StringComparison.Ordinal);
        var enqueue = source.IndexOf("_recentPlanFingerprints.Enqueue(planFingerprint);", validation, StringComparison.Ordinal);
        Assert.True(validation >= 0);
        Assert.True(enqueue > validation);
        Assert.Contains("identical accepted task fingerprint across three Manager waves", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Legacy_false_stall_recovers_by_reparsing_existing_response()
    {
        var source = Source();
        Assert.Contains("legacyUnacceptedResponseSelfStall", source, StringComparison.Ordinal);
        Assert.Contains("_recentPlanFingerprints.Clear();", source, StringComparison.Ordinal);
        Assert.Contains("no duplicate Manager prompt will be sent", source, StringComparison.Ordinal);
        Assert.Contains("NormalizeRecoveredAutopilotState();", source, StringComparison.Ordinal);
    }
}
''', encoding="utf-8")
