[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

function Read-Normalized([string]$Path) {
    (Get-Content $Path -Raw).Replace("`r`n", "`n")
}

function Replace-ExactlyOnce {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Old,
        [Parameter(Mandatory)][string]$New,
        [Parameter(Mandatory)][string]$Description
    )
    $text = Read-Normalized $Path
    $count = ([regex]::Matches($text, [regex]::Escape($Old))).Count
    if ($count -ne 1) {
        throw "PATCH_CONTRACT_MISMATCH: $Description expected exactly one match in $Path, found $count."
    }
    Set-Content -Path $Path -Value $text.Replace($Old, $New) -Encoding utf8 -NoNewline
    Write-Host "PATCHED: $Description"
}

$hostPath = Join-Path $repoRoot 'src/PCCExecutive.Browser/PlaywrightChromeRuntimeHost.cs'
$sessionsPath = Join-Path $repoRoot 'src/PCCExecutive.Browser/OwnershipAndSessions.cs'
$modelsPath = Join-Path $repoRoot 'src/PCCExecutive.App/Presentation/PresentationModels.cs'
$gatewayPath = Join-Path $repoRoot 'src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs'
$screenPath = Join-Path $repoRoot 'src/PCCExecutive.App/ViewModels/ScreenViewModels.cs'
$browserTestPath = Join-Path $repoRoot 'tests/PCCExecutive.Browser.Tests/EndpointTruthV9BuildTests.cs'
$appTestPath = Join-Path $repoRoot 'tests/PCCExecutive.App.Tests/EndpointTruthV9RegressionTests.cs'
$sourceIdentityPath = Join-Path $repoRoot 'src/PCCExecutive.App/ViewModels/BuildSourceIdentity.g.cs'

# V6C improved the old blind PID shortcut, but still accepted a page existing in the
# Playwright graph without proving that the Browser/CDP channel could execute a command.
# The user's post-install diagnostic showed ConnectChrome completing repeatedly while
# EndpointHealth stayed Unknown. Every recovery now requires an actual Playwright roundtrip.
$hostText = Read-Normalized $hostPath
$recoverAnchor = '    public async Task<bool> RecoverAsync(BrowserRuntimeRecord runtime, CancellationToken cancellationToken = default)'
$recoverIndex = $hostText.IndexOf($recoverAnchor, [StringComparison]::Ordinal)
if ($recoverIndex -lt 0) { throw 'PATCH_CONTRACT_MISMATCH: RecoverAsync anchor missing.' }
$existingPattern = '(?ms)^        if \(_connections\.TryGetValue\(runtime\.RuntimeId, out var existing\) && !existing\.Process\.HasExited\)\s*\{.*?^            _connections\.TryRemove\(runtime\.RuntimeId, out _\);\s*^        \}'
$existingMatches = [regex]::Matches($hostText.Substring($recoverIndex), $existingPattern)
if ($existingMatches.Count -ne 1) {
    throw "PATCH_CONTRACT_MISMATCH: cached PCC connection block expected exactly one match after RecoverAsync, found $($existingMatches.Count)."
}
$existingMatch = $existingMatches[0]
$existingIndex = $recoverIndex + $existingMatch.Index
$newExistingConnection = @'
        if (_connections.TryGetValue(runtime.RuntimeId, out var existing))
        {
            if (await ProbeConnectionAsync(existing, runtime.ProviderConversationIdentity, cancellationToken).ConfigureAwait(false))
                return true;

            // A live PID, cached IBrowser, or cached IPage is not endpoint proof. Drop only the
            // in-memory PCC connection and continue through the normal CDP reconnect/replacement path.
            _connections.TryRemove(runtime.RuntimeId, out _);
        }
'@
$hostText = $hostText.Remove($existingIndex, $existingMatch.Length).Insert($existingIndex, $newExistingConnection)
Set-Content -Path $hostPath -Value $hostText -Encoding utf8 -NoNewline
Write-Host 'PATCHED: Require a live Playwright roundtrip for cached PCC connections structurally'

$oldRecoveryBind = @'
        _connections[runtime.RuntimeId] = new Connection(process, browser, page, runtime.ContextIdentity ?? Guid.NewGuid().ToString("N"), runtime.ProfilePath);
        return true;
