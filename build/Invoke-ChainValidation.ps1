[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Owner,
    [Parameter(Mandatory)] [string]$HeadSha,
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [Parameter(Mandatory)] [string]$OutputRoot
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$repo=(Resolve-Path $RepositoryRoot).Path
if($HeadSha -notmatch '^[0-9a-f]{40}$'){throw 'HeadSha must be an exact 40-character SHA.'}
New-Item -ItemType Directory -Path $OutputRoot -Force|Out-Null
$worktree=Join-Path ([IO.Path]::GetTempPath()) ('pcc-chain-'+$Owner.ToLowerInvariant()+'-'+[Guid]::NewGuid().ToString('N'))
$phase='CHECKOUT'; $command='git worktree add'; $project='repository'
try{
    & git -C $repo cat-file -e "$HeadSha^{commit}" 2>$null
    if($LASTEXITCODE -ne 0){& git -C $repo fetch --no-tags origin $HeadSha; if($LASTEXITCODE -ne 0){throw "Unable to fetch exact chain head $HeadSha"}}
    & git -C $repo worktree add --detach $worktree $HeadSha
    if($LASTEXITCODE -ne 0){throw 'Temporary exact-head worktree creation failed.'}
    $actual=(& git -C $worktree rev-parse HEAD).Trim().ToLowerInvariant()
    if($actual -ne $HeadSha.ToLowerInvariant()){throw "EXACT_HEAD_MISMATCH expected=$HeadSha actual=$actual"}

    $phase='TOOLCHAIN'; $command='dotnet --info'
    (& dotnet --info | Out-String) | Set-Content (Join-Path $OutputRoot 'dotnet-info.txt') -Encoding UTF8
    if($LASTEXITCODE -ne 0){throw '.NET SDK inspection failed.'}

    $log=Join-Path $OutputRoot 'build.log'
    $projects=@(Get-ChildItem (Join-Path $worktree 'src') -Recurse -File -Filter '*.csproj' -ErrorAction SilentlyContinue|Sort-Object FullName)
    if($projects.Count -eq 0){throw 'No source projects found on consumable chain.'}
    foreach($p in $projects){
        $project=$p.FullName; $phase='RESTORE'; $command="dotnet restore $($p.FullName)"; & dotnet restore $p.FullName 2>&1|Tee-Object -FilePath $log -Append; if($LASTEXITCODE -ne 0){throw "Restore failed: $($p.FullName)"}
        $phase='BUILD'; $command="dotnet build $($p.FullName) -c Release --no-restore"; & dotnet build $p.FullName --configuration Release --no-restore -p:ContinuousIntegrationBuild=true 2>&1|Tee-Object -FilePath $log -Append; if($LASTEXITCODE -ne 0){throw "Build failed: $($p.FullName)"}
    }

    $testProjects=@(Get-ChildItem (Join-Path $worktree 'tests') -Recurse -File -Filter '*.csproj' -ErrorAction SilentlyContinue|Sort-Object FullName)
    $trx=Join-Path $OutputRoot 'trx'; New-Item -ItemType Directory -Path $trx -Force|Out-Null
    foreach($p in $testProjects){$project=$p.FullName;$phase='TESTS';$command="dotnet test $($p.FullName) -c Release";& dotnet test $p.FullName --configuration Release --logger trx --results-directory $trx -p:ContinuousIntegrationBuild=true 2>&1|Tee-Object -FilePath $log -Append;if($LASTEXITCODE -ne 0){throw "Tests failed: $($p.FullName)"}}

    if($Owner -eq 'WORKER_5'){
        foreach($script in @('build/Test-ReleaseHardening.ps1','build/Test-UpdateIntegrity.ps1')){if(Test-Path (Join-Path $worktree $script)){$phase='TESTS';$project=$script;$command=$script;& (Join-Path $worktree $script) 2>&1|Tee-Object -FilePath $log -Append;if($LASTEXITCODE -ne 0){throw "Release test failed: $script"}}}
    }
    $result=[ordered]@{Owner=$Owner;SourceSha=$actual;Restore='PASS';Build='PASS';Tests=if($testProjects.Count -gt 0 -or $Owner -eq 'WORKER_5'){'PASS'}else{'NOT_PRESENT'};Projects=$projects.Count;TestProjects=$testProjects.Count}
    $result|ConvertTo-Json -Depth 5|Set-Content (Join-Path $OutputRoot 'result.json') -Encoding UTF8
    $result|ConvertTo-Json -Depth 5
}
catch{
    $packet=[ordered]@{OWNER=$Owner;'FILE/PROJECT'=$project;COMMAND=$command;ERROR=$_.Exception.Message;SOURCE_SHA=$HeadSha;LIKELY_CAUSE=$phase;REQUIRED_FIX='Route to owning Worker unless a minimal build/release compatibility bridge is sufficient.'}
    $packet|ConvertTo-Json -Depth 5|Set-Content (Join-Path $OutputRoot 'failure-ownership.json') -Encoding UTF8
    throw
}
finally{
    if(Test-Path $worktree){& git -C $repo worktree remove --force $worktree 2>$null|Out-Null}
}
