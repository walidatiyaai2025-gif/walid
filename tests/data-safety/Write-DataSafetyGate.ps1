[CmdletBinding()]
param(
    [string]$RepositoryRoot,
    [string]$PayloadRoot,
    [string]$OutputPath,
    [switch]$PersistenceTestsPassed,
    [switch]$InstallerLifecyclePassed,
    [switch]$UpgradeLifecyclePassed,
    [switch]$RollbackLifecyclePassed,
    [switch]$NotExecuted
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
} else {
    $RepositoryRoot = (Resolve-Path $RepositoryRoot).Path
}
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $RepositoryRoot 'artifacts/release-evidence/DATA_SAFETY.json'
}

$matrixPath = Join-Path $RepositoryRoot 'tests/data-safety/data-safety-matrix.json'
$matrix = Get-Content $matrixPath -Raw | ConvertFrom-Json
$failures = [System.Collections.Generic.List[string]]::new()
$packageFindings = [System.Collections.Generic.List[string]]::new()

if ([string]$matrix.gate -ne 'DATA_SAFETY') { $failures.Add('matrix:gate-identity') }
if (@($matrix.cases).Count -ne 25) { $failures.Add("matrix:expected-25-cases:actual-$(@($matrix.cases).Count)") }
$duplicateIds = @($matrix.cases | Group-Object id | Where-Object Count -gt 1)
if ($duplicateIds.Count -gt 0) { $failures.Add('matrix:duplicate-case-id') }

$schemaPath = Join-Path $RepositoryRoot 'src/PCCExecutive.Infrastructure/SCHEMA_VERSION'
$schema = if (Test-Path $schemaPath) { (Get-Content $schemaPath -Raw).Trim() } else { 'MISSING' }
if ($schema -ne '2') { $failures.Add("schema:expected-2:actual-$schema") }

$installerPath = Join-Path $RepositoryRoot 'installer/PCCExecutive.iss'
$updaterPath = Join-Path $RepositoryRoot 'updater/Invoke-Upgrade.ps1'
$backupServicePath = Join-Path $RepositoryRoot 'src/PCCExecutive.Infrastructure/VerifiedBackupService.cs'
$recoveryPath = Join-Path $RepositoryRoot 'src/PCCExecutive.Infrastructure/RecoveryIntegration.cs'
foreach ($required in @($installerPath,$updaterPath,$backupServicePath,$recoveryPath)) {
    if (-not (Test-Path $required)) { $failures.Add("required-source:missing:$required") }
}

if (Test-Path $installerPath) {
    $installer = Get-Content $installerPath -Raw
    foreach ($token in @('UsePreviousAppDir=yes','FULLCLEANUP=1','Preserving durable PCC Executive user data','prepare-installer-upgrade','post-install-verify')) {
        if (-not $installer.Contains($token)) { $failures.Add("installer-contract:missing:$token") }
    }
}

if (Test-Path $updaterPath) {
    $updater = Get-Content $updaterPath -Raw
    foreach ($token in @('prepare-update','checkpoint.json','HEALTH_FAILED_ROLLBACK_REQUIRED','restore-update-checkpoint','ROLLED_BACK')) {
        if (-not $updater.Contains($token)) { $failures.Add("updater-contract:missing:$token") }
    }
}

if (Test-Path $backupServicePath) {
    $backupSource = Get-Content $backupServicePath -Raw
    foreach ($token in @('SourceDatabaseId','schema-version:newer-than-application','Backup database identity does not match','-wal','-shm')) {
        if (-not $backupSource.Contains($token)) { $failures.Add("backup-contract:missing:$token") }
    }
}

if (Test-Path $recoveryPath) {
    $recovery = Get-Content $recoveryPath -Raw
    foreach ($token in @('SUBMITTED_UNKNOWN','PRAGMA wal_checkpoint(FULL)','CreateAndVerifyAsync','PRE_UPDATE_CHECKPOINT')) {
        if (-not $recovery.Contains($token)) { $failures.Add("recovery-contract:missing:$token") }
    }
}

$payloadAvailable = -not [string]::IsNullOrWhiteSpace($PayloadRoot) -and (Test-Path $PayloadRoot)
if ($payloadAvailable) {
    $payload = (Resolve-Path $PayloadRoot).Path
    $forbiddenDirectoryNames = @('User Data','BrowserProfiles','BrowserProfile','ChatGPTProfiles','ChatGPTProfile','browser-profiles','auth-state','storage-state','playwright-auth','Backups')
    $forbiddenExactFiles = @('Cookies','Cookies-journal','Login Data','Login Data-journal','Web Data','History','Preferences','Secure Preferences')
    foreach ($directory in Get-ChildItem -Path $payload -Recurse -Force -Directory -ErrorAction SilentlyContinue) {
        if ($forbiddenDirectoryNames -contains $directory.Name) {
            $packageFindings.Add("forbidden-directory:$([IO.Path]::GetRelativePath($payload,$directory.FullName))")
        }
    }
    foreach ($file in Get-ChildItem -Path $payload -Recurse -Force -File -ErrorAction SilentlyContinue) {
        $relative = [IO.Path]::GetRelativePath($payload,$file.FullName)
        $lower = $file.Name.ToLowerInvariant()
        if ($forbiddenExactFiles -contains $file.Name) { $packageFindings.Add("browser-profile-file:$relative"); continue }
        if ($lower.EndsWith('.db') -or $lower.EndsWith('.sqlite') -or $lower.EndsWith('.sqlite3') -or $lower.EndsWith('-wal') -or $lower.EndsWith('-shm')) {
            $packageFindings.Add("durable-state-file:$relative"); continue
        }
        if ($lower.EndsWith('.log')) { $packageFindings.Add("developer-log:$relative"); continue }
    }
    if ($packageFindings.Count -gt 0) {
        foreach ($finding in $packageFindings) { $failures.Add("package:$finding") }
    }
}