'@
$newRecoveryBind = @'
        var recoveredConnection = new Connection(process, browser, page, runtime.ContextIdentity ?? Guid.NewGuid().ToString("N"), runtime.ProfilePath);
        if (!await ProbeConnectionAsync(recoveredConnection, runtime.ProviderConversationIdentity, cancellationToken).ConfigureAwait(false))
            return false;

        _connections[runtime.RuntimeId] = recoveredConnection;
        return true;
'@
Replace-ExactlyOnce $hostPath $oldRecoveryBind $newRecoveryBind 'Verify the newly reconnected CDP endpoint before marking recovery successful'

$hostText = Read-Normalized $hostPath
$probeAnchor = '    private static async Task CloseOtherChatGptPagesAsync(IBrowserContext context, IPage selected)'
$probeIndex = $hostText.IndexOf($probeAnchor, [StringComparison]::Ordinal)
if ($probeIndex -lt 0) { throw 'PATCH_CONTRACT_MISMATCH: V8E duplicate-tab helper anchor missing.' }
if ($hostText.Contains('private static async Task<bool> ProbeConnectionAsync', [StringComparison]::Ordinal)) {
    throw 'PATCH_CONTRACT_MISMATCH: endpoint probe helper already exists.'
}
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
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return false;
        return string.Equals(uri.Host, "chatgpt.com", StringComparison.OrdinalIgnoreCase) ||
               uri.Host.EndsWith(".chatgpt.com", StringComparison.OrdinalIgnoreCase);
    }

'@
$hostText = $hostText.Insert($probeIndex, $probeHelper)
Set-Content -Path $hostPath -Value $hostText -Encoding utf8 -NoNewline
Write-Host 'PATCHED: Added bounded endpoint liveness probe'

# Visibility is presentation state, not endpoint verification. Preserve verified Ready and
# preserve failure/recovery states so Open/Bring-to-front/Hide can never manufacture readiness.
$oldVisibility = @'
        var state = visibility == BrowserVisibility.Hidden ? BrowserSessionState.Hidden : BrowserSessionState.Visible;
        var updated = runtime with { Visibility = visibility, State = state, LastActivityAt = DateTimeOffset.UtcNow };
'@
$newVisibility = @'
        var state = runtime.State switch
        {
            BrowserSessionState.Ready => BrowserSessionState.Ready,
            BrowserSessionState.FailedRequiresAttention => BrowserSessionState.FailedRequiresAttention,
            BrowserSessionState.Degraded => BrowserSessionState.Degraded,
            BrowserSessionState.Recovering => BrowserSessionState.Recovering,
            _ => visibility == BrowserVisibility.Hidden ? BrowserSessionState.Hidden : BrowserSessionState.Visible
        };
        var updated = runtime with { Visibility = visibility, State = state, LastActivityAt = DateTimeOffset.UtcNow };
'@
Replace-ExactlyOnce $sessionsPath $oldVisibility $newVisibility 'Keep visibility actions from overwriting endpoint-verification state'

# A usable Chrome connection is now represented only by the state written after RecoverOrphanAsync
# receives a positive host probe. Legacy HIDDEN/VISIBLE records are intentionally not accepted.
$oldChromeProof = @'
    public bool ChromeConnectionProven => GatewayBound && Sessions.Any(x =>
        x.IsPccOwned &&
        string.Equals(x.Role, "Manager", StringComparison.OrdinalIgnoreCase) &&
        x.State is "READY" or "HIDDEN" or "VISIBLE" or "ACTIVE");
