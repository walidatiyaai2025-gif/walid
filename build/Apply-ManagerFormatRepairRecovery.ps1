$ErrorActionPreference = 'Stop'

$path = 'src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs'
$text = Get-Content -Raw -LiteralPath $path

$methodPattern = '(?s)    private string BuildManagerPrompt\(ProjectRun run, ProjectBaselineSnapshot baseline\) =>\s*\$".*?";\r?\n\r?\n    private async Task ReconcileManagerResponseAsync'
$methodRegex = [regex]::new($methodPattern)
if (-not $methodRegex.IsMatch($text)) {
    throw 'BuildManagerPrompt/ReconcileManagerResponse boundary was not found.'
}

$replacement = @'
    private string BuildManagerPrompt(ProjectRun run, ProjectBaselineSnapshot baseline) =>
        ManagerPlanningPromptBuilder.Build(_projectControlId ?? baseline.ProjectControlId, _projectDisplay, _projectRepository, run, baseline, _autopilot);

    private sealed record DurableManagerFormatRepair(string? RejectedResponseHash, int AttemptsUsed, string? RepairContentHash, DateTimeOffset? SubmittedAt);

    private static string ManagerFormatRepairCheckpointKey(ProjectRun run) => $"manager-format-repair:{run.Id}";

    private async Task<DurableManagerFormatRepair> LoadManagerFormatRepairStateAsync(ProjectRun run, CancellationToken cancellationToken)
    {
        var checkpoint = await _store.LoadCheckpointAsync(ManagerFormatRepairCheckpointKey(run), cancellationToken).ConfigureAwait(false);
        if (checkpoint is null || string.IsNullOrWhiteSpace(checkpoint.Payload))
            return new DurableManagerFormatRepair(null, 0, null, null);
        try
        {
            return JsonSerializer.Deserialize<DurableManagerFormatRepair>(checkpoint.Payload)
                ?? new DurableManagerFormatRepair(null, 0, null, null);
        }
        catch (JsonException)
        {
            return new DurableManagerFormatRepair(null, 0, null, null);
        }
    }

    private Task ResetManagerFormatRepairStateAsync(ProjectRun run, CancellationToken cancellationToken) =>
        _store.SaveCheckpointAsync(
            new DurableCheckpoint(
                ManagerFormatRepairCheckpointKey(run),
                run.Id.ToString(),
                "manager-format-repair-v1",
                JsonSerializer.Serialize(new DurableManagerFormatRepair(null, 0, null, null)),
                DateTimeOffset.UtcNow),
            cancellationToken);

    private async Task<bool> TryRepairManagerResponseFormatAsync(
        ProjectRun run,
        LogicalAgentId managerAgentId,
        BrowserRuntimeRecord runtime,
        ChatGptSemanticSnapshot semantic,
        ManagerPlanParseResult parsed,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(semantic.CapturedResponseText))
            return false;
        if (string.IsNullOrWhiteSpace(runtime.ConversationIdentity) || string.IsNullOrWhiteSpace(runtime.ProviderConversationIdentity))
            return false;

        var rejectedResponseHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(semantic.CapturedResponseText))).ToLowerInvariant();
        var repairState = await LoadManagerFormatRepairStateAsync(run, cancellationToken).ConfigureAwait(false);
        if (!ManagerPlanningPromptBuilder.CanSubmitOrReconcileFormatRepair(repairState.AttemptsUsed, repairState.RejectedResponseHash, rejectedResponseHash))
            return false;

        var baseline = _managerBaseline ?? throw new InvalidOperationException("Manager planning baseline is unavailable for structured-response repair.");
        var repairPrompt = ManagerPlanningPromptBuilder.BuildFormatRepair(rejectedResponseHash, parsed.Findings, baseline);
        var repairHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(repairPrompt))).ToLowerInvariant();
        var request = new AgentRequest(
            run.Id,
            managerAgentId,
            new ConversationId(Guid.Parse(runtime.ConversationIdentity)),
            DispatchId.New(),
            repairPrompt,
            repairHash,
            null,
            null,
            null,
            runtime.ProviderConversationIdentity);
        var result = await _agentProvider.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (repairState.AttemptsUsed == 0)
        {
            await _store.SaveCheckpointAsync(
                new DurableCheckpoint(
                    ManagerFormatRepairCheckpointKey(run),
                    run.Id.ToString(),
                    "manager-format-repair-v1",
                    JsonSerializer.Serialize(new DurableManagerFormatRepair(rejectedResponseHash, 1, repairHash, DateTimeOffset.UtcNow)),
                    DateTimeOffset.UtcNow),
                cancellationToken).ConfigureAwait(false);
        }

        CaptureProviderAttention(result.ErrorCode, runtime.RuntimeId, "Manager ChatGPT session");
        if (!result.Accepted && !result.IsUncertain)
            throw new InvalidOperationException($"Manager structured-response repair send stopped safely: {result.ErrorCode ?? result.ProviderEvidence ?? "unknown provider state"}.");

        _autopilot = "PLANNING";
        _latestManagerHandoff = result.IsUncertain
            ? $"REPAIRING_MANAGER_FORMAT — the bounded JSON-only correction dispatch {result.DispatchId} is uncertain; PCC is reconciling it safely and will not duplicate the physical send."
            : $"REPAIRING_MANAGER_FORMAT — Manager returned an unstructured response. PCC submitted one bounded JSON-only correction automatically ({result.DispatchId}) and is waiting for the corrected response.";
        _recovery.Insert(0, new RecoveryEventSummary(DateTimeOffset.UtcNow, "REPAIRING_MANAGER_FORMAT", $"rejected={rejectedResponseHash};repair={repairHash};accepted={result.Accepted};uncertain={result.IsUncertain}", true));
        await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (_settings.AutoResume) EnsureAutopilotLoop();
        return true;
    }

    private async Task ReconcileManagerResponseAsync
