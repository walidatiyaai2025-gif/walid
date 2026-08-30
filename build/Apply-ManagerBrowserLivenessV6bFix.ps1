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

function Replace-RequiredRegex {
    param(
        [Parameter(Mandatory)][string]$RelativePath,
        [Parameter(Mandatory)][string]$Pattern,
        [Parameter(Mandatory)][string]$Replacement,
        [Parameter(Mandatory)][string]$Description
    )
    $path = Join-Path $repoRoot $RelativePath
    $text = Read-NormalizedText $path
    $regex = [regex]::new($Pattern, [System.Text.RegularExpressions.RegexOptions]::Singleline)
    $matches = $regex.Matches($text)
    if ($matches.Count -ne 1) { throw "PATCH_CONTRACT_MISMATCH: $Description expected exactly one regex match in $RelativePath, found $($matches.Count)." }
    $newText = $regex.Replace($text, [System.Text.RegularExpressions.MatchEvaluator]{ param($m) $Replacement }, 1)
    Set-Content -Path $path -Value $newText -Encoding utf8 -NoNewline
    Write-Host "PATCHED: $Description"
}

$runtimePath = 'src/PCCExecutive.Browser/PlaywrightChromeRuntimeHost.cs'
$gatewayPath = 'src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs'

# A live Chrome PID is not sufficient liveness evidence. Verify that the existing
# Playwright/CDP object graph still exposes the expected live ChatGPT page; when it
# does not, forget the stale in-memory connection and reconnect over CDP.
$recoverOld = '        if (_connections.TryGetValue(runtime.RuntimeId, out var existing) && !existing.Process.HasExited) return true;'
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
                // Chrome can remain alive while the Playwright/CDP object graph is stale.
                // Fall through to a fresh CDP reconnect rather than returning a false READY.
            }

            _connections.TryRemove(runtime.RuntimeId, out _);
        }
'@
Replace-RequiredLiteral -RelativePath $runtimePath -Old $recoverOld -New $recoverNew -Description 'RecoverAsync proves a usable Playwright page before returning READY'

# If a durable provider conversation already exists, a replacement Chrome must
# reopen that exact conversation. Otherwise recovery can land on / and lose the
# Manager response while still preserving the old binding in SQLite.
$launchRoot = '        startInfo.ArgumentList.Add("https://chatgpt.com/");'
$launchBound = @'
        var boundProviderIdentity = !string.IsNullOrWhiteSpace(request.ProviderConversationIdentity) &&
            !string.Equals(request.ProviderConversationIdentity, "NEW", StringComparison.OrdinalIgnoreCase)
            ? request.ProviderConversationIdentity
            : null;
        var launchUrl = boundProviderIdentity is null
            ? "https://chatgpt.com/"
            : $"https://chatgpt.com/c/{Uri.EscapeDataString(boundProviderIdentity)}";
        startInfo.ArgumentList.Add(launchUrl);
'@
Replace-RequiredLiteral -RelativePath $runtimePath -Old $launchRoot -New $launchBound -Description 'Replacement Chrome command line targets the durable conversation'

$launchIndexOld = '            var launchPageIndex = ChatGptPageSelectionPolicy.SelectForLaunch(launchPages.Select(x => x.Url).ToArray());'
$launchIndexNew = @'
            var launchPageIndex = boundProviderIdentity is null
                ? ChatGptPageSelectionPolicy.SelectForLaunch(launchPages.Select(x => x.Url).ToArray())
                : ChatGptPageSelectionPolicy.SelectForRecovery(launchPages.Select(x => x.Url).ToArray(), boundProviderIdentity);
'@
Replace-RequiredLiteral -RelativePath $runtimePath -Old $launchIndexOld -New $launchIndexNew -Description 'Launch page selection honors a durable provider conversation'

$gotoOld = '                await page.GotoAsync("https://chatgpt.com/", new PageGotoOptions'
$gotoNew = '                await page.GotoAsync(launchUrl, new PageGotoOptions'
Replace-RequiredLiteral -RelativePath $runtimePath -Old $gotoOld -New $gotoNew -Description 'New launch page navigates to the durable conversation URL'

