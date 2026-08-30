[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

function ReadN([string]$p) { (Get-Content $p -Raw).Replace("`r`n", "`n") }
function ReplaceOne([string]$rel,[string]$old,[string]$new,[string]$desc) {
    $p=Join-Path $repoRoot $rel; $t=ReadN $p
    $n=([regex]::Matches($t,[regex]::Escape($old))).Count
    if($n-ne 1){throw "PATCH_CONTRACT_MISMATCH: $desc expected 1, found $n"}
    Set-Content $p $t.Replace($old,$new) -Encoding utf8 -NoNewline
    Write-Host "PATCHED: $desc"
}

$runtime='src/PCCExecutive.Browser/PlaywrightChromeRuntimeHost.cs'
$gateway='src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs'

$old='        if (_connections.TryGetValue(runtime.RuntimeId, out var existing) && !existing.Process.HasExited) return true;'
$new=@'
        if (_connections.TryGetValue(runtime.RuntimeId, out var existing) && !existing.Process.HasExited)
        {
            try
            {
                var existingPages = existing.Browser.Contexts.SelectMany(x => x.Pages).Where(x => !x.IsClosed).ToArray();
                var existingPageIndex = ChatGptPageSelectionPolicy.SelectForRecovery(
                    existingPages.Select(x => x.Url).ToArray(), runtime.ProviderConversationIdentity);
                if (existingPageIndex >= 0)
                {
                    _connections[runtime.RuntimeId] = existing with { Page = existingPages[existingPageIndex] };
                    return true;
                }
            }
            catch (PlaywrightException)
            {
                // A live Chrome PID does not prove that the Playwright/CDP page graph is usable.
            }
            _connections.TryRemove(runtime.RuntimeId, out _);
        }
'@
ReplaceOne $runtime $old $new 'RecoverAsync verifies a usable Playwright page'

$old='        startInfo.ArgumentList.Add("https://chatgpt.com/");'
$new=@'
        var boundProviderIdentity = !string.IsNullOrWhiteSpace(request.ProviderConversationIdentity) &&
            !string.Equals(request.ProviderConversationIdentity, "NEW", StringComparison.OrdinalIgnoreCase)
            ? request.ProviderConversationIdentity
            : null;
        var launchUrl = boundProviderIdentity is null
            ? "https://chatgpt.com/"
            : $"https://chatgpt.com/c/{Uri.EscapeDataString(boundProviderIdentity)}";
        startInfo.ArgumentList.Add(launchUrl);
'@
ReplaceOne $runtime $old $new 'Replacement Chrome targets durable Manager conversation'

ReplaceOne $runtime `
'            var launchPageIndex = ChatGptPageSelectionPolicy.SelectForLaunch(launchPages.Select(x => x.Url).ToArray());' `
@'
            var launchPageIndex = boundProviderIdentity is null
                ? ChatGptPageSelectionPolicy.SelectForLaunch(launchPages.Select(x => x.Url).ToArray())
                : ChatGptPageSelectionPolicy.SelectForRecovery(launchPages.Select(x => x.Url).ToArray(), boundProviderIdentity);
'@ `
'Launch page selection honors durable conversation'

ReplaceOne $runtime `
'                await page.GotoAsync("https://chatgpt.com/", new PageGotoOptions' `
'                await page.GotoAsync(launchUrl, new PageGotoOptions' `
'New launch page navigates to durable conversation'

# Remove only the V5 unconditional Manager-response readiness gate. It is the source
# of the diagnostic's three-second PLANNING/RECOVERING oscillation because the older
# live-readiness patch makes EnsureManagerChromeReadyAsync probe on every call.
$p=Join-Path $repoRoot $gateway; $t=ReadN $p
$anchor='    private async Task ReconcileManagerResponseAsync(CancellationToken cancellationToken)'
$m=$t.IndexOf($anchor,[StringComparison]::Ordinal); if($m-lt 0){throw 'ReconcileManagerResponseAsync missing'}
$comment='        // Response reconciliation must never inspect a persisted Browser runtime merely because'
$s=$t.IndexOf($comment,$m,[StringComparison]::Ordinal); if($s-lt 0){throw 'V5 response gate start missing'}
# Include the blank line immediately before the V5 comment when present.
if($s-gt 0 -and $t[$s-1]-eq "`n"){$s--}
$runtimeLine='        var runtime = (await _runtimeRegistry.ListAsync(cancellationToken).ConfigureAwait(false))'
$e=$t.IndexOf($runtimeLine,$s,[StringComparison]::Ordinal); if($e-lt 0){throw 'Runtime lookup after V5 gate missing'}
$t=$t.Remove($s,$e-$s)
Set-Content $p $t -Encoding utf8 -NoNewline
Write-Host 'PATCHED: Manager response poll no longer forces Chrome recovery every cycle'

# Replace the V5 all-Unknown block using structural anchors rather than formatting.
$t=ReadN $p; $m=$t.IndexOf($anchor,[StringComparison]::Ordinal)
$ifLine='        if (semanticBrowserEvidenceMissing)'
$s=$t.IndexOf($ifLine,$m,[StringComparison]::Ordinal); if($s-lt 0){throw 'semanticBrowserEvidenceMissing block missing'}
$resLine='        var resilience = new ChatGptResilienceClassifier().Classify(semantic, DateTimeOffset.UtcNow - runtime.LastActivityAt);'
$e=$t.IndexOf($resLine,$s,[StringComparison]::Ordinal); if($e-lt 0){throw 'resilience line after semantic gap missing'}
$newBlock=@'
        if (semanticBrowserEvidenceMissing)
        {
            _runtimeErrorFingerprint = null;
            _runtimeErrorCount = 0;
            await PersistLoopGuardAsync(false, cancellationToken).ConfigureAwait(false);

            if (DateTimeOffset.UtcNow < _nextChromeRecoveryRetryAt)
            {
                _autopilot = "PLANNING";
                _latestManagerHandoff = "RECOVERING_MANAGER_BROWSER — live Browser evidence is unavailable. PCC is waiting for the bounded automatic retry while preserving the accepted Manager dispatch; no resend is authorized.";
                await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            _autopilot = "RECOVERING";
            _latestManagerHandoff = "RECOVERING_MANAGER_BROWSER — Playwright page evidence disappeared. PCC is reconnecting the PCC-owned runtime while preserving the current Manager dispatch and conversation.";
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
$t=$t.Remove($s,$e-$s).Insert($s,$newBlock)
Set-Content $p $t -Encoding utf8 -NoNewline
Write-Host 'PATCHED: all-Unknown Manager evidence performs bounded Browser recovery'

# Compile-time transformation assertions.
$rt=ReadN (Join-Path $repoRoot $runtime)
if($rt.Contains('if (_connections.TryGetValue(runtime.RuntimeId, out var existing) && !existing.Process.HasExited) return true;',[StringComparison]::Ordinal)){throw 'V6C blind PID success remains'}
if(-not $rt.Contains('existingPageIndex >= 0',[StringComparison]::Ordinal)){throw 'V6C page proof missing'}
if(-not $rt.Contains('GotoAsync(launchUrl',[StringComparison]::Ordinal)){throw 'V6C durable launch missing'}
$gt=ReadN $p
if($gt.Contains($comment,[StringComparison]::Ordinal)){throw 'V6C unconditional V5 gate remains'}
if(-not $gt.Contains('var browserRecovery = await _sessions.RecoverOrphanAsync(runtime.RuntimeId',[StringComparison]::Ordinal)){throw 'V6C evidence-led recovery missing'}
Write-Host 'MANAGER_BROWSER_LIVENESS_V6C_FIX_APPLIED'
