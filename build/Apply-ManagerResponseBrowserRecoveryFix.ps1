[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$gatewayPath = Join-Path $repoRoot 'src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs'
$text = (Get-Content $gatewayPath -Raw).Replace("`r`n", "`n")

$methodAnchor = '    private async Task ReconcileManagerResponseAsync(CancellationToken cancellationToken)'
$methodIndex = $text.IndexOf($methodAnchor, [StringComparison]::Ordinal)
if ($methodIndex -lt 0) { throw 'PATCH_CONTRACT_MISMATCH: ReconcileManagerResponseAsync not found.' }

$managerLine = '        var managerAgentId = _managerAgentId ?? throw new InvalidOperationException("Manager logical identity is not initialized.");'
$managerIndex = $text.IndexOf($managerLine, $methodIndex, [StringComparison]::Ordinal)
if ($managerIndex -lt 0) { throw 'PATCH_CONTRACT_MISMATCH: Manager identity line not found inside ReconcileManagerResponseAsync.' }

$runtimeLine = '        var runtime = (await _runtimeRegistry.ListAsync(cancellationToken).ConfigureAwait(false))'
$runtimeIndex = $text.IndexOf($runtimeLine, $managerIndex, [StringComparison]::Ordinal)
if ($runtimeIndex -lt 0) { throw 'PATCH_CONTRACT_MISMATCH: Manager runtime lookup not found inside ReconcileManagerResponseAsync.' }

$liveGateMarker = 'RECOVERING_MANAGER_BROWSER'
if ($text.IndexOf($liveGateMarker, $methodIndex, [StringComparison]::Ordinal) -ge 0) {
    throw 'PATCH_CONTRACT_MISMATCH: Manager response browser recovery patch is already present.'
}

$insertAt = $managerIndex + $managerLine.Length
$liveGate = @'

        // Response reconciliation must never inspect a persisted Browser runtime merely because
        // its logical binding still exists. Re-establish a live PCC-owned DevTools/Playwright
        // connection first. If recovery is still pending, remain in PLANNING so the autopilot
        // continues reconciliation and can never fall back to StartManagerAsync / duplicate send.
        if (!await EnsureManagerChromeReadyAsync(cancellationToken).ConfigureAwait(false))
        {
            _autopilot = "PLANNING";
            _latestManagerHandoff = "RECOVERING_MANAGER_BROWSER — the accepted Manager dispatch is preserved, but live PCC-owned Chrome/Playwright readiness is not proven yet. PCC is recovering it automatically; no Manager prompt will be resent.";
            _recovery.Insert(0, new RecoveryEventSummary(DateTimeOffset.UtcNow, "RECOVERING_MANAGER_BROWSER", _latestManagerHandoff, true));
            await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
'@
$text = $text.Insert($insertAt, $liveGate)
Write-Host 'PATCHED: Manager response reconciliation re-proves live Chrome before inspecting response'

$methodIndex = $text.IndexOf($methodAnchor, [StringComparison]::Ordinal)
$semanticLine = '        var semantic = await _browserAdapter.InspectAsync(runtime, expected, cancellationToken).ConfigureAwait(false);'
$semanticIndex = $text.IndexOf($semanticLine, $methodIndex, [StringComparison]::Ordinal)
if ($semanticIndex -lt 0) { throw 'PATCH_CONTRACT_MISMATCH: semantic inspection line not found inside ReconcileManagerResponseAsync.' }
$resilienceLine = '        var resilience = new ChatGptResilienceClassifier().Classify(semantic, DateTimeOffset.UtcNow - runtime.LastActivityAt);'
$resilienceIndex = $text.IndexOf($resilienceLine, $semanticIndex, [StringComparison]::Ordinal)
if ($resilienceIndex -lt 0) { throw 'PATCH_CONTRACT_MISMATCH: resilience classification line not found after semantic inspection.' }

$semanticGap = @'

        var semanticBrowserEvidenceMissing =
            semantic.Input.State == InputState.Unknown &&
            semantic.Generation.State == GenerationState.Unknown &&
            semantic.Auth.State == AuthState.Unknown &&
            semantic.Conversation.State == ConversationMatch.Unknown &&
            semantic.Health.State == PageHealth.Unknown &&
            semantic.ResponseCompleteness == ResponseCompleteness.Unknown &&
            semantic.AssistantMessageCount == 0 &&
            string.IsNullOrWhiteSpace(semantic.CapturedResponseText);
        if (semanticBrowserEvidenceMissing)
        {
            // This is the adapter's page-missing / inspection-unavailable shape, not evidence that
            // the Manager has failed to answer. Force the next loop through the live Chrome probe
            // and preserve the already accepted dispatch without consuming repair/LoopGuard state.
            _nextChromeRecoveryRetryAt = DateTimeOffset.MinValue;
            _autopilot = "PLANNING";
            _runtimeErrorFingerprint = null;
            _runtimeErrorCount = 0;
            await PersistLoopGuardAsync(false, cancellationToken).ConfigureAwait(false);
            _latestManagerHandoff = "RECOVERING_MANAGER_BROWSER — the Manager conversation is durably bound, but the live Playwright page is unavailable. PCC will reconnect/recover Chrome and continue reading the same response; no resend is authorized.";
            _recovery.Insert(0, new RecoveryEventSummary(DateTimeOffset.UtcNow, "RECOVERING_MANAGER_BROWSER", _latestManagerHandoff, true));
            await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
'@
$text = $text.Insert($resilienceIndex, $semanticGap)
Write-Host 'PATCHED: all-Unknown Manager semantic snapshot triggers Browser recovery instead of endless response polling'

Set-Content -Path $gatewayPath -Value $text -Encoding utf8 -NoNewline
Write-Host 'MANAGER_RESPONSE_BROWSER_RECOVERY_FIX_APPLIED'
