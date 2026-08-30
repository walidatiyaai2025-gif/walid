[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

function Read-Normalized([string]$Path) {
    (Get-Content $Path -Raw).Replace("`r`n", "`n")
}

function Write-Normalized([string]$Path, [string]$Text) {
    Set-Content -Path $Path -Value $Text -Encoding utf8 -NoNewline
}

function Assert-Contains([string]$Text, [string]$Needle, [string]$Description) {
    if (-not $Text.Contains($Needle, [StringComparison]::Ordinal)) {
        throw "PATCH_CONTRACT_MISMATCH: $Description"
    }
}

$hostPath = Join-Path $repoRoot 'src/PCCExecutive.Browser/PlaywrightChromeRuntimeHost.cs'
$sessionsPath = Join-Path $repoRoot 'src/PCCExecutive.Browser/OwnershipAndSessions.cs'
$modelsPath = Join-Path $repoRoot 'src/PCCExecutive.App/Presentation/PresentationModels.cs'
$gatewayPath = Join-Path $repoRoot 'src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs'
$screenPath = Join-Path $repoRoot 'src/PCCExecutive.App/ViewModels/ScreenViewModels.cs'
$sourceIdentityPath = Join-Path $repoRoot 'src/PCCExecutive.App/ViewModels/BuildSourceIdentity.g.cs'

$recoverAnchor = '    public async Task<bool> RecoverAsync(BrowserRuntimeRecord runtime, CancellationToken cancellationToken = default)'
$launchAnchor = '    public async Task<BrowserRuntimeRecord> LaunchAsync(BrowserSessionRequest request, CancellationToken cancellationToken = default)'

# 1. Cached process/browser/page objects are not endpoint proof. A real Playwright roundtrip is required.
$hostText = Read-Normalized $hostPath
$recoverIndex = $hostText.IndexOf($recoverAnchor, [StringComparison]::Ordinal)
if ($recoverIndex -lt 0) { throw 'PATCH_CONTRACT_MISMATCH: RecoverAsync anchor missing.' }
$cachedPattern = '(?ms)^        if \(_connections\.TryGetValue\(runtime\.RuntimeId, out var existing\) && !existing\.Process\.HasExited\)\s*\{.*?^            _connections\.TryRemove\(runtime\.RuntimeId, out _\);\s*^        \}'
$cachedMatches = [regex]::Matches($hostText.Substring($recoverIndex), $cachedPattern)
if ($cachedMatches.Count -ne 1) { throw "PATCH_CONTRACT_MISMATCH: cached recovery block found $($cachedMatches.Count)." }
$cachedMatch = $cachedMatches[0]
$cachedGlobalIndex = $recoverIndex + $cachedMatch.Index
$cachedReplacement = @'
        if (_connections.TryGetValue(runtime.RuntimeId, out var existing))
        {
            if (await ProbeConnectionAsync(existing, runtime.ProviderConversationIdentity, cancellationToken).ConfigureAwait(false))
                return true;

            _connections.TryRemove(runtime.RuntimeId, out _);
        }
'@
$hostText = $hostText.Remove($cachedGlobalIndex, $cachedMatch.Length).Insert($cachedGlobalIndex, $cachedReplacement)
Write-Normalized $hostPath $hostText
Write-Host 'PATCHED: Cached PCC connection requires live DevTools/Playwright roundtrip'

# 2. A fresh CDP reconnect must pass the same roundtrip before recovery succeeds.
$hostText = Read-Normalized $hostPath
$recoverIndex = $hostText.IndexOf($recoverAnchor, [StringComparison]::Ordinal)
$recoveryBind = '        _connections[runtime.RuntimeId] = new Connection(process, browser, page, runtime.ContextIdentity ?? Guid.NewGuid().ToString("N"), runtime.ProfilePath);'
$recoveryBindIndex = $hostText.IndexOf($recoveryBind, $recoverIndex, [StringComparison]::Ordinal)
$setVisibilityIndex = $hostText.IndexOf('    public async Task SetVisibilityAsync(', $recoverIndex, [StringComparison]::Ordinal)
if ($recoveryBindIndex -lt 0 -or $setVisibilityIndex -lt 0 -or $recoveryBindIndex -gt $setVisibilityIndex) {
    throw 'PATCH_CONTRACT_MISMATCH: RecoverAsync connection bind missing.'
}
$recoveryReplacement = @'
        var recoveredConnection = new Connection(process, browser, page, runtime.ContextIdentity ?? Guid.NewGuid().ToString("N"), runtime.ProfilePath);
        if (!await ProbeConnectionAsync(recoveredConnection, runtime.ProviderConversationIdentity, cancellationToken).ConfigureAwait(false))
            return false;
        _connections[runtime.RuntimeId] = recoveredConnection;
'@
$hostText = $hostText.Remove($recoveryBindIndex, $recoveryBind.Length).Insert($recoveryBindIndex, $recoveryReplacement)
Write-Normalized $hostPath $hostText
Write-Host 'PATCHED: Reconnected CDP endpoint requires live page probe'

# 3. Launch itself is not READY until JavaScript executes through the attached Playwright/CDP channel.
$hostText = Read-Normalized $hostPath
$launchIndex = $hostText.IndexOf($launchAnchor, [StringComparison]::Ordinal)
if ($launchIndex -lt 0) { throw 'PATCH_CONTRACT_MISMATCH: LaunchAsync anchor missing.' }
$launchBind = '            _connections[runtimeId] = new Connection(process, browser, page, contextIdentity, profilePath);'
$launchBindIndex = $hostText.IndexOf($launchBind, $launchIndex, [StringComparison]::Ordinal)
$recoverIndex = $hostText.IndexOf($recoverAnchor, $launchIndex, [StringComparison]::Ordinal)
if ($launchBindIndex -lt 0 -or $recoverIndex -lt 0 -or $launchBindIndex -gt $recoverIndex) {
    throw 'PATCH_CONTRACT_MISMATCH: LaunchAsync connection bind missing.'
}
$launchReplacement = @'
            var launchedConnection = new Connection(process, browser, page, contextIdentity, profilePath);
            if (!await ProbeConnectionAsync(launchedConnection, request.ProviderConversationIdentity, cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("Chrome launched but the DevTools/Playwright endpoint did not pass the live page probe.");
            _connections[runtimeId] = launchedConnection;
'@
$hostText = $hostText.Remove($launchBindIndex, $launchBind.Length).Insert($launchBindIndex, $launchReplacement)

# Recompute method boundary after the insertion. V9B failed because it reused the stale pre-insert index.
$recoverIndex = $hostText.IndexOf($recoverAnchor, $launchIndex, [StringComparison]::Ordinal)
if ($recoverIndex -lt 0) { throw 'PATCH_CONTRACT_MISMATCH: RecoverAsync anchor missing after LaunchAsync patch.' }
$launchState = '                State = request.DefaultVisibility == BrowserVisibility.Hidden ? BrowserSessionState.Hidden : BrowserSessionState.Visible,'
$launchStateIndex = $hostText.IndexOf($launchState, $launchIndex, [StringComparison]::Ordinal)
if ($launchStateIndex -lt 0 -or $launchStateIndex -gt $recoverIndex) {
    throw 'PATCH_CONTRACT_MISMATCH: LaunchAsync state assignment missing after boundary recompute.'
}
if ($hostText.IndexOf($launchState, $launchStateIndex + $launchState.Length, [StringComparison]::Ordinal) -ge 0) {
    throw 'PATCH_CONTRACT_MISMATCH: LaunchAsync state assignment is not unique.'
}
$hostText = $hostText.Remove($launchStateIndex, $launchState.Length).Insert($launchStateIndex, '                State = BrowserSessionState.Ready,')
Write-Normalized $hostPath $hostText
Write-Host 'PATCHED: New Chrome becomes READY only after live endpoint proof'

# 4. Insert the bounded liveness probe once, before V8E duplicate-tab cleanup.
$hostText = Read-Normalized $hostPath
if ($hostText.Contains('private static async Task<bool> ProbeConnectionAsync', [StringComparison]::Ordinal)) {
    throw 'PATCH_CONTRACT_MISMATCH: endpoint probe helper already exists before V9C.'
}
$helperAnchor = '    private static async Task CloseOtherChatGptPagesAsync(IBrowserContext context, IPage selected)'
$helperIndex = $hostText.IndexOf($helperAnchor, [StringComparison]::Ordinal)
if ($helperIndex -lt 0) { throw 'PATCH_CONTRACT_MISMATCH: V8E duplicate-tab helper anchor missing.' }
$probeHelper = @'
    private static async Task<bool> ProbeConnectionAsync(
        Connection connection,
        string? providerConversationIdentity,
        CancellationToken cancellationToken)
    {
        try
        {
            if (connection.Process.HasExited || !connection.Browser.IsConnected || connection.Page.IsClosed)
                return false;

            var pages = connection.Browser.Contexts
                .SelectMany(x => x.Pages)
                .Where(x => !x.IsClosed)
                .ToArray();
            var selectedIndex = ChatGptPageSelectionPolicy.SelectForRecovery(
                pages.Select(x => x.Url).ToArray(),
                providerConversationIdentity);
            if (selectedIndex < 0)
                return false;

            var selected = pages[selectedIndex];
            using var probeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            probeTimeout.CancelAfter(TimeSpan.FromSeconds(3));
            var readyState = await selected
                .EvaluateAsync<string>("() => document.readyState")
                .WaitAsync(probeTimeout.Token)
                .ConfigureAwait(false);

            return !string.IsNullOrWhiteSpace(readyState) && IsChatGptUrl(selected.Url);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (PlaywrightException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsChatGptUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
        return string.Equals(uri.Host, "chatgpt.com", StringComparison.OrdinalIgnoreCase) ||
               uri.Host.EndsWith(".chatgpt.com", StringComparison.OrdinalIgnoreCase);
    }

'@
$hostText = $hostText.Insert($helperIndex, $probeHelper)
Write-Normalized $hostPath $hostText
Write-Host 'PATCHED: Added bounded endpoint liveness probe'

# 5. Visibility is presentation only; Open/Hide/BringToFront cannot manufacture READY or erase failure.
$sessionsText = Read-Normalized $sessionsPath
$visibilityAnchor = '    private async Task<SessionActionResult> SetVisibilityAsync('
$visibilityIndex = $sessionsText.IndexOf($visibilityAnchor, [StringComparison]::Ordinal)
if ($visibilityIndex -lt 0) { throw 'PATCH_CONTRACT_MISMATCH: SetVisibilityAsync anchor missing.' }
$visibilityState = '        var state = visibility == BrowserVisibility.Hidden ? BrowserSessionState.Hidden : BrowserSessionState.Visible;'
$visibilityStateIndex = $sessionsText.IndexOf($visibilityState, $visibilityIndex, [StringComparison]::Ordinal)
if ($visibilityStateIndex -lt 0) { throw 'PATCH_CONTRACT_MISMATCH: SetVisibility state assignment missing.' }
$visibilityReplacement = @'
        var state = runtime.State switch
        {
            BrowserSessionState.Ready => BrowserSessionState.Ready,
            BrowserSessionState.FailedRequiresAttention => BrowserSessionState.FailedRequiresAttention,
            BrowserSessionState.Degraded => BrowserSessionState.Degraded,
            BrowserSessionState.Recovering => BrowserSessionState.Recovering,
            _ => visibility == BrowserVisibility.Hidden ? BrowserSessionState.Hidden : BrowserSessionState.Visible
        };
'@
$sessionsText = $sessionsText.Remove($visibilityStateIndex, $visibilityState.Length).Insert($visibilityStateIndex, $visibilityReplacement)
Write-Normalized $sessionsPath $sessionsText
Write-Host 'PATCHED: Visibility actions preserve endpoint-verification state'

# 6. UI connection proof now requires positive PCC ownership, exact READY, and a process identity.
$modelsText = Read-Normalized $modelsPath
$proofPattern = '(?ms)^    public bool ChromeConnectionProven => GatewayBound && Sessions\.Any\(x =>\s*.*?^\s*\);'
$proofMatches = [regex]::Matches($modelsText, $proofPattern)
if ($proofMatches.Count -ne 1) { throw "PATCH_CONTRACT_MISMATCH: ChromeConnectionProven property found $($proofMatches.Count)." }
$proofReplacement = @'
    public bool ChromeConnectionProven => GatewayBound && Sessions.Any(x =>
        x.IsPccOwned &&
        string.Equals(x.Role, "Manager", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(x.State, "READY", StringComparison.Ordinal) &&
        x.ProcessId is > 0);
'@
$modelsText = [regex]::Replace($modelsText, $proofPattern, { param($m) $proofReplacement }, 1)
Write-Normalized $modelsPath $modelsText
Write-Host 'PATCHED: ChromeConnectionProven requires verified READY Manager runtime'

# 7. Endpoint health becomes Healthy for the state produced only after the live probe.
$gatewayText = Read-Normalized $gatewayPath
$healthPattern = '(?ms)^    private static HealthState MapSessionHealth\(BrowserSessionState state\) => state switch\s*\{.*?^    \};'
$healthMatches = [regex]::Matches($gatewayText, $healthPattern)
if ($healthMatches.Count -ne 1) { throw "PATCH_CONTRACT_MISMATCH: MapSessionHealth found $($healthMatches.Count)." }
$healthReplacement = @'
    private static HealthState MapSessionHealth(BrowserSessionState state) => state switch
    {
        BrowserSessionState.Ready => HealthState.Healthy,
        BrowserSessionState.Recovering => HealthState.Recovering,
        BrowserSessionState.Degraded or BrowserSessionState.FailedRequiresAttention => HealthState.Unknown,
        _ => HealthState.Unknown
    };
'@
$gatewayText = [regex]::Replace($gatewayText, $healthPattern, { param($m) $healthReplacement }, 1)
Write-Normalized $gatewayPath $gatewayText
Write-Host 'PATCHED: EndpointHealth reflects verified READY state'

# 8. Embed exact build source provenance into installed diagnostics instead of relying on a CI-only env var.
$sourceSha = (git -C $repoRoot rev-parse HEAD).Trim().ToLowerInvariant()
if ($sourceSha -notmatch '^[0-9a-f]{40}$') { throw "SOURCE_IDENTITY_INVALID: $sourceSha" }
$sourceIdentity = @"
namespace PCCExecutive.App.ViewModels;

internal static class BuildSourceIdentity
{
    internal const string Value = "$sourceSha";
}
"@
Write-Normalized $sourceIdentityPath $sourceIdentity
$screenText = Read-Normalized $screenPath
$oldExport = '    private Task<string> ExportAsync() => _services!.ExportJson(typeof(RuntimeInspectorViewModel).Assembly.GetName().Version?.ToString() ?? "unknown", Environment.GetEnvironmentVariable("PCC_SOURCE_SHA"), 250, CancellationToken.None);'
$exportIndex = $screenText.IndexOf($oldExport, [StringComparison]::Ordinal)
if ($exportIndex -lt 0) { throw 'PATCH_CONTRACT_MISMATCH: Runtime diagnostic ExportAsync old source identity binding missing.' }
$newExport = '    private Task<string> ExportAsync() => _services!.ExportJson(typeof(RuntimeInspectorViewModel).Assembly.GetName().Version?.ToString() ?? "unknown", BuildSourceIdentity.Value, 250, CancellationToken.None);'
$screenText = $screenText.Remove($exportIndex, $oldExport.Length).Insert($exportIndex, $newExport)
Write-Normalized $screenPath $screenText
Write-Host "PATCHED: Embedded installed diagnostic SourceIdentity=$sourceSha"

# 9. Deterministic post-patch assertions for the exact false-ready path.
$hostText = Read-Normalized $hostPath
Assert-Contains $hostText 'ProbeConnectionAsync(existing, runtime.ProviderConversationIdentity, cancellationToken)' 'cached connection probe missing after patch.'
Assert-Contains $hostText 'ProbeConnectionAsync(recoveredConnection, runtime.ProviderConversationIdentity, cancellationToken)' 'recovery connection probe missing after patch.'
Assert-Contains $hostText 'ProbeConnectionAsync(launchedConnection, request.ProviderConversationIdentity, cancellationToken)' 'launch connection probe missing after patch.'
Assert-Contains $hostText 'EvaluateAsync<string>("() => document.readyState")' 'Playwright JavaScript roundtrip missing after patch.'
Assert-Contains $hostText 'State = BrowserSessionState.Ready,' 'Launch READY assignment missing after probe.'
$sessionsText = Read-Normalized $sessionsPath
Assert-Contains $sessionsText 'BrowserSessionState.Ready => BrowserSessionState.Ready' 'visibility READY preservation missing.'
Assert-Contains $sessionsText 'BrowserSessionState.FailedRequiresAttention => BrowserSessionState.FailedRequiresAttention' 'visibility failure preservation missing.'
$modelsText = Read-Normalized $modelsPath
Assert-Contains $modelsText 'string.Equals(x.State, "READY", StringComparison.Ordinal)' 'strict Chrome connection proof missing.'
$gatewayText = Read-Normalized $gatewayPath
Assert-Contains $gatewayText 'BrowserSessionState.Ready => HealthState.Healthy' 'verified endpoint health mapping missing.'
$screenText = Read-Normalized $screenPath
Assert-Contains $screenText 'BuildSourceIdentity.Value' 'installed SourceIdentity binding missing.'

Write-Host 'ENDPOINT_TRUTH_V9C_FIX_APPLIED'
