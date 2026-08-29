[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PackagePath,
    [Parameter(Mandatory)]
    [string]$ManifestPath,
    [string]$InstallRoot = (Join-Path $env:ProgramFiles 'PCC Executive'),
    [string]$DataRoot = (Join-Path $env:LOCALAPPDATA 'PCC Executive'),
    [switch]$SimulatePostInstallHealthFailure
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$stageRecordPath = & (Join-Path $PSScriptRoot 'Stage-Update.ps1') -PackagePath $PackagePath -ManifestPath $ManifestPath
if ($LASTEXITCODE -ne 0) { throw 'Update staging/verification failed.' }

$stageRecord = Get-Content $stageRecordPath -Raw | ConvertFrom-Json
$manifest = Get-Content $stageRecord.ManifestPath -Raw | ConvertFrom-Json

$attemptId = [Guid]::NewGuid().ToString('N')
$attemptRoot = Join-Path $DataRoot "Backups\update-$attemptId"
New-Item -ItemType Directory -Path $attemptRoot -Force | Out-Null

$attempt = [ordered]@{
    AttemptId = $attemptId
    FromVersion = $null
    ToVersion = $manifest.version
    SourceSha = $manifest.sourceSha
    PackageIdentity = $manifest.packageIdentity
    StartedAt = [DateTimeOffset]::UtcNow.ToString('o')
    State = 'PREPARING'
}
$attemptPath = Join-Path $attemptRoot 'update-attempt.json'

$provenancePath = Join-Path $InstallRoot 'build-provenance.json'
if (Test-Path $provenancePath) {
    $current = Get-Content $provenancePath -Raw | ConvertFrom-Json
    $attempt.FromVersion = [string]$current.Version
}
$attempt | ConvertTo-Json -Depth 4 | Set-Content $attemptPath -Encoding UTF8

$appExe = Join-Path $InstallRoot 'PCCExecutive.exe'
$updaterExe = Join-Path $InstallRoot 'updater\PCCExecutive.Updater.exe'
if (-not (Test-Path $appExe)) { throw "INSTALLED_APP_NOT_FOUND: $appExe" }
if (-not (Test-Path $updaterExe)) {
    throw 'UPGRADE_BLOCKED: installed safe-upgrade helper is missing; no files were replaced.'
}

$prepareArgs = @('prepare-update', '--backup-root', $attemptRoot, '--attempt', $attemptId)
& $updaterExe @prepareArgs
$prepareExit = $LASTEXITCODE
if ($prepareExit -ne 0) {
    throw "UPGRADE_BLOCKED: checkpoint/runtime shutdown failed with exit code $prepareExit."
}

$checkpoint = Join-Path $attemptRoot 'checkpoint.json'
if (-not (Test-Path $checkpoint)) {
    throw 'UPGRADE_BLOCKED: updater reported success but no checkpoint.json was produced.'
}

$previousInstaller = $null
if ($attempt.FromVersion) {
    $candidate = Join-Path $DataRoot "InstallerCache\PCCExecutive-$($attempt.FromVersion)-Setup-x64.exe"
    if (Test-Path $candidate) { $previousInstaller = $candidate }
}

$attempt.State = 'INSTALLING'
$attempt | ConvertTo-Json -Depth 4 | Set-Content $attemptPath -Encoding UTF8

$install = Start-Process -FilePath $stageRecord.PackagePath -ArgumentList @('/SILENT','/SUPPRESSMSGBOXES','/NORESTART') -Wait -PassThru
if ($install.ExitCode -ne 0) {
    $attempt.State = 'INSTALL_FAILED'
    $attempt | ConvertTo-Json -Depth 4 | Set-Content $attemptPath -Encoding UTF8
    throw "Installer failed with exit code $($install.ExitCode). Existing checkpoint is preserved at $attemptRoot"
}

$newUpdater = Join-Path $InstallRoot 'updater\PCCExecutive.Updater.exe'
if (-not (Test-Path $newUpdater)) {
    $healthExit = 9001
}
elseif ($SimulatePostInstallHealthFailure) {
    if ($env:PCCEXECUTIVE_ALLOW_FAILURE_INJECTION -ne '1') {
        throw 'Failure injection is allowed only when PCCEXECUTIVE_ALLOW_FAILURE_INJECTION=1.'
    }
    $healthExit = 9002
}
else {
    $verifyArgs = @('post-install-verify', '--attempt', $attemptId, '--backup-root', $attemptRoot)
    & $newUpdater @verifyArgs
    $healthExit = $LASTEXITCODE
}

if ($healthExit -eq 0) {
    $attempt.State = 'VERIFIED'
    $attempt.CompletedAt = [DateTimeOffset]::UtcNow.ToString('o')
    $attempt | ConvertTo-Json -Depth 4 | Set-Content $attemptPath -Encoding UTF8
    Write-Output $attemptPath
    exit 0
}

$attempt.State = 'HEALTH_FAILED_ROLLBACK_REQUIRED'
$attempt.HealthExitCode = $healthExit
$attempt | ConvertTo-Json -Depth 4 | Set-Content $attemptPath -Encoding UTF8

if (-not $previousInstaller) {
    throw "POST_INSTALL_HEALTH_FAILED: rollback installer unavailable. Checkpoint preserved at $attemptRoot"
}

$rollbackInstall = Start-Process -FilePath $previousInstaller -ArgumentList @('/SILENT','/SUPPRESSMSGBOXES','/NORESTART') -Wait -PassThru
if ($rollbackInstall.ExitCode -ne 0) {
    throw "ROLLBACK_INSTALLER_FAILED: exit=$($rollbackInstall.ExitCode). Checkpoint preserved at $attemptRoot"
}

$restoredUpdater = Join-Path $InstallRoot 'updater\PCCExecutive.Updater.exe'
if (-not (Test-Path $restoredUpdater)) {
    throw "ROLLBACK_DATA_RESTORE_BLOCKED: previous binaries restored but updater helper is missing. Checkpoint: $attemptRoot"
}

& $restoredUpdater 'restore-update-checkpoint' '--backup-root' $attemptRoot '--attempt' $attemptId
$restoreExit = $LASTEXITCODE
if ($restoreExit -ne 0) {
    throw "ROLLBACK_DATA_RESTORE_FAILED: exit=$restoreExit. Checkpoint preserved at $attemptRoot"
}

$attempt.State = 'ROLLED_BACK'
$attempt.CompletedAt = [DateTimeOffset]::UtcNow.ToString('o')
$attempt | ConvertTo-Json -Depth 4 | Set-Content $attemptPath -Encoding UTF8
throw "POST_INSTALL_HEALTH_FAILED_ROLLED_BACK: previous version and checkpoint were restored. Attempt=$attemptId"
