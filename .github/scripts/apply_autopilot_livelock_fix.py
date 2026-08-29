from pathlib import Path

source_path = Path("src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs")
test_path = Path("tests/PCCExecutive.App.Tests/ProductionRecoveryWiringContractTests.cs")

source = source_path.read_text(encoding="utf-8")

old = '_autopilot = recovered.Phase.ToString().ToUpperInvariant();'
new = '_autopilot = MapRecoveredPhaseToAutopilot(recovered.Phase, recovered.CurrentWave);'
if old not in source:
    raise SystemExit("startup raw phase assignment anchor not found")
source = source.replace(old, new, 1)

snapshot_anchor = '        Snapshot = BuildSnapshot(Array.Empty<BrowserRuntimeRecord>(), new HashSet<string>(StringComparer.Ordinal));'
if snapshot_anchor not in source:
    raise SystemExit("snapshot constructor anchor not found")
source = source.replace(snapshot_anchor, '        NormalizeRecoveredAutopilotState();\n' + snapshot_anchor, 1)

loop_anchor = '''            try
            {
                if (_sendGate.Snapshot is { IsPaused: true, ResumeNotBefore: not null } gate'''
if loop_anchor not in source:
    raise SystemExit("autopilot loop anchor not found")
source = source.replace(loop_anchor, '''            try
            {
                NormalizeRecoveredAutopilotState();
                if (_sendGate.Snapshot is { IsPaused: true, ResumeNotBefore: not null } gate''', 1)

old_fingerprint_block = '''        var planFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", parsed.Plan.Tasks.Select(x => x.Task.Fingerprint))))).ToLowerInvariant();
        _recentPlanFingerprints.Enqueue(planFingerprint);
        while (_recentPlanFingerprints.Count > 3) _recentPlanFingerprints.Dequeue();
        await PersistLoopGuardAsync(false, cancellationToken).ConfigureAwait(false);
        if (_recentPlanFingerprints.Count == 3 && _recentPlanFingerprints.Distinct(StringComparer.Ordinal).Count() == 1)
        {
            _run = run with { State = ProjectRunState.StalledAutoStopped };
            _autopilot = "STALLED";
            _latestManagerHandoff = "STALLED_AUTO_STOPPED — Manager repeated the identical task fingerprint for three plans.";
            await _orchestrationStore.SaveAsync(new OrchestrationRecoverySnapshot(_run, _currentWave, _runtimeTasks, _assignments, [], null, OrchestrationPhase.StalledAutoStopped, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
            await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
'''
if old_fingerprint_block not in source:
    raise SystemExit("pre-validation fingerprint block anchor not found")
source = source.replace(old_fingerprint_block,
'''        var planFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", parsed.Plan.Tasks.Select(x => x.Task.Fingerprint))))).ToLowerInvariant();
''', 1)

validation_anchor = '''        if (!validation.IsValid)
            throw new InvalidOperationException($"Manager wave rejected: {string.Join("; ", validation.Findings.Select(x => $"{x.Code}:{x.Message}"))}");
        if (parsed.Plan.Tasks.Count == 0)
'''
if validation_anchor not in source:
    raise SystemExit("post-validation anchor not found")
source = source.replace(validation_anchor,
'''        if (!validation.IsValid)
            throw new InvalidOperationException($"Manager wave rejected: {string.Join("; ", validation.Findings.Select(x => $"{x.Code}:{x.Message}"))}");

        // Only accepted Manager waves count toward repetition. Polling/reparsing one
        // already-received response must never manufacture three "plans" and self-stall.
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

        if (parsed.Plan.Tasks.Count == 0)
''', 1)

recovery_anchor = '''                            var prePlanRecovery = PrePlanAutoRecoveryPolicy.Classify(loop.RuntimeErrorFingerprint);
                            _autopilot = prePlanRecovery == PrePlanAutoRecoveryMode.ExistingManagerResponse ? "PLANNING" : "RECOVERING";
'''
if recovery_anchor not in source:
    raise SystemExit("pre-plan recovery classification anchor not found")
