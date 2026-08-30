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

function Replace-OnceRegex {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Pattern,
        [Parameter(Mandatory)][string]$Replacement,
        [Parameter(Mandatory)][string]$Description
    )
    $text = Read-Normalized $Path
    $matches = [regex]::Matches($text, $Pattern)
    if ($matches.Count -ne 1) {
        throw "PATCH_CONTRACT_MISMATCH: $Description expected exactly one match in $Path, found $($matches.Count)."
    }
    $text = [regex]::Replace($text, $Pattern, { param($m) $Replacement }, 1)
    Write-Normalized $Path $text
    Write-Host "PATCHED: $Description"
}

$hostPath = Join-Path $repoRoot 'src/PCCExecutive.Browser/PlaywrightChromeRuntimeHost.cs'
$sessionsPath = Join-Path $repoRoot 'src/PCCExecutive.Browser/OwnershipAndSessions.cs'
$modelsPath = Join-Path $repoRoot 'src/PCCExecutive.App/Presentation/PresentationModels.cs'
$gatewayPath = Join-Path $repoRoot 'src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs'
$screenPath = Join-Path $repoRoot 'src/PCCExecutive.App/ViewModels/ScreenViewModels.cs'
$browserTestPath = Join-Path $repoRoot 'tests/PCCExecutive.Browser.Tests/EndpointTruthV9bBuildTests.cs'
$appTestPath = Join-Path $repoRoot 'tests/PCCExecutive.App.Tests/EndpointTruthV9bRegressionTests.cs'
$sourceIdentityPath = Join-Path $repoRoot 'src/PCCExecutive.App/ViewModels/BuildSourceIdentity.g.cs'

# 1) A cached Playwright object graph is not proof that the CDP channel still works.
$hostText = Read-Normalized $hostPath
$recoverAnchor = '    public async Task<bool> RecoverAsync(BrowserRuntimeRecord runtime, CancellationToken cancellationToken = default)'
$recoverIndex = $hostText.IndexOf($recoverAnchor, [StringComparison]::Ordinal)
if ($recoverIndex -lt 0) { throw 'PATCH_CONTRACT_MISMATCH: RecoverAsync anchor missing.' }
$recoverTail = $hostText.Substring($recoverIndex)
$cachedPattern = '(?ms)^        if \(_connections\.TryGetValue\(runtime\.RuntimeId, out var existing\) && !existing\.Process\.HasExited\)\s*\{.*?^            _connections\.TryRemove\(runtime\.RuntimeId, out _\);\s*^        \}'
$cachedMatches = [regex]::Matches($recoverTail, $cachedPattern)
if ($cachedMatches.Count -ne 1) { throw "PATCH_CONTRACT_MISMATCH: cached recovery block found $($cachedMatches.Count)." }
$cached = $cachedMatches[0]
$cachedGlobalIndex = $recoverIndex + $cached.Index
$cachedReplacement = @'
        if (_connections.TryGetValue(runtime.RuntimeId, out var existing))
        {
            if (await ProbeConnectionAsync(existing, runtime.ProviderConversationIdentity, cancellationToken).ConfigureAwait(false))
                return true;

            _connections.TryRemove(runtime.RuntimeId, out _);
        }
'@
$hostText = $hostText.Remove($cachedGlobalIndex, $cached.Length).Insert($cachedGlobalIndex, $cachedReplacement)
Write-Normalized $hostPath $hostText
Write-Host 'PATCHED: Cached PCC connection now requires real endpoint probe'

# 2) CDP reconnect must also execute a real page roundtrip before success.
$hostText = Read-Normalized $hostPath
$recoverIndex = $hostText.IndexOf($recoverAnchor, [StringComparison]::Ordinal)
$bindAnchor = '        _connections[runtime.RuntimeId] = new Connection(process, browser, page, runtime.ContextIdentity ?? Guid.NewGuid().ToString("N"), runtime.ProfilePath);'
$bindIndex = $hostText.IndexOf($bindAnchor, $recoverIndex, [StringComparison]::Ordinal)
if ($bindIndex -lt 0) { throw 'PATCH_CONTRACT_MISMATCH: RecoverAsync connection bind missing.' }
$nextMethod = $hostText.IndexOf('    public async Task SetVisibilityAsync(', $bindIndex, [StringComparison]::Ordinal)
if ($nextMethod -lt 0 -or $bindIndex -gt $nextMethod) { throw 'PATCH_CONTRACT_MISMATCH: RecoverAsync bind escaped method.' }
$bindReplacement = @'
        var recoveredConnection = new Connection(process, browser, page, runtime.ContextIdentity ?? Guid.NewGuid().ToString("N"), runtime.ProfilePath);
        if (!await ProbeConnectionAsync(recoveredConnection, runtime.ProviderConversationIdentity, cancellationToken).ConfigureAwait(false))
            return false;
        _connections[runtime.RuntimeId] = recoveredConnection;
