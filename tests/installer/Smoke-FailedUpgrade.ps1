[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PreviousInstallerPath,
    [Parameter(Mandatory)]
    [string]$CandidateInstallerPath,
    [Parameter(Mandatory)]
    [string]$CandidateManifestPath,
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'PCC Executive Smoke\FailedUpgrade'),
    [string]$DataRoot = (Join-Path $env:LOCALAPPDATA 'PCC Executive')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Remove-Item $InstallRoot -Recurse -Force -ErrorAction SilentlyContinue

$oldInstall = Start-Process -FilePath (Resolve-Path $PreviousInstallerPath).Path -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/CURRENTUSER',"/DIR=`"$InstallRoot`"") -Wait -PassThru
if ($oldInstall.ExitCode -ne 0) { throw "Previous-version install failed: exit=$($oldInstall.ExitCode)" }

$before = Get-Content (Join-Path $InstallRoot 'build-provenance.json') -Raw | ConvertFrom-Json
$env:PCCEXECUTIVE_ALLOW_FAILURE_INJECTION = '1'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$failedAsExpected = $false
try {
    & (Join-Path $repoRoot 'updater\Invoke-Upgrade.ps1') `
        -PackagePath $CandidateInstallerPath `
        -ManifestPath $CandidateManifestPath `
        -InstallRoot $InstallRoot `
        -DataRoot $DataRoot `
        -SimulatePostInstallHealthFailure
}
catch {
    $failedAsExpected = $true
}

if (-not $failedAsExpected) { throw 'Failure injection did not surface an upgrade failure.' }

$after = Get-Content (Join-Path $InstallRoot 'build-provenance.json') -Raw | ConvertFrom-Json
if ($after.Version -ne $before.Version -or $after.SourceSha -ne $before.SourceSha) {
    throw 'Failed upgrade did not restore the previous binary provenance.'
}

$backups = Get-ChildItem (Join-Path $DataRoot 'Backups') -Directory -Filter 'update-*' -ErrorAction SilentlyContinue
if (-not $backups) { throw 'Failed upgrade did not retain a recovery backup.' }

Write-Host "FAILED_UPGRADE_ROLLBACK_SMOKE_PASS restoredVersion=$($after.Version)"