# V5 invokes EnsureManagerChromeReadyAsync before EVERY Manager response poll.
# The earlier LiveChrome patch deliberately makes that method probe unconditionally,
# so this combination creates the exact 3-second PLANNING -> RECOVERING -> PLANNING
# oscillation seen in the installed diagnostic. Remove only that V5-added gate.
$v5GatePattern = '(?ms)\n        // Response reconciliation must never inspect a persisted Browser runtime merely because\n.*?        if \(!await EnsureManagerChromeReadyAsync\(cancellationToken\)\.ConfigureAwait\(false\)\)\n        \{.*?\n            return;\n        \}\n(?=        var runtime = \(await _runtimeRegistry\.ListAsync)'
Replace-RequiredRegex -RelativePath $gatewayPath -Pattern $v5GatePattern -Replacement "`n" -Description 'Manager response polling no longer forces a Chrome recovery every cycle'

# Browser recovery is now evidence-led: only the adapter's all-Unknown/page-missing
# semantic shape can request recovery. That preserves normal response polling and
# also prevents a missing page from being mistaken for an incomplete Manager answer.
$v5SemanticPattern = '(?ms)        if \(semanticBrowserEvidenceMissing\)\n        \{\n            // This is the adapter''s page-missing / inspection-unavailable shape, not evidence that\n.*?\n            return;\n        \}\n(?=        var resilience = new ChatGptResilienceClassifier)'
$v6Semantic = @'
        if (semanticBrowserEvidenceMissing)
        {
            _runtimeErrorFingerprint = null;
            _runtimeErrorCount = 0;
            await PersistLoopGuardAsync(false, cancellationToken).ConfigureAwait(false);

            if (DateTimeOffset.UtcNow < _nextChromeRecoveryRetryAt)
            {
                _autopilot = "PLANNING";
                _latestManagerHandoff = "RECOVERING_MANAGER_BROWSER — live Browser evidence is still unavailable. PCC is waiting for the bounded automatic retry while preserving the accepted Manager dispatch; no resend is authorized.";
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
Replace-RequiredRegex -RelativePath $gatewayPath -Pattern $v5SemanticPattern -Replacement ($v6Semantic + "`n") -Description 'All-Unknown Manager evidence triggers bounded real Browser recovery'

# Structural assertions on the exact transformed source that will be compiled.
$runtimeText = Read-NormalizedText (Join-Path $repoRoot $runtimePath)
if ($runtimeText.Contains($recoverOld, [StringComparison]::Ordinal)) { throw 'V6B_ASSERTION_FAILED: blind live-PID success remains.' }
if (-not $runtimeText.Contains('existingPageIndex >= 0', [StringComparison]::Ordinal)) { throw 'V6B_ASSERTION_FAILED: Playwright page proof is missing.' }
if (-not $runtimeText.Contains('Uri.EscapeDataString(boundProviderIdentity)', [StringComparison]::Ordinal)) { throw 'V6B_ASSERTION_FAILED: durable conversation URL is missing.' }
if (-not $runtimeText.Contains('GotoAsync(launchUrl', [StringComparison]::Ordinal)) { throw 'V6B_ASSERTION_FAILED: durable launch navigation is missing.' }

$gatewayText = Read-NormalizedText (Join-Path $repoRoot $gatewayPath)
if ($gatewayText.Contains('Response reconciliation must never inspect a persisted Browser runtime merely because', [StringComparison]::Ordinal)) { throw 'V6B_ASSERTION_FAILED: V5 unconditional response-poll gate remains.' }
if (-not $gatewayText.Contains('var browserRecovery = await _sessions.RecoverOrphanAsync(runtime.RuntimeId', [StringComparison]::Ordinal)) { throw 'V6B_ASSERTION_FAILED: evidence-led browser recovery is missing.' }

Write-Host 'MANAGER_BROWSER_LIVENESS_V6B_FIX_APPLIED'