'@
$hostText = $hostText.Remove($bindIndex, $bindAnchor.Length).Insert($bindIndex, $bindReplacement)
Write-Normalized $hostPath $hostText
Write-Host 'PATCHED: Reconnected CDP endpoint now requires real endpoint probe'

# 3) A newly launched Chrome is not READY until the page can actually execute JavaScript.
$hostText = Read-Normalized $hostPath
$launchAnchor = '    public async Task<BrowserRuntimeRecord> LaunchAsync(BrowserSessionRequest request, CancellationToken cancellationToken = default)'
$launchIndex = $hostText.IndexOf($launchAnchor, [StringComparison]::Ordinal)
if ($launchIndex -lt 0) { throw 'PATCH_CONTRACT_MISMATCH: LaunchAsync anchor missing.' }
$launchBindAnchor = '            _connections[runtimeId] = new Connection(process, browser, page, contextIdentity, profilePath);'
$launchBindIndex = $hostText.IndexOf($launchBindAnchor, $launchIndex, [StringComparison]::Ordinal)
$recoverMethodIndex = $hostText.IndexOf($recoverAnchor, $launchIndex, [StringComparison]::Ordinal)
if ($launchBindIndex -lt 0 -or $recoverMethodIndex -lt 0 -or $launchBindIndex -gt $recoverMethodIndex) { throw 'PATCH_CONTRACT_MISMATCH: LaunchAsync connection bind missing.' }
$launchBindReplacement = @'
            var launchedConnection = new Connection(process, browser, page, contextIdentity, profilePath);
            if (!await ProbeConnectionAsync(launchedConnection, request.ProviderConversationIdentity, cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("Chrome launched but the DevTools/Playwright endpoint did not pass the live page probe.");
            _connections[runtimeId] = launchedConnection;
'@
$hostText = $hostText.Remove($launchBindIndex, $launchBindAnchor.Length).Insert($launchBindIndex, $launchBindReplacement)
$launchStatePattern = '(?m)^\s*State\s*=\s*request\.DefaultVisibility\s*==\s*BrowserVisibility\.Hidden\s*\?\s*BrowserSessionState\.Hidden\s*:\s*BrowserSessionState\.Visible,\s*$'
$launchStateMatches = [regex]::Matches($hostText.Substring($launchIndex, $recoverMethodIndex - $launchIndex), $launchStatePattern)
if ($launchStateMatches.Count -ne 1) { throw "PATCH_CONTRACT_MISMATCH: LaunchAsync state assignment found $($launchStateMatches.Count)." }
$launchState = $launchStateMatches[0]
$launchStateGlobal = $launchIndex + $launchState.Index
$hostText = $hostText.Remove($launchStateGlobal, $launchState.Length).Insert($launchStateGlobal, '                State = BrowserSessionState.Ready,')
Write-Normalized $hostPath $hostText
Write-Host 'PATCHED: New Chrome becomes READY only after launch endpoint probe'

# 4) Add one bounded liveness probe helper. V8E already adds duplicate-tab cleanup; insert before it.
$hostText = Read-Normalized $hostPath
if ($hostText.Contains('private static async Task<bool> ProbeConnectionAsync', [StringComparison]::Ordinal)) {
    throw 'PATCH_CONTRACT_MISMATCH: endpoint probe helper already exists before V9b.'
}
$helperAnchor = '    private static async Task CloseOtherChatGptPagesAsync(IBrowserContext context, IPage selected)'
$helperIndex = $hostText.IndexOf($helperAnchor, [StringComparison]::Ordinal)
if ($helperIndex -lt 0) { throw 'PATCH_CONTRACT_MISMATCH: V8E helper anchor missing.' }
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

            var pages = connection.Browser.Contexts.SelectMany(x => x.Pages).Where(x => !x.IsClosed).ToArray();
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
Write-Host 'PATCHED: Added bounded DevTools/Playwright endpoint probe'

# 5) Visibility is UI state only. It must never manufacture or erase endpoint verification.
$sessionsText = Read-Normalized $sessionsPath
$visibilityAnchor = '    private async Task<SessionActionResult> SetVisibilityAsync('
$visibilityIndex = $sessionsText.IndexOf($visibilityAnchor, [StringComparison]::Ordinal)
if ($visibilityIndex -lt 0) { throw 'PATCH_CONTRACT_MISMATCH: SetVisibilityAsync anchor missing.' }
$visibilityTail = $sessionsText.Substring($visibilityIndex)
$statePattern = '(?m)^\s*var state\s*=\s*visibility\s*==\s*BrowserVisibility\.Hidden\s*\?\s*BrowserSessionState\.Hidden\s*:\s*BrowserSessionState\.Visible;\s*$'
$stateMatches = [regex]::Matches($visibilityTail, $statePattern)
if ($stateMatches.Count -ne 1) { throw "PATCH_CONTRACT_MISMATCH: SetVisibility state assignment found $($stateMatches.Count)." }
$stateMatch = $stateMatches[0]
$stateGlobal = $visibilityIndex + $stateMatch.Index
$stateReplacement = @'
        var state = runtime.State switch
        {
            BrowserSessionState.Ready => BrowserSessionState.Ready,
            BrowserSessionState.FailedRequiresAttention => BrowserSessionState.FailedRequiresAttention,
            BrowserSessionState.Degraded => BrowserSessionState.Degraded,
            BrowserSessionState.Recovering => BrowserSessionState.Recovering,
            _ => visibility == BrowserVisibility.Hidden ? BrowserSessionState.Hidden : BrowserSessionState.Visible
        };
'@
$sessionsText = $sessionsText.Remove($stateGlobal, $stateMatch.Length).Insert($stateGlobal, $stateReplacement)
Write-Normalized $sessionsPath $sessionsText
Write-Host 'PATCHED: Open/Hide/BringToFront preserve endpoint verification state'

# 6) Presentation connection proof accepts only a positively verified READY Manager with process identity.
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
Write-Host 'PATCHED: ChromeConnectionProven requires READY + ProcessId + PCC ownership'

# 7) EndpointHealth reports the verified runtime fact. Global semantic ChatGPT health stays evidence-driven.
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
Write-Host 'PATCHED: EndpointHealth becomes Healthy only for verified READY runtime'

# 8) Embed exact build provenance so installed diagnostics no longer export SourceIdentity="".
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
$exportPattern = '(?m)^\s*private Task<string> ExportAsync\(\) => _services!\.ExportJson\(typeof\(RuntimeInspectorViewModel\)\.Assembly\.GetName\(\)\.Version\?\.ToString\(\) \?\? "unknown", Environment\.GetEnvironmentVariable\("PCC_SOURCE_SHA"\), 250, CancellationToken\.None\);\s*$'
$exportMatches = [regex]::Matches($screenText, $exportPattern)
if ($exportMatches.Count -ne 1) { throw "PATCH_CONTRACT_MISMATCH: Runtime diagnostic ExportAsync found $($exportMatches.Count)." }
$exportReplacement = '    private Task<string> ExportAsync() => _services!.ExportJson(typeof(RuntimeInspectorViewModel).Assembly.GetName().Version?.ToString() ?? "unknown", BuildSourceIdentity.Value, 250, CancellationToken.None);'
$screenText = [regex]::Replace($screenText, $exportPattern, { param($m) $exportReplacement }, 1)
Write-Normalized $screenPath $screenText
Write-Host "PATCHED: Embedded diagnostic SourceIdentity=$sourceSha"

# 9) Focused regressions for the exact false-ready path observed on the installed machine.
$browserTest = @'
using Xunit;

namespace PCCExecutive.Browser.Tests;

public sealed class EndpointTruthV9bBuildTests
{
    [Fact]
    public void Runtime_host_requires_real_page_roundtrips_for_launch_and_recovery()
    {
        var source = ReadRepoFile("src", "PCCExecutive.Browser", "PlaywrightChromeRuntimeHost.cs");
        Assert.Contains("private static async Task<bool> ProbeConnectionAsync", source, StringComparison.Ordinal);
        Assert.Contains("connection.Browser.IsConnected", source, StringComparison.Ordinal);
        Assert.Contains("EvaluateAsync<string>(\"() => document.readyState\")", source, StringComparison.Ordinal);
        Assert.Contains("ProbeConnectionAsync(existing, runtime.ProviderConversationIdentity", source, StringComparison.Ordinal);
        Assert.Contains("ProbeConnectionAsync(recoveredConnection, runtime.ProviderConversationIdentity", source, StringComparison.Ordinal);
        Assert.Contains("ProbeConnectionAsync(launchedConnection, request.ProviderConversationIdentity", source, StringComparison.Ordinal);
        Assert.DoesNotContain("if (_connections.TryGetValue(runtime.RuntimeId, out var existing) && !existing.Process.HasExited) return true;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Visibility_cannot_overwrite_verified_or_failed_lifecycle_state()
    {
        var source = ReadRepoFile("src", "PCCExecutive.Browser", "OwnershipAndSessions.cs");
        Assert.Contains("BrowserSessionState.Ready => BrowserSessionState.Ready", source, StringComparison.Ordinal);
        Assert.Contains("BrowserSessionState.FailedRequiresAttention => BrowserSessionState.FailedRequiresAttention", source, StringComparison.Ordinal);
        Assert.Contains("BrowserSessionState.Recovering => BrowserSessionState.Recovering", source, StringComparison.Ordinal);
    }

    private static string ReadRepoFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PCCExecutive.sln"))) directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(new[] { directory!.FullName }.Concat(parts).ToArray()));
    }
}
'@
Write-Normalized $browserTestPath $browserTest

$appTest = @'
using PCCExecutive.App.Presentation;
using Xunit;

namespace PCCExecutive.App.Tests;

public sealed class EndpointTruthV9bRegressionTests
{
    [Fact]
    public void Visible_owned_manager_is_not_connection_proof()
    {
        var snapshot = Snapshot("VISIBLE", 4242, HealthState.Unknown);
        Assert.False(snapshot.ChromeConnectionProven);
    }

    [Fact]
    public void Ready_owned_manager_with_process_identity_is_connection_proof()
    {
        var snapshot = Snapshot("READY", 4242, HealthState.Healthy);
        Assert.True(snapshot.ChromeConnectionProven);
        Assert.Equal("CHROME CONNECTED", snapshot.HealthText);
    }

    [Fact]
    public void Ready_without_process_identity_is_not_connection_proof()
    {
        var snapshot = Snapshot("READY", null, HealthState.Healthy);
        Assert.False(snapshot.ChromeConnectionProven);
    }

    private static RuntimeSnapshot Snapshot(string state, int? processId, HealthState health) => new(
        true, true, "Integrated runtime", HealthState.Unknown, "READY", "Manager planning",
        0, 0, CompletionMode.Running, 0, 0, 0, 0, "NORMAL", "handoff", "flow", false,
        ProviderMode.BrowserWeb, DispatchSettingsSummary.ProductDefaults,
        new UpdateSummary("0.1.0", null, "ok", "ok", "ok", "ok", false),
        Array.Empty<ProjectSummary>(),
        new[] { new SessionSummary("runtime-v9b", "Manager", "Manager", state, SessionVisibility.Visible, "conversation", DateTimeOffset.UtcNow, true, processId, health) },
        Array.Empty<WorkerSummary>(), Array.Empty<TaskSummary>(), Array.Empty<EvidenceGateSummary>(), Array.Empty<AttentionSummary>(), Array.Empty<RecoveryEventSummary>());
}
'@
Write-Normalized $appTestPath $appTest
Write-Host 'PATCHED: Added endpoint-truth V9b regressions'

$finalHost = Read-Normalized $hostPath
$finalSessions = Read-Normalized $sessionsPath
$finalModels = Read-Normalized $modelsPath
$finalGateway = Read-Normalized $gatewayPath
$finalScreen = Read-Normalized $screenPath
if (-not $finalHost.Contains('EvaluateAsync<string>("() => document.readyState")', [StringComparison]::Ordinal)) { throw 'V9B_ASSERTION_FAILED: Playwright roundtrip missing.' }
if (-not $finalHost.Contains('ProbeConnectionAsync(launchedConnection', [StringComparison]::Ordinal)) { throw 'V9B_ASSERTION_FAILED: launch probe missing.' }
if (-not $finalHost.Contains('ProbeConnectionAsync(recoveredConnection', [StringComparison]::Ordinal)) { throw 'V9B_ASSERTION_FAILED: reconnect probe missing.' }
if (-not $finalSessions.Contains('BrowserSessionState.Ready => BrowserSessionState.Ready', [StringComparison]::Ordinal)) { throw 'V9B_ASSERTION_FAILED: visibility preservation missing.' }
if (-not $finalModels.Contains('string.Equals(x.State, "READY", StringComparison.Ordinal)', [StringComparison]::Ordinal)) { throw 'V9B_ASSERTION_FAILED: READY-only proof missing.' }
if (-not $finalModels.Contains('x.ProcessId is > 0', [StringComparison]::Ordinal)) { throw 'V9B_ASSERTION_FAILED: process proof missing.' }
if (-not $finalGateway.Contains('BrowserSessionState.Ready => HealthState.Healthy', [StringComparison]::Ordinal)) { throw 'V9B_ASSERTION_FAILED: endpoint health projection missing.' }
if (-not $finalScreen.Contains('BuildSourceIdentity.Value', [StringComparison]::Ordinal)) { throw 'V9B_ASSERTION_FAILED: embedded source identity missing.' }
Write-Host 'ENDPOINT_TRUTH_V9B_FIX_APPLIED'
