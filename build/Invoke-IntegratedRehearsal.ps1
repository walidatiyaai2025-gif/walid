[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [string]$ConfigPath = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..')).Path 'release\integration-rehearsal.json'),
    [string]$OutputRoot = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..')).Path 'artifacts\rehearsal\combined')
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$repo=(Resolve-Path $RepositoryRoot).Path
$config=Get-Content (Resolve-Path $ConfigPath) -Raw|ConvertFrom-Json
New-Item -ItemType Directory -Path $OutputRoot -Force|Out-Null
$worktree=Join-Path ([IO.Path]::GetTempPath()) ('pcc-integrated-'+[Guid]::NewGuid().ToString('N'))
$phase='CHECKOUT';$command='git worktree add';$project='repository';$failures=@()
function Add-Failure([string]$owner,[string]$file,[string]$cmd,[string]$error,[string]$cause,[string]$fix){
    $script:failures += [pscustomobject]@{OWNER=$owner;'FILE/PROJECT'=$file;COMMAND=$cmd;ERROR=$error;SOURCE_SHA=(& git -C $script:repo rev-parse HEAD).Trim().ToLowerInvariant();LIKELY_CAUSE=$cause;REQUIRED_FIX=$fix}
}
try{
    $baseline=[string]$config.canonical.sha
    & git -C $repo worktree add --detach $worktree $baseline
    if($LASTEXITCODE -ne 0){throw 'Unable to create temporary convergence checkout.'}
    & git -C $worktree config user.name 'PCC Executive Rehearsal'
    & git -C $worktree config user.email 'pcc-executive-rehearsal@invalid.local'
    $included=@()
    foreach($chain in @($config.chains|Where-Object {$_.includeInPartialCandidate})){
        $head=[string]$chain.head;$phase='MERGE';$command="git merge --no-ff --no-edit $head";$project=[string]$chain.owner
        & git -C $worktree merge --no-ff --no-edit $head
        if($LASTEXITCODE -ne 0){throw "Temporary convergence merge failed for $($chain.owner) at $head"}
        $included += [ordered]@{Owner=$chain.owner;Head=$head}
    }
    $convergenceSha=(& git -C $worktree rev-parse HEAD).Trim().ToLowerInvariant()
    (& dotnet --info|Out-String)|Set-Content (Join-Path $OutputRoot 'dotnet-info.txt') -Encoding UTF8

    $log=Join-Path $OutputRoot 'combined-build.log'
    $projects=@(Get-ChildItem (Join-Path $worktree 'src') -Recurse -File -Filter '*.csproj' -ErrorAction SilentlyContinue|Sort-Object FullName)
    foreach($p in $projects){$project=$p.FullName;$phase='RESTORE';$command="dotnet restore $($p.FullName)";& dotnet restore $p.FullName 2>&1|Tee-Object -FilePath $log -Append;if($LASTEXITCODE -ne 0){throw "Combined restore failed: $($p.FullName)"};$phase='BUILD';$command="dotnet build $($p.FullName)";& dotnet build $p.FullName --configuration Release --no-restore -p:ContinuousIntegrationBuild=true 2>&1|Tee-Object -FilePath $log -Append;if($LASTEXITCODE -ne 0){throw "Combined build failed: $($p.FullName)"}}

    $tests=@(Get-ChildItem (Join-Path $worktree 'tests') -Recurse -File -Filter '*.csproj' -ErrorAction SilentlyContinue|Sort-Object FullName)
    $trx=Join-Path $OutputRoot 'trx';New-Item -ItemType Directory -Path $trx -Force|Out-Null
    foreach($p in $tests){$project=$p.FullName;$phase='TESTS';$command="dotnet test $($p.FullName)";& dotnet test $p.FullName --configuration Release --logger trx --results-directory $trx -p:ContinuousIntegrationBuild=true 2>&1|Tee-Object -FilePath $log -Append;if($LASTEXITCODE -ne 0){throw "Combined tests failed: $($p.FullName)"}}

    foreach($script in @('build/Test-ReleaseHardening.ps1','build/Test-UpdateIntegrity.ps1')){if(Test-Path (Join-Path $worktree $script)){$phase='RELEASE_TESTS';$project=$script;$command=$script;& (Join-Path $worktree $script) 2>&1|Tee-Object -FilePath $log -Append;if($LASTEXITCODE -ne 0){throw "Combined release test failed: $script"}}}

    $app=Join-Path $worktree 'src\PCCExecutive.App\PCCExecutive.App.csproj';$infra=Join-Path $worktree 'src\PCCExecutive.Infrastructure\PCCExecutive.Infrastructure.csproj'
    $publishState='BLOCKED_DEPENDENCY';$installerState='BLOCKED_DEPENDENCY';$installSmoke='BLOCKED_DEPENDENCY';$firstRun='BLOCKED_DEPENDENCY';$dbSmoke='BLOCKED_DEPENDENCY';$uninstallSmoke='BLOCKED_DEPENDENCY'
    $firstRunReason='Required App or Infrastructure module is absent.';$installReason='Required App or Infrastructure module is absent.';$dbReason='Infrastructure is absent.'
    if((Test-Path $app) -and (Test-Path $infra)){
        if([string]$config.databaseSchemaTarget -eq 'UNRESOLVED'){throw 'BLOCKED_DEPENDENCY: all product modules exist but databaseSchemaTarget remains UNRESOLVED.'}
        $env:PCCEXECUTIVE_DB_SCHEMA_TARGET=[string]$config.databaseSchemaTarget;$env:PCCEXECUTIVE_MINIMUM_UPGRADE_VERSION=[string]$config.minimumUpgradeVersion
        $phase='PACKAGING';$project='build/Package.ps1';$command='Package.ps1'
        try{
            Push-Location $worktree;try{& .\build\Package.ps1;if($LASTEXITCODE -ne 0){throw 'Installer package build failed.'}}finally{Pop-Location}
            $publishState='PASS';$installerState='PASS'
        }catch{
            $publishState='FAIL';$installerState='FAIL';$installReason=$_.Exception.Message
            Add-Failure 'WORKER_5' $project $command $_.Exception.Message 'PACKAGING' 'Repair publish/installer convergence without changing feature architecture.'
        }

        if($installerState -eq 'PASS'){
            $v=(Get-Content (Join-Path $worktree 'VERSION') -Raw).Trim();$setup=Join-Path $worktree "artifacts\package\PCCExecutive-$v-Setup-x64.exe";$installRoot=Join-Path $env:LOCALAPPDATA 'PCC Executive Smoke\Fresh'
            $phase='INSTALL_SMOKE';$project='tests/installer/Smoke-FreshInstall.ps1';$command='Smoke-FreshInstall.ps1'
            try{& (Join-Path $worktree 'tests\installer\Smoke-FreshInstall.ps1') -InstallerPath $setup -ExpectedVersion $v -InstallRoot $installRoot;if($LASTEXITCODE -ne 0){throw 'Fresh install/launch smoke failed.'};$installSmoke='PASS';$installReason='Installer completed and PCCExecutive.exe launched.'}catch{$installSmoke='FAIL';$installReason=$_.Exception.Message;Add-Failure 'WORKER_5' $project $command $_.Exception.Message 'INSTALL_SMOKE' 'Repair installer/install-launch behavior.'}

            if($installSmoke -eq 'PASS'){
                $phase='FIRST_RUN';$project='tests/installer/Smoke-FirstRun.ps1';$command='Smoke-FirstRun.ps1'
                try{& (Join-Path $worktree 'tests\installer\Smoke-FirstRun.ps1') -InstallRoot $installRoot -ExpectedSchemaVersion ([int]$config.databaseSchemaTarget);if($LASTEXITCODE -ne 0){throw 'First-run smoke failed.'};$firstRun='PASS';$firstRunReason='Installed app initialized durable state on first launch.'}catch{$firstRun='FAIL';$firstRunReason=$_.Exception.Message;Add-Failure 'CROSS_WORKER_INTERFACE' $project $command $_.Exception.Message 'FIRST_RUN_RUNTIME_WIRING' 'Wire WPF App startup to Infrastructure/settings initialization; do not fake the database artifact.'}
            }

            $phase='UNINSTALL_SMOKE';$project='tests/installer/Smoke-Uninstall.ps1';$command='Smoke-Uninstall.ps1'
            try{& (Join-Path $worktree 'tests\installer\Smoke-Uninstall.ps1') -InstallRoot $installRoot;if($LASTEXITCODE -ne 0){throw 'Uninstall smoke failed.'};$uninstallSmoke='PASS'}catch{$uninstallSmoke='FAIL';Add-Failure 'WORKER_5' $project $command $_.Exception.Message 'UNINSTALL_SMOKE' 'Repair uninstall behavior while preserving default durable data.'}
        }

        $phase='DB_SMOKE';$project='build/Smoke-Database.ps1';$command='Smoke-Database.ps1'
        try{& (Join-Path $worktree 'build\Smoke-Database.ps1') -ExpectedSchemaVersion ([int]$config.databaseSchemaTarget) -OutputRoot (Join-Path $OutputRoot 'db-smoke');if($LASTEXITCODE -ne 0){throw 'Database smoke failed.'};$dbSmoke='PASS';$dbReason='Fresh SQLite migration, reopen, settings and core-state persistence test passed.'}catch{$dbSmoke='FAIL';$dbReason=$_.Exception.Message;Add-Failure 'WORKER_2_PERSISTENCE' $project $command $_.Exception.Message 'DB_SMOKE' 'Repair persistence implementation or its deterministic smoke contract.'}

        if(Test-Path (Join-Path $worktree 'artifacts')){Copy-Item (Join-Path $worktree 'artifacts\*') $OutputRoot -Recurse -Force -ErrorAction SilentlyContinue}
    }

    if($failures.Count -gt 0){$failures|ConvertTo-Json -Depth 6|Set-Content (Join-Path $OutputRoot 'failure-ownership.json') -Encoding UTF8}
    $states=@($publishState,$installerState,$installSmoke,$firstRun,$dbSmoke,$uninstallSmoke)
    $overall=if('FAIL' -in $states){'FAIL'}elseif('BLOCKED_DEPENDENCY' -in $states){'BLOCKED_DEPENDENCY'}else{'PASS'}
    $result=[ordered]@{Scope='TEMPORARY_REHEARSAL_NOT_CANONICAL';ControllerSourceSha=(& git -C $repo rev-parse HEAD).Trim().ToLowerInvariant();CanonicalSourceSha=$baseline;ConvergenceSha=$convergenceSha;IncludedHeads=$included;Restore='PASS';Build='PASS';Tests='PASS';ReleaseHardening='PASS';Publish=$publishState;Installer=$installerState;InstallSmoke=$installSmoke;InstallReason=$installReason;FirstRun=$firstRun;FirstRunReason=$firstRunReason;DbSmoke=$dbSmoke;DbReason=$dbReason;UninstallSmoke=$uninstallSmoke;Failures=$failures;Overall=$overall}
    $result|ConvertTo-Json -Depth 10|Set-Content (Join-Path $OutputRoot 'result.json') -Encoding UTF8
    $result|ConvertTo-Json -Depth 10
}
catch{
    Add-Failure (if($project -match 'Browser'){'WORKER_3_BROWSER'}elseif($project -match 'Infrastructure'){'WORKER_2_PERSISTENCE'}elseif($project -match 'App'){'WORKER_4_UI'}elseif($phase -match 'PACKAGING|RELEASE'){'WORKER_5_RELEASE'}else{'CROSS_WORKER_INTERFACE'}) $project $command $_.Exception.Message $phase 'Apply the smallest owning-Worker or build-compatibility repair, then rerun exact-head rehearsal.'
    $failures|ConvertTo-Json -Depth 6|Set-Content (Join-Path $OutputRoot 'failure-ownership.json') -Encoding UTF8
    throw
}
finally{if(Test-Path $worktree){& git -C $repo worktree remove --force $worktree 2>$null|Out-Null}}
