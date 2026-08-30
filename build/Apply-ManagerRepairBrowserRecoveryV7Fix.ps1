[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$gatewayPath = Join-Path $repoRoot 'src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs'
$adapterPath = Join-Path $repoRoot 'src/PCCExecutive.Browser/ChatGptBrowserAdapter.cs'

function Read-Normalized([string]$path) {
    (Get-Content $path -Raw).Replace("`r`n", "`n")
}

# 1) Keep the browser adapter aligned with the stable ChatGPT composer contracts used by the
# current web UI. This only broadens positive composer discovery; all existing auth,
# conversation, health, generation and final-enter safety gates remain mandatory.
$adapter = Read-Normalized $adapterPath
$oldComposer = '    private const string ComposerSelector = "textarea, [contenteditable=''true''][role=''textbox''], [contenteditable=''true''][data-lexical-editor=''true''], [data-testid=''composer-text-input'']";'
$newComposer = '    private const string ComposerSelector = "#prompt-textarea, textarea[name=''prompt-textarea''], [data-testid=''prompt-textarea''], textarea, [contenteditable=''true''][role=''textbox''], [contenteditable=''true''][data-lexical-editor=''true''], [data-testid=''composer-text-input''], .ProseMirror[contenteditable=''true'']";'
$composerCount = ([regex]::Matches($adapter, [regex]::Escape($oldComposer))).Count
if ($composerCount -ne 1) { throw "PATCH_CONTRACT_MISMATCH: ComposerSelector expected 1, found $composerCount" }
$adapter = $adapter.Replace($oldComposer, $newComposer)
Set-Content -Path $adapterPath -Value $adapter -Encoding utf8 -NoNewline
Write-Host 'PATCHED: stable current ChatGPT composer selectors added without weakening send gates'

# 2) V6 made Manager response *reading* recover a missing Playwright page, but the bounded
# Manager JSON-format repair path still only changed the UI to PLANNING when SendAsync returned
# BROWSER_ADAPTER_UNCERTAIN. That produced a safe-but-infinite three-second re-probe loop:
# the same durable dispatch was never physically submitted, yet nobody reattached CDP/Playwright.
# Replace that branch with an explicit bounded BrowserSessionController recovery.
$gateway = Read-Normalized $gatewayPath
$methodAnchor = '    private async Task<bool> TryRepairManagerResponseFormatAsync('
$methodIndex = $gateway.IndexOf($methodAnchor, [StringComparison]::Ordinal)
if ($methodIndex -lt 0) { throw 'PATCH_CONTRACT_MISMATCH: TryRepairManagerResponseFormatAsync not found.' }
$branchAnchor = '            if (managerSendRecovery == ManagerSendRecoveryAction.BrowserAdapterReprobe)'
$branchStart = $gateway.IndexOf($branchAnchor, $methodIndex, [StringComparison]::Ordinal)
if ($branchStart -lt 0) { throw 'PATCH_CONTRACT_MISMATCH: BrowserAdapterReprobe branch not found. Apply BrowserAdapterUncertainRepairFix before V7.' }
$nextAnchor = '            if (managerSendRecovery == ManagerSendRecoveryAction.GlobalRateLimitCooldown)'
$branchEnd = $gateway.IndexOf($nextAnchor, $branchStart, [StringComparison]::Ordinal)
if ($branchEnd -lt 0) { throw 'PATCH_CONTRACT_MISMATCH: GlobalRateLimitCooldown branch boundary not found.' }
$existingBranch = $gateway.Substring($branchStart, $branchEnd - $branchStart)
if ($existingBranch.Contains('RecoverOrphanAsync(runtime.RuntimeId', [StringComparison]::Ordinal)) {
    throw 'PATCH_CONTRACT_MISMATCH: Manager repair Browser recovery is already present.'
}

$replacement = @'
            if (managerSendRecovery == ManagerSendRecoveryAction.BrowserAdapterReprobe)
            {
                // The physical repair send is proven NOT to have occurred. Preserve the same
                // durable dispatch/content hash, but actively repair the PCC-owned browser graph
                // instead of merely polling the same missing/uncertain Playwright evidence forever.
                _runtimeErrorFingerprint = null;
                _runtimeErrorCount = 0;
                await PersistLoopGuardAsync(false, cancellationToken).ConfigureAwait(false);

                if (DateTimeOffset.UtcNow < _nextChromeRecoveryRetryAt)
                {
                    _autopilot = "PLANNING";
                    _latestManagerHandoff = $"RECOVERING_BROWSER_EVIDENCE — Manager repair dispatch {result.DispatchId} was not submitted. PCC is waiting for the bounded Browser recovery retry; the same durable dispatch is preserved and no duplicate physical send is permitted.";
                    await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
                    if (_settings.AutoResume) EnsureAutopilotLoop();
                    return true;
                }

                _autopilot = "RECOVERING";
                var uncertainEvidence = string.IsNullOrWhiteSpace(result.ProviderEvidence)
                    ? result.ErrorCode ?? "BROWSER_ADAPTER_UNCERTAIN"
                    : $"{result.ErrorCode ?? "BROWSER_ADAPTER_UNCERTAIN"}; {result.ProviderEvidence}";
                _latestManagerHandoff = $"RECOVERING_BROWSER_EVIDENCE — Manager repair dispatch {result.DispatchId} was NOT submitted because live browser semantics are unproven ({uncertainEvidence}). PCC is reconnecting/recovering the PCC-owned Chrome/Playwright runtime now; the bounded repair attempt is not consumed.";
                _recovery.Insert(0, new RecoveryEventSummary(DateTimeOffset.UtcNow, "RECOVERING_BROWSER_EVIDENCE", _latestManagerHandoff, true));
                await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);

                var repairBrowserRecovery = await _sessions.RecoverOrphanAsync(runtime.RuntimeId, cancellationToken).ConfigureAwait(false);
                if (repairBrowserRecovery.Succeeded)
                {
                    _nextChromeRecoveryRetryAt = DateTimeOffset.MinValue;
                    _autopilot = "PLANNING";
                    _latestManagerHandoff = $"MANAGER_REPAIR_BROWSER_RECOVERED — live PCC-owned Chrome/Playwright evidence was restored ({repairBrowserRecovery.RuntimeId}). PCC will retry the SAME durable repair dispatch on the next semantic pass; no physical send occurred before recovery.";
                    _recovery.Insert(0, new RecoveryEventSummary(DateTimeOffset.UtcNow, "MANAGER_REPAIR_BROWSER_RECOVERED", _latestManagerHandoff, true));
                }
                else
                {
                    _nextChromeRecoveryRetryAt = DateTimeOffset.UtcNow.AddSeconds(5);
                    _autopilot = "PLANNING";
                    _latestManagerHandoff = $"RECOVERING_BROWSER_EVIDENCE — Browser recovery is still pending ({repairBrowserRecovery.Reason}). Automatic retry in 5 seconds; Manager repair dispatch {result.DispatchId} remains preserved and unconsumed.";
                    _recovery.Insert(0, new RecoveryEventSummary(DateTimeOffset.UtcNow, "RECOVERING_BROWSER_EVIDENCE", _latestManagerHandoff, false));
                }

                await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
                if (_settings.AutoResume) EnsureAutopilotLoop();
                return true;
            }