$caseResults = foreach ($case in $matrix.cases) {
    $status = 'PASS'
    $reason = 'Deterministic persistence acceptance is required and source contract is present.'
    switch ([string]$case.id) {
        'DS06' { if (-not $InstallerLifecyclePassed) { $status='BLOCKED_CI'; $reason='Installer reinstall lifecycle evidence not supplied.' } }
        'DS10' { if (-not $RollbackLifecyclePassed) { $status='BLOCKED_CI'; $reason='Failed-update rollback lifecycle evidence not supplied.' } }
        'DS15' { if (-not $InstallerLifecyclePassed) { $status='BLOCKED_CI'; $reason='Uninstall preservation lifecycle evidence not supplied.' } }
        'DS16' { if (-not $InstallerLifecyclePassed) { $status='BLOCKED_CI'; $reason='Reinstall rediscovery lifecycle evidence not supplied.' } }
        'DS17' {
            if (-not $payloadAvailable) { $status='BLOCKED_PACKAGE'; $reason='Published payload root was not supplied.' }
            elseif ($packageFindings.Count -gt 0) { $status='FAIL'; $reason=($packageFindings -join '; ') }
            else { $reason='Published payload contains no SQLite/WAL/SHM/browser-profile/developer-state artifacts.' }
        }
        default {
            if (-not $PersistenceTestsPassed) { $status='BLOCKED_CI'; $reason='Persistence acceptance test pass evidence not supplied.' }
        }
    }
    if ([string]$case.id -in @('DS09','DS10') -and -not $UpgradeLifecyclePassed) {
        if ($status -eq 'PASS') { $status='BLOCKED_CI'; $reason='Upgrade lifecycle evidence not supplied.' }
    }
    [pscustomobject]@{ Id=[string]$case.id; Title=[string]$case.title; Status=$status; Reason=$reason }
}

$status = 'PASS'
$details = 'All data-safety contracts, persistence tests, package scan and installer/update lifecycle evidence passed.'
if ($NotExecuted) {
    $status = 'NOT_EXECUTED'
    $details = 'Data-safety gate was intentionally not executed.'
} elseif ($failures.Count -gt 0) {
    $status = 'FAIL'
    $details = $failures -join '; '
} elseif (-not $payloadAvailable) {
    $status = 'BLOCKED_PACKAGE'
    $details = 'Worker 5 published payload is required for contamination acceptance.'
} elseif (-not $PersistenceTestsPassed -or -not $InstallerLifecyclePassed -or -not $UpgradeLifecyclePassed -or -not $RollbackLifecyclePassed) {
    $status = 'BLOCKED_CI'
    $details = 'Required persistence and Windows installer/update lifecycle evidence is incomplete.'
}

$sha = 'UNKNOWN'
try {
    $rawSha = & git -C $RepositoryRoot rev-parse HEAD 2>$null
    if ($LASTEXITCODE -eq 0 -and $rawSha) {
        $candidate = ([string]($rawSha | Select-Object -First 1)).Trim().ToLowerInvariant()
        if ($candidate -match '^[0-9a-f]{40}$') { $sha = $candidate }
    }
} catch { }

$report = [ordered]@{
    Gate='DATA_SAFETY'
    ReleaseGateAlias='DATA_PRESERVATION'
    Status=$status
    Details=$details
    SourceSha=$sha
    Version=(Get-Content (Join-Path $RepositoryRoot 'VERSION') -Raw).Trim()
    SchemaVersion=$schema
    GeneratedAt=[DateTimeOffset]::UtcNow.ToString('o')
    Evidence=[ordered]@{
        PersistenceTestsPassed=[bool]$PersistenceTestsPassed
        InstallerLifecyclePassed=[bool]$InstallerLifecyclePassed
        UpgradeLifecyclePassed=[bool]$UpgradeLifecyclePassed
        RollbackLifecyclePassed=[bool]$RollbackLifecyclePassed
        PayloadRoot=if($payloadAvailable){(Resolve-Path $PayloadRoot).Path}else{$null}
    }
    Failures=@($failures)
    PackageFindings=@($packageFindings)
    Cases=@($caseResults)
}

New-Item -ItemType Directory -Path (Split-Path $OutputPath -Parent) -Force | Out-Null
$report | ConvertTo-Json -Depth 8 | Set-Content $OutputPath -Encoding UTF8

# Existing release readiness consumes DATA_PRESERVATION.json and currently accepts only
# PASS/FAIL/NOT_APPLICABLE. Keep the richer DATA_SAFETY state in DataSafetyStatus while
# mapping every non-PASS result to a blocking FAIL for release-readiness consumption.
$readinessStatus = if ($status -eq 'PASS') { 'PASS' } else { 'FAIL' }
$readinessAlias = [ordered]@{
    Gate='DATA_PRESERVATION'
    Status=$readinessStatus
    DataSafetyStatus=$status
    Details="DATA_SAFETY=$status; $details"
    SourceSha=$sha
    Version=(Get-Content (Join-Path $RepositoryRoot 'VERSION') -Raw).Trim()
    GeneratedAt=[DateTimeOffset]::UtcNow.ToString('o')
}
$aliasPath = Join-Path (Split-Path $OutputPath -Parent) 'DATA_PRESERVATION.json'
$readinessAlias | ConvertTo-Json -Depth 5 | Set-Content $aliasPath -Encoding UTF8

$report | ConvertTo-Json -Depth 8
if ($status -eq 'FAIL') { throw "DATA_SAFETY_FAIL: $details" }
