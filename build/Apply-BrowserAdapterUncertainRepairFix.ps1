[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

function Read-NormalizedText([string]$Path) {
    return (Get-Content $Path -Raw).Replace("`r`n", "`n")
}

function Replace-RequiredLiteral {
    param(
        [Parameter(Mandatory)][string]$RelativePath,
        [Parameter(Mandatory)][AllowEmptyString()][string]$Old,
        [Parameter(Mandatory)][AllowEmptyString()][string]$New,
        [Parameter(Mandatory)][string]$Description
    )
    $path = Join-Path $repoRoot $RelativePath
    $text = Read-NormalizedText $path
    $count = ([regex]::Matches($text, [regex]::Escape($Old))).Count
    if ($count -ne 1) { throw "PATCH_CONTRACT_MISMATCH: $Description expected exactly one literal match in $RelativePath, found $count." }
    Set-Content -Path $path -Value $text.Replace($Old, $New) -Encoding utf8 -NoNewline
    Write-Host "PATCHED: $Description"
}

$gateway = 'src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs'

$oldRepairDecision = @'
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
'@

$newRepairDecision = @'
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

Replace-RequiredLiteral -RelativePath $gateway `
    -Old $oldRepairDecision `
    -New $newRepairDecision `
    -Description 'Treat BROWSER_ADAPTER_UNCERTAIN during Manager repair as retryable pre-send evidence gap'

Write-Host 'BROWSER_ADAPTER_UNCERTAIN_REPAIR_FIX_APPLIED'