source = source.replace(recovery_anchor,
'''                            var hasReceivedManagerResponseFailure =
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
    raise SystemExit("method insertion anchor not found")
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

        // Durable OrchestrationPhase names are not the command vocabulary used by the
        // autonomous loop. Normalize old persisted names before any routing decision.
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
                ProjectRunState.ManagerPlanning => "PLANNING",
                ProjectRunState.WaveReady when _currentPlan is not null && _currentWave is { State: WaveState.Ready } => "READY_TO_DISPATCH",
                ProjectRunState.Dispatching when _currentWave is { State: WaveState.Running } => "WAITING_WORKERS",
                ProjectRunState.WaveRunning => "WAITING_WORKERS",
                ProjectRunState.Reconciling => "WAITING_WORKERS",
                ProjectRunState.ManagerReview => "MANAGER_REVIEW",
                ProjectRunState.ClosureMode => "CLOSURE_VERIFY",
                ProjectRunState.VerifiedComplete => "VERIFIED_COMPLETE",
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
        if (_currentPlan is not null && _currentWave is { State: WaveState.Running }) return "WAITING_WORKERS";

        var managerResponseFailure =
            _runtimeErrorFingerprint?.Contains("Manager response rejected:", StringComparison.OrdinalIgnoreCase) == true ||
            _runtimeErrorFingerprint?.Contains("Manager wave rejected:", StringComparison.OrdinalIgnoreCase) == true ||
            _runtimeErrorFingerprint?.Contains("MANAGER_PLAN_", StringComparison.OrdinalIgnoreCase) == true;
        if (!managerResponseFailure) return "STALLED";

        _run = _run is null ? null : _run with { State = ProjectRunState.ManagerPlanning };
        _runtimeErrorCount = 0;
        _latestManagerHandoff = "RECOVERING_MANAGER_RESPONSE — retrying the already-received Manager response after recovery; no duplicate Manager prompt will be sent.";
        return "PLANNING";
    }

'''
source = source.replace(method_anchor, methods + method_anchor, 1)
source_path.write_text(source, encoding="utf-8")

tests = test_path.read_text(encoding="utf-8")
insert_anchor = '    [Fact]\n    public void Normal_disposal_invokes_safe_shutdown_coordinator()'
if insert_anchor not in tests:
    raise SystemExit("test insertion anchor not found")
regression = r'''    [Fact]
    public void Restart_maps_durable_orchestration_phase_to_actionable_autopilot_vocabulary()
    {
        var source = ReadSource("src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs");

        Assert.DoesNotContain("_autopilot = recovered.Phase.ToString().ToUpperInvariant();", source, StringComparison.Ordinal);
        Assert.Contains("MapRecoveredPhaseToAutopilot(recovered.Phase, recovered.CurrentWave)", source, StringComparison.Ordinal);
        var mapping = Slice(source, "private static string MapRecoveredPhaseToAutopilot", "private void NormalizeRecoveredAutopilotState");
        Assert.Contains("OrchestrationPhase.ManagerPlanning => \"PLANNING\"", mapping, StringComparison.Ordinal);
        Assert.Contains("OrchestrationPhase.WaveValidation => wave?.State == WaveState.Ready ? \"READY_TO_DISPATCH\"", mapping, StringComparison.Ordinal);
        Assert.Contains("OrchestrationPhase.ManagerReview => \"MANAGER_REVIEW\"", mapping, StringComparison.Ordinal);
        Assert.Contains("OrchestrationPhase.ClosureMode => \"CLOSURE_VERIFY\"", mapping, StringComparison.Ordinal);
    }

    [Fact]
    public void Autopilot_normalizes_ready_against_durable_run_state_before_routing()
    {
        var source = ReadSource("src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs");
        var loop = Slice(source, "private async Task RunAutopilotLoopAsync", "private async Task RunSessionActionAsync");
        var normalize = Slice(source, "private void NormalizeRecoveredAutopilotState", "private string RecoverStalledManagerResponseState");

        AssertOrdered(loop,
            "NormalizeRecoveredAutopilotState();",
            "if (_currentPlan is null",
            "ReconcileManagerResponseAsync(cancellationToken)");
        Assert.Contains("ProjectRunState.ManagerPlanning => \"PLANNING\"", normalize, StringComparison.Ordinal);
        Assert.Contains("ProjectRunState.WaveReady when _currentPlan is not null", normalize, StringComparison.Ordinal);
        Assert.Contains("ProjectRunState.ManagerReview => \"MANAGER_REVIEW\"", normalize, StringComparison.Ordinal);
        Assert.Contains("ProjectRunState.StalledAutoStopped => RecoverStalledManagerResponseState()", normalize, StringComparison.Ordinal);
    }

    [Fact]
    public void Repeated_polling_of_one_unaccepted_manager_response_does_not_trip_plan_repetition_guard()
    {
        var source = ReadSource("src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs");
        var reconcile = Slice(source, "private async Task ReconcileManagerResponseAsync", "private async Task StartDispatchAsync");

        var validationIndex = reconcile.IndexOf("if (!validation.IsValid)", StringComparison.Ordinal);
        var enqueueIndex = reconcile.IndexOf("_recentPlanFingerprints.Enqueue(planFingerprint);", StringComparison.Ordinal);
        Assert.True(validationIndex >= 0);
        Assert.True(enqueueIndex > validationIndex, "Plan fingerprint must be counted only after fresh wave validation succeeds.");
        Assert.Contains("identical accepted task fingerprint across three Manager waves", reconcile, StringComparison.Ordinal);
    }

    [Fact]
    public void Restarted_manager_response_validation_failure_is_reparsed_without_duplicate_prompt()
    {
        var source = ReadSource("src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs");
        Assert.Contains("loop.RuntimeErrorFingerprint?.Contains(\"Manager wave rejected:\"", source, StringComparison.Ordinal);
        Assert.Contains("loop.RuntimeErrorFingerprint?.Contains(\"Manager response rejected:\"", source, StringComparison.Ordinal);
        var recover = Slice(source, "private string RecoverStalledManagerResponseState", "private async Task PersistLoopGuardAsync");
        Assert.Contains("retrying the already-received Manager response after recovery; no duplicate Manager prompt will be sent", recover, StringComparison.Ordinal);
        Assert.Contains("return \"PLANNING\";", recover, StringComparison.Ordinal);
    }

'''
tests = tests.replace(insert_anchor, regression + insert_anchor, 1)
test_path.write_text(tests, encoding="utf-8")
