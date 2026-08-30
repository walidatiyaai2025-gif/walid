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

# This continuation is valid only after the verified V9C prefix reached its known property-anchor mismatch.
$hostText = Read-Normalized $hostPath
Assert-Contains $hostText 'ProbeConnectionAsync(existing, runtime.ProviderConversationIdentity, cancellationToken)' 'V9C cached endpoint probe prefix is missing.'
Assert-Contains $hostText 'ProbeConnectionAsync(recoveredConnection, runtime.ProviderConversationIdentity, cancellationToken)' 'V9C recovery endpoint probe prefix is missing.'
Assert-Contains $hostText 'ProbeConnectionAsync(launchedConnection, request.ProviderConversationIdentity, cancellationToken)' 'V9C launch endpoint probe prefix is missing.'
Assert-Contains $hostText 'EvaluateAsync<string>("() => document.readyState")' 'V9C live JavaScript roundtrip is missing.'
Assert-Contains $hostText 'State = BrowserSessionState.Ready,' 'V9C launch READY-after-probe state is missing.'

$sessionsText = Read-Normalized $sessionsPath
Assert-Contains $sessionsText 'BrowserSessionState.Ready => BrowserSessionState.Ready' 'V9C visibility READY preservation is missing.'
Assert-Contains $sessionsText 'BrowserSessionState.FailedRequiresAttention => BrowserSessionState.FailedRequiresAttention' 'V9C visibility failure preservation is missing.'

# Replace ChromeConnectionProven structurally between two stable member anchors.
$modelsText = Read-Normalized $modelsPath
$proofStart = '    public bool ChromeConnectionProven => GatewayBound && Sessions.Any(x =>'
$proofStartIndex = $modelsText.IndexOf($proofStart, [StringComparison]::Ordinal)
if ($proofStartIndex -lt 0) { throw 'PATCH_CONTRACT_MISMATCH: ChromeConnectionProven start anchor missing.' }
$healthTextAnchor = '    public string HealthText =>'
$healthTextIndex = $modelsText.IndexOf($healthTextAnchor, $proofStartIndex, [StringComparison]::Ordinal)
if ($healthTextIndex -lt 0) { throw 'PATCH_CONTRACT_MISMATCH: HealthText boundary missing after ChromeConnectionProven.' }
$proofReplacement = @'
    public bool ChromeConnectionProven => GatewayBound && Sessions.Any(x =>
        x.IsPccOwned &&
        string.Equals(x.Role, "Manager", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(x.State, "READY", StringComparison.Ordinal) &&
        x.ProcessId is > 0);

'@
$modelsText = $modelsText.Remove($proofStartIndex, $healthTextIndex - $proofStartIndex).Insert($proofStartIndex, $proofReplacement)
Write-Normalized $modelsPath $modelsText
Write-Host 'PATCHED: ChromeConnectionProven requires verified READY Manager runtime'

# Endpoint health becomes Healthy only for BrowserSessionState.Ready, which V9C writes only after a live probe.
$gatewayText = Read-Normalized $gatewayPath
$healthStart = '    private static HealthState MapSessionHealth(BrowserSessionState state) => state switch'
$healthStartIndex = $gatewayText.IndexOf($healthStart, [StringComparison]::Ordinal)
if ($healthStartIndex -lt 0) { throw 'PATCH_CONTRACT_MISMATCH: MapSessionHealth start anchor missing.' }
$aggregateAnchor = '    private static HealthState AggregateHealth('
$aggregateIndex = $gatewayText.IndexOf($aggregateAnchor, $healthStartIndex, [StringComparison]::Ordinal)
if ($aggregateIndex -lt 0) { throw 'PATCH_CONTRACT_MISMATCH: AggregateHealth boundary missing after MapSessionHealth.' }
$healthReplacement = @'
    private static HealthState MapSessionHealth(BrowserSessionState state) => state switch
    {
        BrowserSessionState.Ready => HealthState.Healthy,
        BrowserSessionState.Recovering => HealthState.Recovering,
        BrowserSessionState.Degraded or BrowserSessionState.FailedRequiresAttention => HealthState.Unknown,
        _ => HealthState.Unknown
    };

'@
$gatewayText = $gatewayText.Remove($healthStartIndex, $aggregateIndex - $healthStartIndex).Insert($healthStartIndex, $healthReplacement)
Write-Normalized $gatewayPath $gatewayText
Write-Host 'PATCHED: EndpointHealth reflects verified READY state'

# Embed exact build source provenance into the installed diagnostic payload.
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

# Final deterministic assertions for the exact user-visible false-ready path.
$modelsText = Read-Normalized $modelsPath
Assert-Contains $modelsText 'string.Equals(x.State, "READY", StringComparison.Ordinal)' 'strict Chrome connection proof missing.'
Assert-Contains $modelsText 'x.ProcessId is > 0' 'Chrome connection proof process identity requirement missing.'
$gatewayText = Read-Normalized $gatewayPath
Assert-Contains $gatewayText 'BrowserSessionState.Ready => HealthState.Healthy' 'verified endpoint health mapping missing.'
$screenText = Read-Normalized $screenPath
Assert-Contains $screenText 'BuildSourceIdentity.Value' 'installed SourceIdentity binding missing.'

Write-Host 'ENDPOINT_TRUTH_V9D_CONTINUATION_APPLIED'
