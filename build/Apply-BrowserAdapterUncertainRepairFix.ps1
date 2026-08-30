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

$policy = 'src/PCCExecutive.App/Presentation/ManagerSendRecoveryPolicy.cs'
$gateway = 'src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs'
$policyTests = 'tests/PCCExecutive.App.Tests/ManagerSendRecoveryPolicyTests.cs'
$repairTests = 'tests/PCCExecutive.App.Tests/ManagerFormatRepairRecoveryContractTests.cs'

$oldPolicy = @'
public enum ManagerSendRecoveryAction
{
    None,
    GlobalRateLimitCooldown
}

public static class ManagerSendRecoveryPolicy
{
    public static ManagerSendRecoveryAction Classify(string? errorCode, string? providerEvidence = null)
    {
        var normalized = string.Concat(errorCode, " ", providerEvidence)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal);

        return normalized.Contains("RATELIMIT", StringComparison.OrdinalIgnoreCase)
            ? ManagerSendRecoveryAction.GlobalRateLimitCooldown
            : ManagerSendRecoveryAction.None;
    }
}
'@

$newPolicy = @'
public enum ManagerSendRecoveryAction
{
    None,
    GlobalRateLimitCooldown,
    BrowserAdapterReprobe
}

public static class ManagerSendRecoveryPolicy
{
    public static ManagerSendRecoveryAction Classify(string? errorCode, string? providerEvidence = null)
    {
        var normalized = string.Concat(errorCode, " ", providerEvidence)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal);

        if (normalized.Contains("RATELIMIT", StringComparison.OrdinalIgnoreCase))
            return ManagerSendRecoveryAction.GlobalRateLimitCooldown;
        if (normalized.Contains("BROWSERADAPTERUNCERTAIN", StringComparison.OrdinalIgnoreCase))
            return ManagerSendRecoveryAction.BrowserAdapterReprobe;
        return ManagerSendRecoveryAction.None;
    }
}
'@

Replace-RequiredLiteral -RelativePath $policy `
    -Old $oldPolicy `
    -New $newPolicy `
    -Description 'Classify Browser adapter uncertainty as safe semantic re-probe rather than terminal send failure'

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

$oldPolicyTestTail = @'
    [Theory]
    [InlineData("WRONG_CONVERSATION_BINDING")]
    [InlineData("GLOBAL_SEND_PAUSED")]
    [InlineData("BROWSER_RUNTIME_NOT_BOUND")]
    public void Non_rate_limit_send_stop_keeps_existing_handling(string errorCode)
'@

$newPolicyTestTail = @'
    [Theory]
    [InlineData("BROWSER_ADAPTER_UNCERTAIN")]
    [InlineData("browser-adapter-uncertain")]
    public void Browser_adapter_uncertainty_requests_safe_semantic_reprobe(string errorCode)
    {
        Assert.Equal(
            ManagerSendRecoveryAction.BrowserAdapterReprobe,
            ManagerSendRecoveryPolicy.Classify(errorCode));
    }

    [Fact]
    public void Browser_adapter_uncertainty_can_be_detected_from_provider_evidence()
    {
        Assert.Equal(
            ManagerSendRecoveryAction.BrowserAdapterReprobe,
            ManagerSendRecoveryPolicy.Classify(null, "guard:BROWSER_ADAPTER_UNCERTAIN"));
    }

    [Theory]
    [InlineData("WRONG_CONVERSATION_BINDING")]
    [InlineData("GLOBAL_SEND_PAUSED")]
    [InlineData("BROWSER_RUNTIME_NOT_BOUND")]
    public void Non_rate_limit_send_stop_keeps_existing_handling(string errorCode)
'@

Replace-RequiredLiteral -RelativePath $policyTests `
    -Old $oldPolicyTestTail `
    -New $newPolicyTestTail `
    -Description 'Cover Browser adapter uncertainty recovery classification'

$oldRepairAssertions = @'
        Assert.Contains("CanSubmitOrReconcileFormatRepair", source, StringComparison.Ordinal);
        Assert.Contains("REPAIRING_MANAGER_FORMAT", source, StringComparison.Ordinal);
'@

$newRepairAssertions = @'
        Assert.Contains("CanSubmitOrReconcileFormatRepair", source, StringComparison.Ordinal);
        Assert.Contains("REPAIRING_MANAGER_FORMAT", source, StringComparison.Ordinal);
        Assert.Contains("ManagerSendRecoveryAction.BrowserAdapterReprobe", source, StringComparison.Ordinal);
        Assert.Contains("RECOVERING_BROWSER_EVIDENCE", source, StringComparison.Ordinal);
        Assert.Contains("the bounded repair attempt is not consumed", source, StringComparison.OrdinalIgnoreCase);
'@

Replace-RequiredLiteral -RelativePath $repairTests `
    -Old $oldRepairAssertions `
    -New $newRepairAssertions `
    -Description 'Contract-test retryable pre-send Browser adapter uncertainty during Manager repair'

Write-Host 'BROWSER_ADAPTER_UNCERTAIN_REPAIR_FIX_APPLIED'
