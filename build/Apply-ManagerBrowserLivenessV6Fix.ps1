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
        [Parameter(Mandatory)][string]$Old,
        [Parameter(Mandatory)][string]$New,
        [Parameter(Mandatory)][string]$Description
    )
    $path = Join-Path $repoRoot $RelativePath
    $text = Read-NormalizedText $path
    $count = ([regex]::Matches($text, [regex]::Escape($Old))).Count
    if ($count -ne 1) { throw "PATCH_CONTRACT_MISMATCH: $Description expected exactly one literal match in $RelativePath, found $count." }
    Set-Content -Path $path -Value $text.Replace($Old, $New) -Encoding utf8 -NoNewline
    Write-Host "PATCHED: $Description"
}

# -----------------------------------------------------------------------------
# 1) Playwright runtime liveness: a live Chrome PID is NOT proof that the stored
#    Playwright Browser/Page objects are still usable. Re-select a live page or
#    drop the stale in-memory connection and reconnect over CDP.
# -----------------------------------------------------------------------------
$recoverOld = @'
        if (_connections.TryGetValue(runtime.RuntimeId, out var existing) && !existing.Process.HasExited) return true;
'@
$recoverNew = @'
        if (_connections.TryGetValue(runtime.RuntimeId, out var existing) && !existing.Process.HasExited)
        {
            try
            {
                var existingPages = existing.Browser.Contexts
                    .SelectMany(x => x.Pages)
                    .Where(x => !x.IsClosed)
                    .ToArray();
                var existingPageIndex = ChatGptPageSelectionPolicy.SelectForRecovery(
                    existingPages.Select(x => x.Url).ToArray(),
                    runtime.ProviderConversationIdentity);
                if (existingPageIndex >= 0)
                {
                    _connections[runtime.RuntimeId] = existing with { Page = existingPages[existingPageIndex] };
                    return true;
                }
            }
            catch (PlaywrightException)
            {
                // The process can remain alive after the Playwright/CDP object graph has gone
                // stale. Fall through to a fresh CDP reconnect instead of returning a false READY.
            }

            _connections.TryRemove(runtime.RuntimeId, out _);
        }
'@
Replace-RequiredLiteral -RelativePath 'src/PCCExecutive.Browser/PlaywrightChromeRuntimeHost.cs' -Old $recoverOld -New $recoverNew -Description 'RecoverAsync verifies a usable Playwright page instead of trusting a live PID'

# -----------------------------------------------------------------------------
# 2) When a durable conversation already exists and Chrome must be replaced,
#    launch the replacement directly at that exact conversation. This preserves
#    the accepted Manager dispatch and prevents recovery from landing on / root.
# -----------------------------------------------------------------------------
$launchUrlOld = @'
        startInfo.ArgumentList.Add("https://chatgpt.com/");
'@
$launchUrlNew = @'
        var boundProviderIdentity = !string.IsNullOrWhiteSpace(request.ProviderConversationIdentity) &&
            !string.Equals(request.ProviderConversationIdentity, "NEW", StringComparison.OrdinalIgnoreCase)
            ? request.ProviderConversationIdentity
            : null;
        var launchUrl = boundProviderIdentity is null
            ? "https://chatgpt.com/"
            : $"https://chatgpt.com/c/{Uri.EscapeDataString(boundProviderIdentity)}";
        startInfo.ArgumentList.Add(launchUrl);
'@
Replace-RequiredLiteral -RelativePath 'src/PCCExecutive.Browser/PlaywrightChromeRuntimeHost.cs' -Old $launchUrlOld -New $launchUrlNew -Description 'Replacement Chrome launches the durably-bound ChatGPT conversation'

$launchSelectOld = @'
            var launchPages = context.Pages.Where(x => !x.IsClosed).ToArray();
            var launchPageIndex = ChatGptPageSelectionPolicy.SelectForLaunch(launchPages.Select(x => x.Url).ToArray());
            IPage page;
            if (launchPageIndex >= 0)
            {
                page = launchPages[launchPageIndex];
            }
            else
            {
                page = await context.NewPageAsync().ConfigureAwait(false);
                await page.GotoAsync("https://chatgpt.com/", new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 20_000
                }).ConfigureAwait(false);
            }
'@
$launchSelectNew = @'
            var launchPages = context.Pages.Where(x => !x.IsClosed).ToArray();
            var launchPageIndex = boundProviderIdentity is null
                ? ChatGptPageSelectionPolicy.SelectForLaunch(launchPages.Select(x => x.Url).ToArray())
                : ChatGptPageSelectionPolicy.SelectForRecovery(launchPages.Select(x => x.Url).ToArray(), boundProviderIdentity);
            IPage page;
            if (launchPageIndex >= 0)
            {
                page = launchPages[launchPageIndex];
            }
            else
            {
                page = await context.NewPageAsync().ConfigureAwait(false);
                await page.GotoAsync(launchUrl, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 20_000
                }).ConfigureAwait(false);
            }
'@
Replace-RequiredLiteral -RelativePath 'src/PCCExecutive.Browser/PlaywrightChromeRuntimeHost.cs' -Old $launchSelectOld -New $launchSelectNew -Description 'Launch page selection honors an existing provider conversation identity'

