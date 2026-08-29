[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$InstallerPath,
    [Parameter(Mandatory)] [string]$ExpectedVersion,
    [string]$ExpectedSourceSha,
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'PCC Executive Smoke\Fresh'),
    [string]$DataRoot = (Join-Path $env:LOCALAPPDATA 'PCC Executive'),
    [string]$EvidencePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$installer = (Resolve-Path -LiteralPath $InstallerPath).Path
$app = Join-Path $InstallRoot 'PCCExecutive.exe'
$database = Join-Path $DataRoot 'state\pcc-executive.db'
if (-not (Test-Path -LiteralPath $app)) { throw 'REPAIR_SMOKE_EXISTING_APP_MISSING.' }
if (-not (Test-Path -LiteralPath $database)) { throw 'REPAIR_SMOKE_EXISTING_DATABASE_MISSING.' }

$proofRoot = Join-Path $DataRoot 'acceptance\same-version-repair'
New-Item -ItemType Directory -Path $proofRoot -Force | Out-Null
$sentinel = [ordered]@{
    Project = 'PCCEXECUTIVE'
    Setting = 'BrowserFirst'
    DiagnosticCorrelationId = [Guid]::NewGuid().ToString('D')
    History = @('ChromeReady', 'ProjectBound', 'ManagerCurrent')
}
$sentinelPath = Join-Path $proofRoot 'guided-runtime-state.json'
$sentinel | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $sentinelPath -Encoding UTF8
$sentinelHashBefore = (Get-FileHash -LiteralPath $sentinelPath -Algorithm SHA256).Hash
$databaseHashBefore = (Get-FileHash -LiteralPath $database -Algorithm SHA256).Hash
$databaseCreationBefore = (Get-Item -LiteralPath $database).CreationTimeUtc

$repair = Start-Process -FilePath $installer -ArgumentList @(
    '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', "/DIR=`"$InstallRoot`""
) -Wait -PassThru
if ($repair.ExitCode -ne 0) { throw "Same-version repair failed: exit=$($repair.ExitCode)" }

if (-not (Test-Path -LiteralPath $sentinelPath)) { throw 'REPAIR_SMOKE_GUIDED_STATE_DELETED.' }
if (-not (Test-Path -LiteralPath $database)) { throw 'REPAIR_SMOKE_DATABASE_DELETED.' }
if ((Get-FileHash -LiteralPath $sentinelPath -Algorithm SHA256).Hash -ne $sentinelHashBefore) {
    throw 'REPAIR_SMOKE_GUIDED_STATE_CHANGED.'
}
if ((Get-FileHash -LiteralPath $database -Algorithm SHA256).Hash -ne $databaseHashBefore) {
    throw 'REPAIR_SMOKE_DATABASE_CHANGED_DURING_INSTALL.'
}
if ((Get-Item -LiteralPath $database).CreationTimeUtc -ne $databaseCreationBefore) {
    throw 'REPAIR_SMOKE_DATABASE_REPLACED.'
}

$provenancePath = Join-Path $InstallRoot 'build-provenance.json'
if (-not (Test-Path -LiteralPath $provenancePath)) { throw 'REPAIR_SMOKE_PROVENANCE_MISSING.' }
$provenance = Get-Content -Raw -LiteralPath $provenancePath | ConvertFrom-Json
if ([string]$provenance.Version -ne $ExpectedVersion) { throw 'REPAIR_SMOKE_VERSION_MISMATCH.' }
if (-not [string]::IsNullOrWhiteSpace($ExpectedSourceSha) -and [string]$provenance.SourceSha -ne $ExpectedSourceSha) {
    throw 'REPAIR_SMOKE_SOURCE_SHA_MISMATCH.'
}

$env:PCCEXECUTIVE_SMOKE_MODE = '1'
$process = Start-Process -FilePath $app -ArgumentList @('--installer-smoke') -PassThru
$windowObserved = $false
$deadline = [DateTimeOffset]::UtcNow.AddSeconds(15)
try {
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $process.Refresh()
        if ($process.HasExited) {
            if ($process.ExitCode -ne 0) { throw "REPAIR_SMOKE_APP_FAILED: exit=$($process.ExitCode)" }
            break
        }
        if ($process.MainWindowHandle -ne [IntPtr]::Zero) { $windowObserved = $true; break }
        Start-Sleep -Milliseconds 250
    }
    if (-not $windowObserved) { throw 'REPAIR_SMOKE_WPF_WINDOW_NOT_OBSERVED.' }
}
finally {
    if (-not $process.HasExited) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        $process.WaitForExit(5000) | Out-Null
    }
}

$evidence = [ordered]@{
    State = 'PASS'
    Kind = 'SAME_VERSION_REPAIR'
    Version = $ExpectedVersion
    SourceSha = [string]$provenance.SourceSha
    InstallRoot = $InstallRoot
    DataRoot = $DataRoot
    DatabaseHashBefore = $databaseHashBefore.ToLowerInvariant()
    DatabaseHashAfter = (Get-FileHash -LiteralPath $database -Algorithm SHA256).Hash.ToLowerInvariant()
    GuidedStateHash = $sentinelHashBefore.ToLowerInvariant()
    WindowObserved = $windowObserved
    ObservedAt = [DateTimeOffset]::UtcNow.ToString('o')
}
if (-not [string]::IsNullOrWhiteSpace($EvidencePath)) {
    $parent = Split-Path $EvidencePath -Parent
    if ($parent) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    $evidence | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $EvidencePath -Encoding UTF8
}
Write-Host "SAME_VERSION_REPAIR_SMOKE_PASS version=$ExpectedVersion sourceSha=$($provenance.SourceSha)"
