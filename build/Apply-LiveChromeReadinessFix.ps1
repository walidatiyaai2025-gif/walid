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
    if ($matches.Count -ne 1) { throw "PATCH_CONTRACT_MISMATCH: $Description expected exactly one match in $RelativePath, found $($matches.Count)." }
    $newText = $regex.Replace($text, [System.Text.RegularExpressions.MatchEvaluator]{ param($m) $Replacement }, 1)
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
    if ($count -ne 1) { throw "PATCH_CONTRACT_MISMATCH: $Description expected exactly one literal match in $RelativePath, found $count." }
    Set-Content -Path $path -Value $text.Replace($Old, $New) -Encoding utf8 -NoNewline
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

            var target = existing ?? await _sessions.CreateAsync(
                new BrowserSessionRequest(run.Id.ToString(), managerAgentId.ToString(), DefaultVisibility: BrowserVisibility.Hidden),
                cancellationToken).ConfigureAwait(false);

            // A persisted session record or ownership proof is not a live connection proof.
            // Always make BrowserSessionController reconnect the DevTools/Playwright endpoint.
            // RecoverOrphanAsync safely replaces only a positively-proven PCC-owned stale runtime.
            var verified = await _sessions.RecoverOrphanAsync(target.RuntimeId, cancellationToken).ConfigureAwait(false);
            if (!verified.Succeeded)
            {
                var failedRuntime = verified.Runtime ?? target;
                if (!failedRuntime.IsArchived)
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
            await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }

        await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (_settings.AutoResume) EnsureAutopilotLoop();
    }

    private async Task<bool> EnsureManagerChromeReadyAsync
'@

Replace-RequiredRegex -RelativePath 'src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs' -Pattern '    private async Task ConnectManagerChromeAsync\(CancellationToken cancellationToken\)\s*\{.*?\n    \}\n\n    private async Task<bool> EnsureManagerChromeReadyAsync' -Replacement $connectReplacement -Description 'Connect Chrome performs live endpoint verification and surfaces failure'

$ensureConditionOld = @'
        if (runtime is null || ownership is null || !ownership.IsProven || runtime.State is BrowserSessionState.Creating or BrowserSessionState.Degraded or BrowserSessionState.Recovering or BrowserSessionState.FailedRequiresAttention)
'@
$ensureConditionNew = @'
        // Re-probe on every Manager-start boundary. A persisted runtime that merely looks READY
        // must not unlock Manager/Dispatch until DevTools/Playwright reconnect succeeds now.
        if (true)
'@
Replace-RequiredLiteral -RelativePath 'src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs' -Old $ensureConditionOld -New $ensureConditionNew -Description 'Manager readiness always re-probes Chrome endpoint'

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
Replace-RequiredLiteral -RelativePath 'src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs' -Old $startupOld -New $startupNew -Description 'Startup recovery failure invalidates stale Chrome readiness'

Replace-RequiredLiteral -RelativePath 'src/PCCExecutive.App/ViewModels/MainViewModel.cs' -Old '            _ when manager => BrowserRecoveryState.Ready,' -New '            _ when Snapshot.ChromeConnectionProven => BrowserRecoveryState.Ready,' -Description 'Guided Chrome state uses canonical connection proof'

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
Replace-RequiredLiteral -RelativePath 'src/PCCExecutive.App/Presentation/PresentationModels.cs' -Old $chromeProofOld -New $chromeProofNew -Description 'Chrome connection proof requires concrete PCC-owned process identity'

Replace-RequiredLiteral -RelativePath 'src/PCCExecutive.App/Presentation/RuntimeInspectorPresentation.cs' -Old 'new RuntimePrerequisiteEvidence(GuidedStepId.Chrome, "PCC-owned Chrome runtime ready", current.Sessions.Any(s => s.IsPccOwned), "CHROME_RUNTIME", true, false),' -New 'new RuntimePrerequisiteEvidence(GuidedStepId.Chrome, "PCC-owned Chrome runtime ready", current.ChromeConnectionProven, "CHROME_RUNTIME", true, false),' -Description 'Runtime Inspector Chrome prerequisite uses canonical connection proof'

Replace-RequiredLiteral -RelativePath 'src/PCCExecutive.App/Presentation/RuntimeInspectorPresentation.cs' -Old 'var nextStep = !current.Sessions.Any(s => s.IsPccOwned) ? GuidedStepId.Chrome : !current.HasActiveRun ? GuidedStepId.Project : !current.HasManagerRuntime ? GuidedStepId.Manager : GuidedStepId.Orchestration;' -New 'var nextStep = !current.ChromeConnectionProven ? GuidedStepId.Chrome : !current.HasActiveRun ? GuidedStepId.Project : !current.HasManagerRuntime ? GuidedStepId.Manager : GuidedStepId.Orchestration;' -Description 'Runtime Inspector stays on Chrome until connection proof exists'

Write-Host 'LIVE_CHROME_READINESS_FIX_APPLIED'
