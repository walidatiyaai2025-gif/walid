[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [string]$ConfigPath = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..')).Path 'release\integration-rehearsal.json'),
    [string]$OutputRoot = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..')).Path 'artifacts\rehearsal\combined')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path $RepositoryRoot).Path
$config = Get-Content (Resolve-Path $ConfigPath) -Raw | ConvertFrom-Json
$controllerSha = (& git -C $repo rev-parse HEAD).Trim().ToLowerInvariant()
if ($controllerSha -notmatch '^[0-9a-f]{40}$') { throw "EXACT_CONTROLLER_SHA_REQUIRED: $controllerSha" }
$canonicalSha = [string]$config.canonical.sha
if ($canonicalSha -notmatch '^[0-9a-f]{40}$') { throw "EXACT_CANONICAL_SHA_REQUIRED: $canonicalSha" }

New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
$worktree = Join-Path ([IO.Path]::GetTempPath()) ('pcc-terminal-' + [Guid]::NewGuid().ToString('N'))
$log = Join-Path $OutputRoot 'combined-build.log'
$failures = [System.Collections.Generic.List[object]]::new()

function Add-Failure([string]$owner,[string]$phase,[string]$file,[string]$error,[string]$fix) {
    $script:failures.Add([ordered]@{
        OWNER=$owner; PHASE=$phase; 'FILE/PROJECT'=$file; ERROR=$error;
        SOURCE_SHA=$script:controllerSha; REQUIRED_FIX=$fix
    })
}

$restore='BLOCKED_DEPENDENCY'; $build='BLOCKED_DEPENDENCY'; $tests='BLOCKED_DEPENDENCY'
$releaseHardening='BLOCKED_DEPENDENCY'; $publish='BLOCKED_DEPENDENCY'; $installer='BLOCKED_DEPENDENCY'
$installSmoke='BLOCKED_DEPENDENCY'; $firstRun='BLOCKED_DEPENDENCY'; $dbSmoke='BLOCKED_DEPENDENCY'
$persistenceSmoke='BLOCKED_DEPENDENCY'; $uninstallSmoke='BLOCKED_DEPENDENCY'
$installReason='Not executed.'; $firstRunReason='Not executed.'; $dbReason='Not executed.'

try {
    & git -C $repo worktree add --detach $worktree $controllerSha | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Unable to create exact convergence worktree.' }
    $actual = (& git -C $worktree rev-parse HEAD).Trim().ToLowerInvariant()
    if ($actual -ne $controllerSha) { throw "EXACT_HEAD_MISMATCH expected=$controllerSha actual=$actual" }
    Set-Content (Join-Path $OutputRoot 'SOURCE_SHA.txt') $controllerSha -Encoding ascii
    Set-Content (Join-Path $OutputRoot 'CANONICAL_SHA.txt') $canonicalSha -Encoding ascii
    (& dotnet --info | Out-String) | Set-Content (Join-Path $OutputRoot 'dotnet-info.txt') -Encoding UTF8

    Push-Location $worktree
    try {
        try {
            & .\build\Build.ps1 -Configuration Release -RequireProduct 2>&1 | Tee-Object -FilePath $log -Append
            if ($LASTEXITCODE -ne 0) { throw 'Build.ps1 returned non-zero.' }
            $restore='PASS'; $build='PASS'; $tests='PASS'
        }
        catch {
            $message=$_.Exception.Message
            $restore = if ($message -match '(?i)restore') {'FAIL'} else {'PASS'}
            if ($message -match '(?i)test') { $build='PASS'; $tests='FAIL' } else { $build='FAIL'; $tests='BLOCKED_DEPENDENCY' }
            $owner = if ($log -and (Test-Path $log) -and (Get-Content $log -Raw) -match 'PCCExecutive\.Browser') {'WORKER_3_BROWSER'} elseif ((Test-Path $log) -and (Get-Content $log -Raw) -match 'PCCExecutive\.App') {'WORKER_4_UI'} else {'CROSS_WORKER_INTERFACE'}
            Add-Failure $owner 'BUILD_TEST' 'build/Build.ps1' $message 'Repair the smallest owning compile/test defect and rerun this exact convergence head.'
        }

        if ($build -eq 'PASS' -and $tests -eq 'PASS') {
            try {
                & .\build\Test-ReleaseHardening.ps1 2>&1 | Tee-Object -FilePath $log -Append
                if ($LASTEXITCODE -ne 0) { throw 'Release hardening failed.' }
                if (Test-Path '.\build\Test-UpdateIntegrity.ps1') {
                    & .\build\Test-UpdateIntegrity.ps1 2>&1 | Tee-Object -FilePath $log -Append
                    if ($LASTEXITCODE -ne 0) { throw 'Update integrity failed.' }
                }
                $releaseHardening='PASS'
            }
            catch {
                $releaseHardening='FAIL'
                Add-Failure 'WORKER_5_RELEASE' 'RELEASE_HARDENING' 'build/Test-ReleaseHardening.ps1' $_.Exception.Message 'Repair deterministic release/security compatibility without weakening the gates.'
            }
        }

        if ($build -eq 'PASS' -and $tests -eq 'PASS' -and $releaseHardening -eq 'PASS') {
            $env:PCCEXECUTIVE_DB_SCHEMA_TARGET=[string]$config.databaseSchemaTarget
            $env:PCCEXECUTIVE_MINIMUM_UPGRADE_VERSION=[string]$config.minimumUpgradeVersion
            try {
                & .\build\Package.ps1 2>&1 | Tee-Object -FilePath $log -Append
                if ($LASTEXITCODE -ne 0) { throw 'Package.ps1 returned non-zero.' }
                $publish='PASS'; $installer='PASS'
            }
            catch {
                $publish='FAIL'; $installer='FAIL'
                Add-Failure 'WORKER_5_RELEASE' 'PUBLISH_INSTALLER' 'build/Package.ps1' $_.Exception.Message 'Repair exact-head publish/package behavior; never generate a placeholder installer.'
            }
        }

        if ($installer -eq 'PASS') {
            $version=(Get-Content VERSION -Raw).Trim()
            $setup=Join-Path $worktree "artifacts\package\PCCExecutive-$version-Setup-x64.exe"
            $installRoot=Join-Path $env:LOCALAPPDATA 'PCC Executive Smoke\Fresh'
            $installEvidence=Join-Path $worktree 'artifacts\install-evidence'
            New-Item -ItemType Directory -Path $installEvidence -Force | Out-Null
            try {
                & .\tests\installer\Smoke-FreshInstall.ps1 -InstallerPath $setup -ExpectedVersion $version -ExpectedSourceSha $controllerSha -InstallRoot $installRoot -EvidencePath (Join-Path $installEvidence 'fresh-install.json')
                if ($LASTEXITCODE -ne 0) { throw 'Fresh install smoke returned non-zero.' }
                $installSmoke='PASS'; $installReason='Setup installed exact-head files, Start Menu shortcut was present, and a WPF top-level window was observed.'
            }
            catch {
                $installSmoke='FAIL'; $installReason=$_.Exception.Message
                Add-Failure 'WORKER_5_RELEASE' 'INSTALL_SMOKE' 'tests/installer/Smoke-FreshInstall.ps1' $installReason 'Repair installer/install-launch behavior while preserving the standard GUI wizard.'
            }

            if ($installSmoke -eq 'PASS') {
                try {
                    & .\tests\installer\Smoke-FirstRun.ps1 -InstallRoot $installRoot -ExpectedSchemaVersion ([int]$config.databaseSchemaTarget) -EvidencePath (Join-Path $installEvidence 'first-run.json')
                    if ($LASTEXITCODE -ne 0) { throw 'First-run smoke returned non-zero.' }
                    $firstRun='PASS'; $firstRunReason='Installed app completed integrated startup and initialized non-empty durable SQLite state.'
                }
                catch {
                    $firstRun='FAIL'; $firstRunReason=$_.Exception.Message
                    Add-Failure 'CROSS_WORKER_INTERFACE' 'FIRST_RUN' 'tests/installer/Smoke-FirstRun.ps1' $firstRunReason 'Repair WPF-to-Infrastructure startup wiring; do not fake the database.'
                }

                try {
                    & .\tests\installer\Smoke-Persistence.ps1 -InstallRoot $installRoot -EvidencePath (Join-Path $installEvidence 'persistence-reopen.json')
                    if ($LASTEXITCODE -ne 0) { throw 'Persistence reopen smoke returned non-zero.' }
                    $persistenceSmoke='PASS'
                }
                catch {
                    $persistenceSmoke='FAIL'
                    Add-Failure 'WORKER_2_PERSISTENCE' 'PERSISTENCE_SMOKE' 'tests/installer/Smoke-Persistence.ps1' $_.Exception.Message 'Repair durable restart behavior or the integrated persistence seam.'
                }
            }

            try {
                & .\build\Smoke-Database.ps1 -ExpectedSchemaVersion ([int]$config.databaseSchemaTarget) -OutputRoot (Join-Path $worktree 'artifacts\db-smoke')
                if ($LASTEXITCODE -ne 0) { throw 'Database smoke returned non-zero.' }
                $dbSmoke='PASS'; $dbReason='Fresh SQLite migration/reopen and deterministic persistence smoke passed.'
            }
            catch {
                $dbSmoke='FAIL'; $dbReason=$_.Exception.Message
                Add-Failure 'WORKER_2_PERSISTENCE' 'DB_SMOKE' 'build/Smoke-Database.ps1' $dbReason 'Repair persistence implementation or deterministic schema smoke.'
            }

            try {
                & .\tests\installer\Smoke-Uninstall.ps1 -InstallRoot $installRoot
                if ($LASTEXITCODE -ne 0) { throw 'Uninstall smoke returned non-zero.' }
                $uninstallSmoke='PASS'
            }
            catch {
                $uninstallSmoke='FAIL'
                Add-Failure 'WORKER_5_RELEASE' 'UNINSTALL' 'tests/installer/Smoke-Uninstall.ps1' $_.Exception.Message 'Repair uninstall while preserving durable user data by default.'
            }
        }
    }
    finally { Pop-Location }

    if (Test-Path (Join-Path $worktree 'artifacts')) {
        $copyRoot=Join-Path $OutputRoot 'product-artifacts'
        New-Item -ItemType Directory -Path $copyRoot -Force | Out-Null
        Copy-Item (Join-Path $worktree 'artifacts\*') $copyRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
catch {
    Add-Failure 'WORKER_5_RELEASE' 'HARNESS' 'build/Invoke-IntegratedRehearsal.ps1' $_.Exception.Message 'Repair the terminal rehearsal harness and rerun exact head.'
}
finally {
    if (Test-Path $worktree) { & git -C $repo worktree remove --force $worktree 2>$null | Out-Null }
}

if ($failures.Count -gt 0) { $failures | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $OutputRoot 'failure-ownership.json') -Encoding UTF8 }
$included=@($config.chains | Where-Object {$_.state -eq 'PENDING_PR'} | ForEach-Object {[ordered]@{Owner=[string]$_.owner;Head=[string]$_.head}})
$states=@($restore,$build,$tests,$releaseHardening,$publish,$installer,$installSmoke,$firstRun,$persistenceSmoke,$dbSmoke,$uninstallSmoke)
$overall=if('FAIL' -in $states){'FAIL'}elseif('BLOCKED_DEPENDENCY' -in $states){'BLOCKED_DEPENDENCY'}else{'PASS'}
$result=[ordered]@{
    Scope='PR12_EXACT_HEAD_CONVERGENCE_NOT_CANONICAL'; ControllerSourceSha=$controllerSha; CanonicalSourceSha=$canonicalSha; ConvergenceSha=$controllerSha; IncludedHeads=$included;
    Restore=$restore; Build=$build; Tests=$tests; ReleaseHardening=$releaseHardening; Publish=$publish; Installer=$installer;
    InstallSmoke=$installSmoke; InstallReason=$installReason; FirstRun=$firstRun; FirstRunReason=$firstRunReason; PersistenceSmoke=$persistenceSmoke;
    DbSmoke=$dbSmoke; DbReason=$dbReason; UninstallSmoke=$uninstallSmoke; Failures=@($failures); Overall=$overall
}
$result | ConvertTo-Json -Depth 12 | Set-Content (Join-Path $OutputRoot 'result.json') -Encoding UTF8
$result | ConvertTo-Json -Depth 12