'@
$gateway = $gateway.Remove($branchStart, $branchEnd - $branchStart).Insert($branchStart, $replacement)
Set-Content -Path $gatewayPath -Value $gateway -Encoding utf8 -NoNewline
Write-Host 'PATCHED: Manager format-repair Browser uncertainty now performs active bounded CDP/Playwright recovery'

# Build-time transformation assertions: V7 must prove both liveness and unchanged physical-send fences.
$finalGateway = Read-Normalized $gatewayPath
$finalAdapter = Read-Normalized $adapterPath
if (-not $finalGateway.Contains('var repairBrowserRecovery = await _sessions.RecoverOrphanAsync(runtime.RuntimeId', [StringComparison]::Ordinal)) {
    throw 'V7_ASSERTION_FAILED: active Manager repair Browser recovery missing.'
}
if (-not $finalGateway.Contains('the bounded repair attempt is not consumed', [StringComparison]::Ordinal)) {
    throw 'V7_ASSERTION_FAILED: durable repair-attempt preservation missing.'
}
if (-not $finalAdapter.Contains('#prompt-textarea', [StringComparison]::Ordinal)) {
    throw 'V7_ASSERTION_FAILED: current stable composer selector missing.'
}
if (-not $finalAdapter.Contains('var finalAuthorization = await authorizeBeforeEnter', [StringComparison]::Ordinal)) {
    throw 'V7_ASSERTION_FAILED: final pre-enter authorization fence was altered or removed.'
}
if (-not $finalAdapter.Contains('await composer.PressAsync("Enter")', [StringComparison]::Ordinal)) {
    throw 'V7_ASSERTION_FAILED: physical submit path unexpectedly changed.'
}
Write-Host 'MANAGER_REPAIR_BROWSER_RECOVERY_V7_FIX_APPLIED'