'@
$newChromeProof = @'
    public bool ChromeConnectionProven => GatewayBound && Sessions.Any(x =>
        x.IsPccOwned &&
        string.Equals(x.Role, "Manager", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(x.State, "READY", StringComparison.Ordinal) &&
        x.ProcessId is > 0);
'@
Replace-ExactlyOnce $modelsPath $oldChromeProof $newChromeProof 'Require verified READY state and process identity for ChromeConnectionProven'

$oldSessionHealth = @'
    private static HealthState MapSessionHealth(BrowserSessionState state) => state switch
    {
        BrowserSessionState.Recovering => HealthState.Recovering,
        BrowserSessionState.Degraded or BrowserSessionState.FailedRequiresAttention => HealthState.Unknown,
        _ => HealthState.Unknown
    };
'@
$newSessionHealth = @'
    private static HealthState MapSessionHealth(BrowserSessionState state) => state switch
    {
        BrowserSessionState.Ready => HealthState.Healthy,
        BrowserSessionState.Recovering => HealthState.Recovering,
        BrowserSessionState.Degraded or BrowserSessionState.FailedRequiresAttention => HealthState.Unknown,
        _ => HealthState.Unknown
    };
'@
Replace-ExactlyOnce $gatewayPath $oldSessionHealth $newSessionHealth 'Expose verified endpoint readiness as EndpointHealth=Healthy without fabricating semantic global health'

# Embed exact build provenance into the installed application. Environment variables from CI do not
# exist on the user's machine, which is why previous diagnostics exported SourceIdentity="".
$sourceSha = (git -C $repoRoot rev-parse HEAD).Trim().ToLowerInvariant()
if ($sourceSha -notmatch '^[0-9a-f]{40}$') { throw "SOURCE_IDENTITY_INVALID: $sourceSha" }
$sourceIdentity = @"
namespace PCCExecutive.App.ViewModels;

internal static class BuildSourceIdentity
{
    internal const string Value = "$sourceSha";
}
"@
Set-Content -Path $sourceIdentityPath -Value $sourceIdentity -Encoding utf8 -NoNewline
Write-Host "PATCHED: Embedded exact source identity $sourceSha"

$oldExport = '    private Task<string> ExportAsync() => _services!.ExportJson(typeof(RuntimeInspectorViewModel).Assembly.GetName().Version?.ToString() ?? "unknown", Environment.GetEnvironmentVariable("PCC_SOURCE_SHA"), 250, CancellationToken.None);'
$newExport = '    private Task<string> ExportAsync() => _services!.ExportJson(typeof(RuntimeInspectorViewModel).Assembly.GetName().Version?.ToString() ?? "unknown", BuildSourceIdentity.Value, 250, CancellationToken.None);'
Replace-ExactlyOnce $screenPath $oldExport $newExport 'Export exact installed-build SourceIdentity in runtime diagnostics'

$browserTest = @'
using Xunit;

namespace PCCExecutive.Browser.Tests;

public sealed class EndpointTruthV9BuildTests
{
    [Fact]
    public void Runtime_host_requires_a_real_Playwright_roundtrip_before_recovery_success()
    {
        var source = ReadRepoFile("src", "PCCExecutive.Browser", "PlaywrightChromeRuntimeHost.cs");

        Assert.Contains("private static async Task<bool> ProbeConnectionAsync", source, StringComparison.Ordinal);
        Assert.Contains("connection.Browser.IsConnected", source, StringComparison.Ordinal);
        Assert.Contains("EvaluateAsync<string>(\"() => document.readyState\")", source, StringComparison.Ordinal);
        Assert.Contains("WaitAsync(probeTimeout.Token)", source, StringComparison.Ordinal);
        Assert.Contains("IsChatGptUrl(selected.Url)", source, StringComparison.Ordinal);
        Assert.Contains("ProbeConnectionAsync(existing, runtime.ProviderConversationIdentity", source, StringComparison.Ordinal);
        Assert.Contains("ProbeConnectionAsync(recoveredConnection, runtime.ProviderConversationIdentity", source, StringComparison.Ordinal);
        Assert.DoesNotContain("existingPageIndex >= 0", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Visibility_actions_cannot_turn_unverified_or_failed_runtime_into_ready_state()
    {
        var source = ReadRepoFile("src", "PCCExecutive.Browser", "OwnershipAndSessions.cs");
        Assert.Contains("BrowserSessionState.Ready => BrowserSessionState.Ready", source, StringComparison.Ordinal);
        Assert.Contains("BrowserSessionState.FailedRequiresAttention => BrowserSessionState.FailedRequiresAttention", source, StringComparison.Ordinal);
        Assert.Contains("BrowserSessionState.Recovering => BrowserSessionState.Recovering", source, StringComparison.Ordinal);
    }

    private static string ReadRepoFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PCCExecutive.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(new[] { directory!.FullName }.Concat(parts).ToArray()));
    }
}
'@
Set-Content -Path $browserTestPath -Value $browserTest -Encoding utf8 -NoNewline
Write-Host 'PATCHED: Added endpoint-truth Browser build regressions'

$appTest = @'
using PCCExecutive.App.Presentation;
using Xunit;

namespace PCCExecutive.App.Tests;

public sealed class EndpointTruthV9RegressionTests
{
    [Fact]
    public void Visible_owned_manager_without_verified_ready_state_does_not_complete_Chrome()
    {
        var snapshot = Snapshot(new SessionSummary(
            "runtime-v9", "Manager", "Manager", "VISIBLE", SessionVisibility.Visible,
            "conversation", DateTimeOffset.UtcNow, true, 4242, HealthState.Unknown));

        Assert.False(snapshot.ChromeConnectionProven);
    }

    [Fact]
    public void Verified_ready_owned_manager_with_process_identity_completes_Chrome()
    {
        var snapshot = Snapshot(new SessionSummary(
            "runtime-v9", "Manager", "Manager", "READY", SessionVisibility.Visible,
            "conversation", DateTimeOffset.UtcNow, true, 4242, HealthState.Healthy));

        Assert.True(snapshot.ChromeConnectionProven);
        Assert.Equal("CHROME CONNECTED", snapshot.HealthText);
    }

    [Fact]
    public void Ready_without_process_identity_is_not_connection_proof()
    {
        var snapshot = Snapshot(new SessionSummary(
            "runtime-v9", "Manager", "Manager", "READY", SessionVisibility.Visible,
            "conversation", DateTimeOffset.UtcNow, true, null, HealthState.Healthy));

        Assert.False(snapshot.ChromeConnectionProven);
    }

    private static RuntimeSnapshot Snapshot(SessionSummary session) => new(
        true, true, "Integrated runtime", HealthState.Unknown, "READY", "Manager planning",
        0, 0, CompletionMode.Running, 0, 0, 0, 0, "NORMAL", "handoff", "flow", false,
        ProviderMode.BrowserWeb, DispatchSettingsSummary.ProductDefaults,
        new UpdateSummary("0.1.0", null, "ok", "ok", "ok", "ok", false),
        Array.Empty<ProjectSummary>(), new[] { session }, Array.Empty<WorkerSummary>(),
        Array.Empty<TaskSummary>(), Array.Empty<EvidenceGateSummary>(), Array.Empty<AttentionSummary>(),
        Array.Empty<RecoveryEventSummary>());
}
'@
Set-Content -Path $appTestPath -Value $appTest -Encoding utf8 -NoNewline
Write-Host 'PATCHED: Added endpoint-truth presentation regressions'

$finalHost = Read-Normalized $hostPath
$finalSessions = Read-Normalized $sessionsPath
$finalModels = Read-Normalized $modelsPath
$finalGateway = Read-Normalized $gatewayPath
$finalScreen = Read-Normalized $screenPath
if (-not $finalHost.Contains('EvaluateAsync<string>("() => document.readyState")', [StringComparison]::Ordinal)) { throw 'V9_ASSERTION_FAILED: live Playwright roundtrip missing.' }
if (-not $finalHost.Contains('connection.Browser.IsConnected', [StringComparison]::Ordinal)) { throw 'V9_ASSERTION_FAILED: browser connection check missing.' }
if ($finalHost.Contains('existingPageIndex >= 0', [StringComparison]::Ordinal)) { throw 'V9_ASSERTION_FAILED: V6C page-only readiness remains.' }
if (-not $finalSessions.Contains('BrowserSessionState.FailedRequiresAttention => BrowserSessionState.FailedRequiresAttention', [StringComparison]::Ordinal)) { throw 'V9_ASSERTION_FAILED: visibility failure-state preservation missing.' }
if (-not $finalModels.Contains('string.Equals(x.State, "READY", StringComparison.Ordinal)', [StringComparison]::Ordinal)) { throw 'V9_ASSERTION_FAILED: READY-only connection proof missing.' }
if (-not $finalModels.Contains('x.ProcessId is > 0', [StringComparison]::Ordinal)) { throw 'V9_ASSERTION_FAILED: process identity proof missing.' }
if (-not $finalGateway.Contains('BrowserSessionState.Ready => HealthState.Healthy', [StringComparison]::Ordinal)) { throw 'V9_ASSERTION_FAILED: endpoint health projection missing.' }
if (-not $finalScreen.Contains('BuildSourceIdentity.Value', [StringComparison]::Ordinal)) { throw 'V9_ASSERTION_FAILED: diagnostic source identity missing.' }
Write-Host 'ENDPOINT_TRUTH_V9_FIX_APPLIED'
