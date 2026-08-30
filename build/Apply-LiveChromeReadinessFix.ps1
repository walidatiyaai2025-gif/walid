[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

function Replace-RequiredRegex {
    param(
        [Parameter(Mandatory)][string]$RelativePath,
        [Parameter(Mandatory)][string]$Pattern,
        [Parameter(Mandatory)][string]$Replacement,
        [Parameter(Mandatory)][string]$Description
    )

    $path = Join-Path $repoRoot $RelativePath
    $text = Get-Content $path -Raw
    $regex = [regex]::new($Pattern, [System.Text.RegularExpressions.RegexOptions]::Singleline)
    $matches = $regex.Matches($text)
    if ($matches.Count -ne 1) {
        throw "PATCH_CONTRACT_MISMATCH: $Description expected exactly one match in $RelativePath, found $($matches.Count)."
    }

    $newText = $regex.Replace(
        $text,
        [System.Text.RegularExpressions.MatchEvaluator]{ param($m) $Replacement },
        1)
    Set-Content -Path $path -Value $newText -Encoding utf8 -NoNewline
    Write-Host "PATCHED: $Description"
}

function Replace-RequiredLiteral {
    param(
        [Parameter(Mandatory)][string]$RelativePath,
        [Parameter(Mandatory)][string]$Old,
        [Parameter(Mandatory)][string]$New,
        [Parameter(Mandatory)][string]$Description
    )

    $path = Join-Path $repoRoot $RelativePath
    $text = Get-Content $path -Raw
    $count = ([regex]::Matches($text, [regex]::Escape($Old))).Count
    if ($count -ne 1) {
        throw "PATCH_CONTRACT_MISMATCH: $Description expected exactly one literal match in $RelativePath, found $count."
    }
    $newText = $text.Replace($Old, $New)
    Set-Content -Path $path -Value $newText -Encoding utf8 -NoNewline
    Write-Host "PATCHED: $Description"
}

$connectReplacement = @'
    private async Task ConnectManagerChromeAsync(CancellationToken cancellationToken)
    {
        var run = RequireActiveRun();
        var managerAgentId = _managerAgentId ?? throw new InvalidOperationException("Manager logical identity is not initialized.");
        try
        {
            var existing = (await _runtimeRegistry.ListAsync(cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(x => StringComparer.Ordinal.Equals(x.ProjectRunId, run.Id.ToString()) && StringComparer.Ordinal.Equals(x.LogicalAgentId, managerAgentId.ToString()) && !x.IsArchived && x.State is not BrowserSessionState.Killed);

            SessionActionResult verified;
            if (existing is null)
            {
                var created = await _sessions.CreateAsync(
                    new BrowserSessionRequest(run.Id.ToString(), managerAgentId.ToString(), DefaultVisibility: BrowserVisibility.Hidden),
                    cancellationToken).ConfigureAwait(false);
                verified = await _sessions.RecoverOrphanAsync(created.RuntimeId, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // Never treat a persisted READY/HIDDEN/VISIBLE record as a live connection proof.
                // RecoverOrphanAsync performs positive ownership proof and then reconnects the
                // DevTools/Playwright endpoint; stale or dead PCC-owned runtimes are replaced.
                verified = await _sessions.RecoverOrphanAsync(existing.RuntimeId, cancellationToken).ConfigureAwait(false);
            }

            if (!verified.Succeeded)
            {
                var failedRuntime = verified.Runtime ?? existing;
                if (failedRuntime is not null && !failedRuntime.IsArchived)
                {
                    await _runtimeRegistry.UpsertAsync(failedRuntime with
                    {
                        State = BrowserSessionState.FailedRequiresAttention,
                        LastActivityAt = DateTimeOffset.UtcNow
                    }, cancellationToken).ConfigureAwait(false);
                }
                throw new InvalidOperationException($"Manager Chrome live verification failed: {verified.Reason}.");
            }

            _recovery.Insert(0, new RecoveryEventSummary(
                DateTimeOffset.UtcNow,
                "READY",
                $"PCC-owned Manager Chrome live DevTools/Playwright connection verified ({verified.RuntimeId}); personal Chrome remains excluded.",
                true));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _recovery.Insert(0, new RecoveryEventSummary(DateTimeOffset.UtcNow, "BROWSER BLOCKED", ex.Message, false));
            throw;
        }
        finally
        {
            await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_settings.AutoResume) EnsureAutopilotLoop();
    }

    private async Task<bool> EnsureManagerChromeReadyAsync
'@

Replace-RequiredRegex \
    -RelativePath 'src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs' \
    -Pattern '    private async Task ConnectManagerChromeAsync\(CancellationToken cancellationToken\)\s*\{.*?\n    \}\n\n    private async Task<bool> EnsureManagerChromeReadyAsync' \
    -Replacement $connectReplacement \
    -Description 'Connect Chrome must perform live endpoint verification and surface failure'

$ensureReplacement = @'
    private async Task<bool> EnsureManagerChromeReadyAsync(CancellationToken cancellationToken)
    {
        if (DateTimeOffset.UtcNow < _nextChromeRecoveryRetryAt)
            return false;

        var run = RequireActiveRun();
        var managerAgentId = _managerAgentId ?? throw new InvalidOperationException("Manager logical identity is not initialized.");
        try
        {
            // Re-run the bounded live verification even when a persisted runtime record looks ready.
            // This prevents a stale process id/session record from opening Manager/Dispatch.
            await ConnectManagerChromeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _nextChromeRecoveryRetryAt = DateTimeOffset.UtcNow.AddSeconds(5);
            _autopilot = "RECOVERING";
            _latestManagerHandoff = $"RECOVERING_CHROME — live Chrome verification failed ({ex.Message}). Automatic retry in 5 seconds.";
            await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        var runtime = (await _runtimeRegistry.ListAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(x => !x.IsArchived && x.State is not BrowserSessionState.Killed and not BrowserSessionState.FailedRequiresAttention &&
                StringComparer.Ordinal.Equals(x.ProjectRunId, run.Id.ToString()) &&
                StringComparer.Ordinal.Equals(x.LogicalAgentId, managerAgentId.ToString()));
        if (runtime is null)
        {
            _nextChromeRecoveryRetryAt = DateTimeOffset.UtcNow.AddSeconds(5);
            _autopilot = "RECOVERING";
            _latestManagerHandoff = "RECOVERING_CHROME — no live PCC-owned Manager Chrome runtime remained after verification. Automatic retry in 5 seconds.";
            await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        var ownership = await _ownership.ProveAsync(runtime, cancellationToken).ConfigureAwait(false);
        if (!ownership.IsProven)
        {
            _nextChromeRecoveryRetryAt = DateTimeOffset.UtcNow.AddSeconds(5);
            _autopilot = "RECOVERING";
            _latestManagerHandoff = $"RECOVERING_CHROME — ownership is unproven after endpoint verification ({ownership.Reason}). Automatic retry in 5 seconds.";
            await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        _nextChromeRecoveryRetryAt = DateTimeOffset.MinValue;
        _latestManagerHandoff = "CHROME_READY — live DevTools/Playwright connection and PCC ownership are proven. Continuing to Manager evidence/planning.";
        _recovery.Insert(0, new RecoveryEventSummary(DateTimeOffset.UtcNow, "CHROME_READY", runtime.RuntimeId, true));
        await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task StartManagerAsync
'@

Replace-RequiredRegex \
    -RelativePath 'src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs' \
    -Pattern '    private async Task<bool> EnsureManagerChromeReadyAsync\(CancellationToken cancellationToken\)\s*\{.*?\n    \}\n\n    private async Task StartManagerAsync' \
    -Replacement $ensureReplacement \
    -Description 'Manager readiness must re-prove live Chrome before planning'

$startupOld = @'
            foreach (var reconciliation in result.Reconciliations)
            {
                var browserState = reconciliation.Succeeded ? BrowserRecoveryState.Ready
'@
$startupNew = @'
            foreach (var reconciliation in result.Reconciliations)
            {
                if (!reconciliation.Succeeded)
                {
                    var failedRuntime = await _runtimeRegistry.GetAsync(reconciliation.RuntimeId, cancellationToken).ConfigureAwait(false);
                    if (failedRuntime is not null && !failedRuntime.IsArchived)
                    {
                        await _runtimeRegistry.UpsertAsync(failedRuntime with
                        {
                            State = BrowserSessionState.FailedRequiresAttention,
                            LastActivityAt = DateTimeOffset.UtcNow
                        }, cancellationToken).ConfigureAwait(false);
                    }
                }

                var browserState = reconciliation.Succeeded ? BrowserRecoveryState.Ready
'@
Replace-RequiredLiteral \
    -RelativePath 'src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs' \
    -Old $startupOld \
    -New $startupNew \
    -Description 'startup recovery failure must invalidate stale Chrome session readiness'

Replace-RequiredLiteral \
    -RelativePath 'src/PCCExecutive.App/ViewModels/MainViewModel.cs' \
    -Old '            _ when manager => BrowserRecoveryState.Ready,' \
    -New '            _ when Snapshot.ChromeConnectionProven => BrowserRecoveryState.Ready,' \
    -Description 'guided Chrome state must use connection proof instead of Manager record existence'

$chromeProofOld = @'
    public bool ChromeConnectionProven => GatewayBound && Sessions.Any(x =>
        x.IsPccOwned &&
        string.Equals(x.Role, "Manager", StringComparison.OrdinalIgnoreCase) &&
'@
$chromeProofNew = @'
    public bool ChromeConnectionProven => GatewayBound && Sessions.Any(x =>
        x.IsPccOwned &&
        x.ProcessId is > 0 &&
        string.Equals(x.Role, "Manager", StringComparison.OrdinalIgnoreCase) &&
'@
Replace-RequiredLiteral \
    -RelativePath 'src/PCCExecutive.App/Presentation/PresentationModels.cs' \
    -Old $chromeProofOld \
    -New $chromeProofNew \
    -Description 'Chrome connection proof requires a concrete PCC-owned process identity'

Replace-RequiredLiteral \
    -RelativePath 'src/PCCExecutive.App/Presentation/RuntimeInspectorPresentation.cs' \
    -Old 'new RuntimePrerequisiteEvidence(GuidedStepId.Chrome, "PCC-owned Chrome runtime ready", current.Sessions.Any(s => s.IsPccOwned), "CHROME_RUNTIME", true, false),' \
    -New 'new RuntimePrerequisiteEvidence(GuidedStepId.Chrome, "PCC-owned Chrome runtime ready", current.ChromeConnectionProven, "CHROME_RUNTIME", true, false),' \
    -Description 'Runtime Inspector Chrome prerequisite must use canonical connection proof'

Replace-RequiredLiteral \
    -RelativePath 'src/PCCExecutive.App/Presentation/RuntimeInspectorPresentation.cs' \
    -Old 'var nextStep = !current.Sessions.Any(s => s.IsPccOwned) ? GuidedStepId.Chrome : !current.HasActiveRun ? GuidedStepId.Project : !current.HasManagerRuntime ? GuidedStepId.Manager : GuidedStepId.Orchestration;' \
    -New 'var nextStep = !current.ChromeConnectionProven ? GuidedStepId.Chrome : !current.HasActiveRun ? GuidedStepId.Project : !current.HasManagerRuntime ? GuidedStepId.Manager : GuidedStepId.Orchestration;' \
    -Description 'Runtime Inspector next action must remain on Chrome until live readiness is proven'

Write-Host 'LIVE_CHROME_READINESS_FIX_APPLIED'
