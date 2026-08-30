[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$gatewayPath = Join-Path $repoRoot 'src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs'
$text = (Get-Content $gatewayPath -Raw).Replace("`r`n", "`n")

$methodAnchor = '    private async Task<bool> TryRepairManagerResponseFormatAsync('
$sendAnchor = '        var result = await _agentProvider.SendAsync(request, cancellationToken).ConfigureAwait(false);'
$handoffAnchor = '        _latestManagerHandoff = result.IsUncertain'
$nextMethodAnchor = '    private async Task ReconcileManagerResponseAsync('

$methodIndex = $text.IndexOf($methodAnchor, [StringComparison]::Ordinal)
if ($methodIndex -lt 0) { throw 'PATCH_CONTRACT_MISMATCH: TryRepairManagerResponseFormatAsync anchor not found.' }
$nextMethodIndex = $text.IndexOf($nextMethodAnchor, $methodIndex, [StringComparison]::Ordinal)
if ($nextMethodIndex -lt 0) { throw 'PATCH_CONTRACT_MISMATCH: ReconcileManagerResponseAsync boundary not found.' }
$sendIndex = $text.IndexOf($sendAnchor, $methodIndex, [StringComparison]::Ordinal)
if ($sendIndex -lt 0 -or $sendIndex -ge $nextMethodIndex) { throw 'PATCH_CONTRACT_MISMATCH: repair send anchor not found inside TryRepairManagerResponseFormatAsync.' }
$handoffIndex = $text.IndexOf($handoffAnchor, $sendIndex, [StringComparison]::Ordinal)
if ($handoffIndex -lt 0 -or $handoffIndex -ge $nextMethodIndex) { throw 'PATCH_CONTRACT_MISMATCH: repair handoff anchor not found inside TryRepairManagerResponseFormatAsync.' }

$existing = $text.Substring($sendIndex, $handoffIndex - $sendIndex)
if ($existing.Contains('ManagerSendRecoveryAction.BrowserAdapterReprobe', [StringComparison]::Ordinal)) {
    throw 'PATCH_CONTRACT_MISMATCH: Browser adapter repair recovery block is already present; build-time patch must remain single-application.'
}
if (-not $existing.Contains('Manager structured-response repair send stopped safely:', [StringComparison]::Ordinal)) {
    throw 'PATCH_CONTRACT_MISMATCH: expected terminal Manager repair send-stop branch is absent from structural replacement window.'
}

$replacement = @'
        var result = await _agentProvider.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var managerSendRecovery = ManagerSendRecoveryPolicy.Classify(result.ErrorCode, result.ProviderEvidence);

        CaptureProviderAttention(result.ErrorCode, runtime.RuntimeId, "Manager ChatGPT session");
        if (!result.Accepted && !result.IsUncertain)
        {
            if (managerSendRecovery == ManagerSendRecoveryAction.BrowserAdapterReprobe)
            {
                // The physical send is proven NOT to have occurred. Do not consume the one
                // bounded repair attempt; keep the same durable dispatch/content hash and
                // re-probe live browser semantics until input/auth/conversation/health are safe.
                _autopilot = "PLANNING";
                _runtimeErrorFingerprint = null;
                _runtimeErrorCount = 0;
                await PersistLoopGuardAsync(false, cancellationToken).ConfigureAwait(false);
                _latestManagerHandoff = $"RECOVERING_BROWSER_EVIDENCE — Manager repair dispatch {result.DispatchId} was not submitted because browser semantics are not yet proven safe ({result.ErrorCode ?? result.ProviderEvidence ?? "BROWSER_ADAPTER_UNCERTAIN"}). PCC will re-probe automatically using the same durable dispatch; the bounded repair attempt is not consumed and no duplicate physical send is permitted.";
                _recovery.Insert(0, new RecoveryEventSummary(DateTimeOffset.UtcNow, "RECOVERING_BROWSER_EVIDENCE", _latestManagerHandoff, true));
                await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
                if (_settings.AutoResume) EnsureAutopilotLoop();
                return true;
            }

            if (managerSendRecovery == ManagerSendRecoveryAction.GlobalRateLimitCooldown)
            {
                var rateLimit = new ResilienceDecision(ChatGptResilienceState.RateLimited, FaultScope.Global, true, false, "RATE_LIMITED");
                await PersistGlobalHealthPauseAsync(rateLimit, runtime.RuntimeId, cancellationToken).ConfigureAwait(false);
                _runtimeErrorFingerprint = null;
                _runtimeErrorCount = 0;
                await PersistLoopGuardAsync(false, cancellationToken).ConfigureAwait(false);
                _latestManagerHandoff = "RATE LIMIT DETECTED — Manager repair was not submitted. PCC will re-probe after cooldown using the same durable repair dispatch; the bounded repair attempt is not consumed.";
                _recovery.Insert(0, new RecoveryEventSummary(DateTimeOffset.UtcNow, "RATE_LIMITED", result.ErrorCode ?? result.ProviderEvidence ?? "RATE_LIMITED", true));
                await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
                if (_settings.AutoResume) EnsureAutopilotLoop();
                return true;
            }

            throw new InvalidOperationException($"Manager structured-response repair send stopped safely: {result.ErrorCode ?? result.ProviderEvidence ?? "unknown provider state"}.");
        }

        // A repair attempt is consumed only after a proven submission or a genuinely
        // submitted-unknown result. Safe pre-send semantic uncertainty remains retryable.
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

        _autopilot = "PLANNING";
'@

$text = $text.Substring(0, $sendIndex) + $replacement + "`n" + $text.Substring($handoffIndex)
Set-Content -Path $gatewayPath -Value $text -Encoding utf8 -NoNewline
Write-Host 'PATCHED: Treat BROWSER_ADAPTER_UNCERTAIN during Manager repair as retryable pre-send evidence gap'
Write-Host 'BROWSER_ADAPTER_UNCERTAIN_REPAIR_FIX_APPLIED'