# -----------------------------------------------------------------------------
# 3) V5 accidentally called EnsureManagerChromeReadyAsync on every response poll.
#    The earlier live-readiness patch intentionally forces that method to probe
#    every call, causing the observed PLANNING -> RECOVERING -> PLANNING loop.
#    Remove that unconditional response-poll gate. Recovery is now evidence-led:
#    only an all-Unknown semantic snapshot triggers one Browser recovery attempt.
# -----------------------------------------------------------------------------
$gatewayPath = 'src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs'
$v5Gate = @'

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
Replace-RequiredLiteral -RelativePath $gatewayPath -Old $v5Gate -New "" -Description 'Manager response polling no longer forces Chrome recovery every three seconds'

$v5SemanticBlock = @'
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
$v6SemanticBlock = @'
        if (semanticBrowserEvidenceMissing)
        {
            // All-Unknown means the adapter has no live page evidence. Recover only on this
            // evidence gap; do not run the expensive live-Chrome probe on every normal poll.
            // The already accepted Manager dispatch/conversation remains canonical throughout.
            _runtimeErrorFingerprint = null;
            _runtimeErrorCount = 0;
            await PersistLoopGuardAsync(false, cancellationToken).ConfigureAwait(false);

            if (DateTimeOffset.UtcNow < _nextChromeRecoveryRetryAt)
            {
                _autopilot = "PLANNING";
                _latestManagerHandoff = "RECOVERING_MANAGER_BROWSER — live Browser evidence is still unavailable. The accepted Manager dispatch is preserved; PCC is waiting for the bounded automatic recovery retry and will not resend the prompt.";
                await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            _autopilot = "RECOVERING";
            _latestManagerHandoff = "RECOVERING_MANAGER_BROWSER — Playwright page evidence disappeared. PCC is reconnecting the PCC-owned runtime now while preserving the existing Manager dispatch and conversation.";
            _recovery.Insert(0, new RecoveryEventSummary(DateTimeOffset.UtcNow, "RECOVERING_MANAGER_BROWSER", _latestManagerHandoff, true));
            await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);

            var browserRecovery = await _sessions.RecoverOrphanAsync(runtime.RuntimeId, cancellationToken).ConfigureAwait(false);
            if (browserRecovery.Succeeded)
            {
                _nextChromeRecoveryRetryAt = DateTimeOffset.MinValue;
                _autopilot = "PLANNING";
                _latestManagerHandoff = $"MANAGER_BROWSER_RECOVERED — live Chrome/Playwright evidence was restored ({browserRecovery.RuntimeId}). PCC will continue reading the same Manager response; no resend occurred.";
                _recovery.Insert(0, new RecoveryEventSummary(DateTimeOffset.UtcNow, "MANAGER_BROWSER_RECOVERED", _latestManagerHandoff, true));
            }
            else
            {
                _nextChromeRecoveryRetryAt = DateTimeOffset.UtcNow.AddSeconds(5);
                _autopilot = "PLANNING";
                _latestManagerHandoff = $"RECOVERING_MANAGER_BROWSER — Browser recovery is still pending ({browserRecovery.Reason}). Automatic retry in 5 seconds; the accepted Manager dispatch remains preserved.";
                _recovery.Insert(0, new RecoveryEventSummary(DateTimeOffset.UtcNow, "RECOVERING_MANAGER_BROWSER", _latestManagerHandoff, false));
            }

            await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
'@
Replace-RequiredLiteral -RelativePath $gatewayPath -Old $v5SemanticBlock -New $v6SemanticBlock -Description 'All-Unknown Manager evidence performs bounded real Browser recovery without duplicate send'

# Structural assertions for the exact transformed source used by the package.
$runtimeText = Read-NormalizedText (Join-Path $repoRoot 'src/PCCExecutive.Browser/PlaywrightChromeRuntimeHost.cs')
if ($runtimeText.Contains('if (_connections.TryGetValue(runtime.RuntimeId, out var existing) && !existing.Process.HasExited) return true;', [StringComparison]::Ordinal)) {
    throw 'V6_ASSERTION_FAILED: blind live-PID RecoverAsync success still exists.'
}
if (-not $runtimeText.Contains('existingPageIndex >= 0', [StringComparison]::Ordinal)) {
    throw 'V6_ASSERTION_FAILED: Playwright page liveness verification is missing.'
}
if (-not $runtimeText.Contains('Uri.EscapeDataString(boundProviderIdentity)', [StringComparison]::Ordinal)) {
    throw 'V6_ASSERTION_FAILED: durable conversation relaunch URL is missing.'
}

$gatewayText = Read-NormalizedText (Join-Path $repoRoot $gatewayPath)
if ($gatewayText.Contains('Response reconciliation must never inspect a persisted Browser runtime merely because', [StringComparison]::Ordinal)) {
    throw 'V6_ASSERTION_FAILED: V5 unconditional response-poll Chrome gate still exists.'
}
if (-not $gatewayText.Contains('var browserRecovery = await _sessions.RecoverOrphanAsync(runtime.RuntimeId', [StringComparison]::Ordinal)) {
    throw 'V6_ASSERTION_FAILED: evidence-led Manager Browser recovery is missing.'
}

Write-Host 'MANAGER_BROWSER_LIVENESS_V6_FIX_APPLIED'
