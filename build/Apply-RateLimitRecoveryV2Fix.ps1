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

$gateway = 'src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs'
$adapter = 'src/PCCExecutive.Browser/ChatGptBrowserAdapter.cs'

Replace-RequiredLiteral -RelativePath $gateway `
    -Old '    private DateTimeOffset _nextChromeRecoveryRetryAt = DateTimeOffset.MinValue;' `
    -New "    private DateTimeOffset _nextChromeRecoveryRetryAt = DateTimeOffset.MinValue;`n    private int _runtimeHealthRetryCount;" `
    -Description 'Track durable semantic-health retry count for adaptive rate-limit backoff'

Replace-RequiredLiteral -RelativePath $gateway `
    -Old '                    _runtimeHealthFault = health.State;' `
    -New "                    _runtimeHealthFault = health.State;`n                    _runtimeHealthRetryCount = Math.Max(0, health.RetryCount);" `
    -Description 'Restore rate-limit retry count after restart'

$healthMethods = @'
    private async Task PersistGlobalHealthPauseAsync(ResilienceDecision resilience, string runtimeId, CancellationToken cancellationToken)
    {
        var run = RequireActiveRun();
        if (resilience.State == ChatGptResilienceState.RateLimited)
            _runtimeHealthRetryCount = Math.Clamp(_runtimeHealthRetryCount + 1, 1, 32);
        else if (resilience.RequiresHumanAction)
            _runtimeHealthRetryCount = 0;

        var cooldown = resilience.RequiresHumanAction
            ? (TimeSpan?)null
            : resilience.State == ChatGptResilienceState.RateLimited
                ? new ConservativeCooldownPolicy().GetCooldown(Math.Max(1, _runtimeHealthRetryCount))
                : TimeSpan.FromSeconds(30);
        _sendGate.Apply(resilience with { Scope = FaultScope.Global, PauseUnsafeNewSends = true }, DateTimeOffset.UtcNow, cooldown);
        _runtimeHealthFault = resilience.State.ToString().ToUpperInvariant();
        _autopilot = _runtimeHealthFault;
        var durable = new DurableRuntimeHealth(true, _runtimeHealthFault, resilience.Reason, _sendGate.Snapshot.ResumeNotBefore, resilience.RequiresHumanAction, runtimeId, _runtimeHealthRetryCount);
        await _store.SaveCheckpointAsync(new DurableCheckpoint($"runtime-health:{run.Id}", run.Id.ToString(), "runtime-health-v3", JsonSerializer.Serialize(durable), DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> TryResumeAfterFreshSemanticHealthAsync(CancellationToken cancellationToken)
    {
        var run = RequireActiveRun();
        if (_runtimeHealthFault is null) return true;
        var gate = _sendGate.Snapshot;
        if (gate.ResumeNotBefore is not null && gate.ResumeNotBefore > DateTimeOffset.UtcNow) return false;

        var runtimes = (await _runtimeRegistry.ListAsync(cancellationToken).ConfigureAwait(false))
            .Where(x => !x.IsArchived && x.State is not BrowserSessionState.Killed && StringComparer.Ordinal.Equals(x.ProjectRunId, run.Id.ToString()) && !string.IsNullOrWhiteSpace(x.TaskId) && !string.IsNullOrWhiteSpace(x.ConversationIdentity) && !string.IsNullOrWhiteSpace(x.ProviderConversationIdentity))
            .ToArray();

        if (runtimes.Length == 0)
        {
            var retryState = ParseResilienceState(_runtimeHealthFault);
            var retry = new ResilienceDecision(retryState, FaultScope.Global, true, false, "FRESH_SEMANTIC_HEALTH_RUNTIME_MISSING");
            await PersistGlobalHealthPauseAsync(retry, string.Empty, cancellationToken).ConfigureAwait(false);
            var retryAt = _sendGate.Snapshot.ResumeNotBefore;
            _latestManagerHandoff = $"{_runtimeHealthFault} — semantic re-probe found no bound runtime. Automatic retry {_runtimeHealthRetryCount} is scheduled for {retryAt:O}.";
            _recovery.Insert(0, new RecoveryEventSummary(DateTimeOffset.UtcNow, "HEALTH_RETRY", _latestManagerHandoff, true));
            await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        foreach (var runtime in runtimes)
        {
            var expected = new BrowserDispatchExpectation(run.Id.ToString(), runtime.LogicalAgentId, runtime.TaskId!, runtime.ConversationIdentity!, runtime.ProviderConversationIdentity!, runtime.WorkerSlotId);
            var semantic = await _browserAdapter.InspectAsync(runtime, expected, cancellationToken).ConfigureAwait(false);

            if (semantic.Auth.State is AuthState.LoginRequired or AuthState.Challenge)
            {
                var authDecision = new ChatGptResilienceClassifier().Classify(semantic, DateTimeOffset.UtcNow - runtime.LastActivityAt);
                CaptureProviderAttention(semantic.Auth.State == AuthState.Challenge ? "CHALLENGE" : "LOGIN_REQUIRED", runtime.RuntimeId, "ChatGPT session");
                await PersistGlobalHealthPauseAsync(authDecision, runtime.RuntimeId, cancellationToken).ConfigureAwait(false);
                _latestManagerHandoff = authDecision.Reason;
                await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }

            if (semantic.Health.State != PageHealth.Healthy)
            {
                var classified = new ChatGptResilienceClassifier().Classify(semantic, DateTimeOffset.UtcNow - runtime.LastActivityAt);
                var retryState = classified.Scope == FaultScope.Global && classified.PauseUnsafeNewSends
                    ? classified
                    : new ResilienceDecision(ParseResilienceState(_runtimeHealthFault), FaultScope.Global, true, false, $"FRESH_SEMANTIC_HEALTH_{semantic.Health.State.ToString().ToUpperInvariant()}");
                await PersistGlobalHealthPauseAsync(retryState, runtime.RuntimeId, cancellationToken).ConfigureAwait(false);
                var retryAt = _sendGate.Snapshot.ResumeNotBefore;
                _latestManagerHandoff = $"{_runtimeHealthFault} — ChatGPT is still not proven healthy ({semantic.Health.State}). No send occurred. Retry {_runtimeHealthRetryCount}; next semantic probe at {retryAt:O}.";
                _recovery.Insert(0, new RecoveryEventSummary(DateTimeOffset.UtcNow, "HEALTH_RETRY", _latestManagerHandoff, true));
                await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }
        }

        await _newSendPause.ResumeNewSendsAsync("Fresh semantic Browser health proved safe after durable global fault.", cancellationToken).ConfigureAwait(false);
        _runtimeHealthFault = null;
        _runtimeHealthRetryCount = 0;
        _latestManagerHandoff = "HEALTH RECOVERED — fresh ChatGPT semantic health is proven safe. Automatic Manager execution is resuming without duplicating any send.";
        _recovery.Insert(0, new RecoveryEventSummary(DateTimeOffset.UtcNow, "HEALTH_RECOVERED", _latestManagerHandoff, true));
        await _store.SaveCheckpointAsync(new DurableCheckpoint($"runtime-health:{run.Id}", run.Id.ToString(), "runtime-health-v3", JsonSerializer.Serialize(new DurableRuntimeHealth(false, "READY", "Fresh semantic health proven.", null, false, null, 0)), DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
        await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static string MapRecoveredPhaseToAutopilot
'@

Replace-RequiredRegex -RelativePath $gateway `
    -Pattern '    private async Task PersistGlobalHealthPauseAsync\(ResilienceDecision resilience, string runtimeId, CancellationToken cancellationToken\)\s*\{.*?\n    \}\n\n    private async Task<bool> TryResumeAfterFreshSemanticHealthAsync\(CancellationToken cancellationToken\)\s*\{.*?\n    \}\n\n    private static string MapRecoveredPhaseToAutopilot' `
    -Replacement $healthMethods `
    -Description 'Re-arm repeated rate limits with adaptive durable cooldown and visible semantic re-probes'

Replace-RequiredLiteral -RelativePath $gateway `
    -Old '    private sealed record DurableRuntimeHealth(bool Active, string State, string Reason, DateTimeOffset? ResumeNotBefore, bool RequiresHumanAction, string? RuntimeId);' `
    -New '    private sealed record DurableRuntimeHealth(bool Active, string State, string Reason, DateTimeOffset? ResumeNotBefore, bool RequiresHumanAction, string? RuntimeId, int RetryCount = 0);' `
    -Description 'Persist repeated rate-limit backoff count'

Replace-RequiredLiteral -RelativePath $adapter `
    -Old '            var health = DetectHealth(page.Url, body, composer is not null, auth.State);' `
    -New "            var transientHealthText = await TransientHealthSurfaceTextAsync(page).ConfigureAwait(false);`n            var health = DetectHealth(page.Url, transientHealthText, composer is not null, auth.State);" `
    -Description 'Do not classify old conversation text as a live provider rate limit'

$healthSurfaceMethod = @'
    private static async Task<string> TransientHealthSurfaceTextAsync(IPage page)
    {
        try
        {
            return await page.EvaluateAsync<string>(
                """
                () => {
                  const pattern = /(too many requests|rate limit|try again in a few minutes|sending too quickly|temporary usage limit|account limit|you are offline|no internet|network connection was lost|conversation is too long|maximum conversation length|start a new chat to continue|this conversation has reached its limit|something went wrong|temporary error|failed to load|there was an error generating|taking longer than expected|still working on this)/i;
                  const turnSelector = "[data-message-author-role], [data-turn], article[data-testid*='conversation-turn'], [data-testid^='conversation-turn-']";
                  const visible = (el) => {
                    const style = window.getComputedStyle(el);
                    const rect = el.getBoundingClientRect();
                    return style.visibility !== 'hidden' && style.display !== 'none' && rect.width > 0 && rect.height > 0;
                  };
                  const out = [];
                  const seen = new Set();
                  const nodes = document.querySelectorAll("[role='alert'], [role='status'], [role='dialog'], [aria-live], [data-testid*='toast'], [data-testid*='error'], [class*='toast'], [class*='alert'], [class*='error'], main div, main p, main span");
                  for (const el of nodes) {
                    if (!visible(el)) continue;
                    if (el.closest(turnSelector) || el.querySelector(turnSelector)) continue;
                    const text = ((el.innerText || el.textContent) || '').replace(/\s+/g, ' ').trim();
                    if (!text || text.length > 500 || !pattern.test(text)) continue;
                    if (seen.has(text)) continue;
                    seen.add(text);
                    out.push(text);
                  }
                  return out.join('\n');
                }
                """).ConfigureAwait(false) ?? string.Empty;
        }
        catch (PlaywrightException)
        {
            return string.Empty;
        }
    }

    private async Task<IPage?> ExpectedPageAsync
'@

Replace-RequiredLiteral -RelativePath $adapter `
    -Old '    private async Task<IPage?> ExpectedPageAsync' `
    -New $healthSurfaceMethod `
    -Description 'Add visible non-conversation transient health surface extraction'

Write-Host 'RATE_LIMIT_RECOVERY_V2_FIX_APPLIED'
