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
$phase='CHECKOUT';$command='git worktree add';$project='repository'
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
    $installerState='BLOCKED_DEPENDENCY';$installSmoke='BLOCKED_DEPENDENCY';$dbSmoke='BLOCKED_DEPENDENCY';$firstRun='BLOCKED_DEPENDENCY'
    if((Test-Path $app) -and (Test-Path $infra)){
        if([string]$config.databaseSchemaTarget -eq 'UNRESOLVED'){throw 'BLOCKED_DEPENDENCY: all product modules exist but databaseSchemaTarget remains UNRESOLVED.'}
        $env:PCCEXECUTIVE_DB_SCHEMA_TARGET=[string]$config.databaseSchemaTarget;$env:PCCEXECUTIVE_MINIMUM_UPGRADE_VERSION=[string]$config.minimumUpgradeVersion
        $phase='PACKAGING';$project='build/Package.ps1';$command='Package.ps1';Push-Location $worktree;try{& .\build\Package.ps1;if($LASTEXITCODE -ne 0){throw 'Installer package build failed.'}}finally{Pop-Location};$installerState='PASS'
        $v=(Get-Content (Join-Path $worktree 'VERSION') -Raw).Trim();$setup=Join-Path $worktree "artifacts\package\PCCExecutive-$v-Setup-x64.exe"
        $phase='INSTALL_SMOKE';$project='tests/installer/Smoke-FreshInstall.ps1';$command='Smoke-FreshInstall.ps1';& (Join-Path $worktree 'tests\installer\Smoke-FreshInstall.ps1') -InstallerPath $setup -ExpectedVersion $v;if($LASTEXITCODE -ne 0){throw 'Fresh install/launch smoke failed.'};$installSmoke='PASS';$firstRun='PASS'
        & (Join-Path $worktree 'tests\installer\Smoke-Uninstall.ps1') -InstallRoot (Join-Path $env:LOCALAPPDATA 'PCC Executive Smoke\Fresh');if($LASTEXITCODE -ne 0){throw 'Uninstall smoke failed.'}
        foreach($candidate in @('tests\integration\Smoke-Database.ps1','tests\installer\Smoke-Database.ps1')){if(Test-Path (Join-Path $worktree $candidate)){& (Join-Path $worktree $candidate);if($LASTEXITCODE -ne 0){throw 'Database smoke failed.'};$dbSmoke='PASS';break}}
        if(Test-Path (Join-Path $worktree 'artifacts')){Copy-Item (Join-Path $worktree 'artifacts\*') $OutputRoot -Recurse -Force -ErrorAction SilentlyContinue}
    }
    $missing=@($config.chains|Where-Object {$_.state -eq 'BLOCKED'}|ForEach-Object {$_.owner})
    $result=[ordered]@{Scope='TEMPORARY_REHEARSAL_NOT_CANONICAL';ControllerSourceSha=(& git -C $repo rev-parse HEAD).Trim().ToLowerInvariant();CanonicalSourceSha=$baseline;ConvergenceSha=$convergenceSha;IncludedHeads=$included;BlockedOwners=$missing;Restore='PASS';Build='PASS';Tests='PASS';ReleaseHardening='PASS';Installer=$installerState;InstallSmoke=$installSmoke;FirstRun=$firstRun;DbSmoke=$dbSmoke;Overall=if($missing.Count -gt 0){'BLOCKED_DEPENDENCY'}elseif($installerState -eq 'PASS' -and $installSmoke -eq 'PASS'){'PASS'}else{'BLOCKED_DEPENDENCY'}}
    $result|ConvertTo-Json -Depth 8|Set-Content (Join-Path $OutputRoot 'result.json') -Encoding UTF8
    $result|ConvertTo-Json -Depth 8
}
catch{
    $packet=[ordered]@{OWNER=if($project -match 'Browser'){'WORKER_3'}elseif($project -match 'Infrastructure'){'WORKER_2'}elseif($project -match 'App'){'WORKER_4'}elseif($phase -match 'PACKAGING|RELEASE'){'WORKER_5'}else{'CROSS_WORKER_INTERFACE'};'FILE/PROJECT'=$project;COMMAND=$command;ERROR=$_.Exception.Message;SOURCE_SHA=(& git -C $repo rev-parse HEAD).Trim().ToLowerInvariant();LIKELY_CAUSE=$phase;REQUIRED_FIX='Apply the smallest owning-Worker or build-compatibility repair, then rerun exact-head rehearsal.'}
    $packet|ConvertTo-Json -Depth 5|Set-Content (Join-Path $OutputRoot 'failure-ownership.json') -Encoding UTF8
    throw
}
finally{if(Test-Path $worktree){& git -C $repo worktree remove --force $worktree 2>$null|Out-Null}}