'@

$text = $methodRegex.Replace($text, [System.Text.RegularExpressions.MatchEvaluator]{ param($match) $replacement }, 1)

$baselineNeedle = '        _managerBaseline = baseline.Value;'
$baselineReplacement = @'
        _managerBaseline = baseline.Value;
        await ResetManagerFormatRepairStateAsync(run, cancellationToken).ConfigureAwait(false);
'@
if (-not $text.Contains($baselineNeedle)) {
    throw 'Manager baseline assignment was not found.'
}
$text = $text.Replace($baselineNeedle, $baselineReplacement.TrimEnd())

$parseNeedle = @'
        var parsed = new StructuredManagerPlanParser().Parse(semantic.CapturedResponseText);
        if (!parsed.IsValid || parsed.Plan is null)
            throw new InvalidOperationException($"Manager response rejected: {string.Join("; ", parsed.Findings.Select(x => $"{x.Code}:{x.Message}"))}");
        var planFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", parsed.Plan.Tasks.Select(x => x.Task.Fingerprint))))).ToLowerInvariant();
'@
$parseReplacement = @'
        var parsed = new StructuredManagerPlanParser().Parse(semantic.CapturedResponseText);
        if (!parsed.IsValid || parsed.Plan is null)
        {
            if (await TryRepairManagerResponseFormatAsync(run, managerAgentId, runtime, semantic, parsed, cancellationToken).ConfigureAwait(false))
                return;
            throw new InvalidOperationException($"Manager response rejected after bounded automatic format repair: {string.Join("; ", parsed.Findings.Select(x => $"{x.Code}:{x.Message}"))}");
        }
        await ResetManagerFormatRepairStateAsync(run, cancellationToken).ConfigureAwait(false);
        var planFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", parsed.Plan.Tasks.Select(x => x.Task.Fingerprint))))).ToLowerInvariant();
'@
if (-not $text.Contains($parseNeedle.Trim())) {
    throw 'Manager parse/reject block was not found.'
}
$text = $text.Replace($parseNeedle.Trim(), $parseReplacement.Trim())

$zeroTaskNeedle = @'
            throw new InvalidOperationException("A zero-task Manager response must request CLOSE with 99% evidence-backed completion, or identify a real blocker.");
'@
$zeroTaskReplacement = @'
            if (string.Equals(parsed.Plan.ProjectDecision, "BLOCKED", StringComparison.OrdinalIgnoreCase) && parsed.Plan.KnownBlockers.Count > 0)
            {
                _run = run with { State = ProjectRunState.BlockedExternal, ManagerEstimate = parsed.Plan.ManagerEstimate, CompletionMode = ProjectCompletionMode.Blocked };
                _currentPlan = parsed.Plan;
                _currentWave = _currentWave is null ? null : _currentWave with { State = WaveState.Blocked };
                await _store.SaveCheckpointAsync(new DurableCheckpoint($"manager-plan:{run.Id}", run.Id.ToString(), "structured-manager-plan-v1", semantic.CapturedResponseText, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
                await _orchestrationStore.SaveAsync(new OrchestrationRecoverySnapshot(_run, _currentWave, _runtimeTasks, _assignments, [], null, OrchestrationPhase.BlockedExternal, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
                _autopilot = "BLOCKED_EXTERNAL";
                _runtimeErrorFingerprint = null;
                _runtimeErrorCount = 0;
                await PersistLoopGuardAsync(false, cancellationToken).ConfigureAwait(false);
                _latestManagerHandoff = $"BLOCKED_EXTERNAL — Manager supplied a valid structured blocker response: {string.Join("; ", parsed.Plan.KnownBlockers)}";
                await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            throw new InvalidOperationException("A zero-task Manager response must request CLOSE with 99% evidence-backed completion or ProjectDecision BLOCKED with concrete KnownBlockers.");
'@
if (-not $text.Contains($zeroTaskNeedle.Trim())) {
    throw 'Zero-task Manager response boundary was not found.'
}
$text = $text.Replace($zeroTaskNeedle.Trim(), $zeroTaskReplacement.Trim())

Set-Content -LiteralPath $path -Value $text -Encoding utf8NoBOM
Write-Host 'Manager runtime now sends evidence-complete prompts, performs one bounded durable JSON-only repair, and accepts structured external blockers.'
