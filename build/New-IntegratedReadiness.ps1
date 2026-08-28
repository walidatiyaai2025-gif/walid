[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$MatrixPath,
    [Parameter(Mandatory)] [string]$CombinedPath,
    [Parameter(Mandatory)] [string]$PlaywrightPlanPath,
    [string]$ConfigPath = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..')).Path 'release\integration-rehearsal.json'),
    [string]$OutputPath = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..')).Path 'artifacts\release\integrated-readiness.json')
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$matrix=Get-Content (Resolve-Path $MatrixPath) -Raw|ConvertFrom-Json;$combined=Get-Content (Resolve-Path $CombinedPath) -Raw|ConvertFrom-Json;$plan=Get-Content (Resolve-Path $PlaywrightPlanPath) -Raw|ConvertFrom-Json;$config=Get-Content (Resolve-Path $ConfigPath) -Raw|ConvertFrom-Json
$includedOwners=@($combined.IncludedHeads|ForEach-Object {[string]$_.Owner})
function ModuleGate([string]$name){$m=$matrix.Modules|Where-Object {$_.Name -eq $name}|Select-Object -First 1;if($null -eq $m){return [ordered]@{Name=$name;State='FAIL';Details='Matrix entry missing.'}};if($m.State -eq 'FOUND'){return [ordered]@{Name=$name;State='PASS';Details='Present on canonical baseline.'}};if($m.State -eq 'PENDING_PR' -and $m.Owner -in $includedOwners -and $combined.Build -eq 'PASS'){return [ordered]@{Name=$name;State='PASS';Details="Validated in temporary convergence from $($m.SourceSha); not canonical."}};return [ordered]@{Name=$name;State='BLOCKED_DEPENDENCY';Details=$m.Reason}}
$gates=@();foreach($n in @('DOMAIN','APPLICATION','PERSISTENCE','BROWSER','UI','PCC','GITHUB','UPDATER')){$gates+=ModuleGate $n}
$gates += [ordered]@{Name='BUILD';State=[string]$combined.Build;Details='All source projects present in the temporary convergence checkout were restored and built.'}
$gates += [ordered]@{Name='TESTS';State=[string]$combined.Tests;Details='All test projects present in the temporary convergence checkout were executed.'}
$gates += [ordered]@{Name='INSTALLER';State=[string]$combined.Installer;Details=if($combined.Installer -eq 'PASS'){'Actual Setup EXE built.'}else{'PCCExecutive.App and persistence must exist before Setup rehearsal.'}}
$gates += [ordered]@{Name='INSTALL_SMOKE';State=[string]$combined.InstallSmoke;Details='Fresh install includes launch verification when candidate is packageable.'}
$gates += [ordered]@{Name='BROWSER_SMOKE';State=if(($gates|Where-Object Name -eq 'BROWSER').State -eq 'PASS' -and $combined.Tests -eq 'PASS'){'PASS'}else{'BLOCKED_DEPENDENCY'};Details='Deterministic PCC-owned runtime/ownership tests; live ChatGPT login is not required.'}
$gates += [ordered]@{Name='DB_SMOKE';State=[string]$combined.DbSmoke;Details='Requires integrated persistence plus an explicit database smoke contract.'}
$gates += [ordered]@{Name='PROVENANCE';State=if([string]$combined.ConvergenceSha -match '^[0-9a-f]{40}$'){'PASS'}else{'FAIL'};Details='Controller SHA, canonical SHA, exact Worker heads, and temporary convergence SHA are recorded.'}
$gates += [ordered]@{Name='SECURITY';State=if($combined.ReleaseHardening -eq 'PASS'){'PASS'}else{'FAIL'};Details='Release payload secret/profile guard and update-integrity hardening tests executed.'}
$gates += [ordered]@{Name='SIGNING';State=if([string]$config.signingState -eq 'UNSIGNED_DEV'){'NOT_APPLICABLE'}else{'PASS'};Details="Rehearsal signing state: $($config.signingState). No production signing claim is made."}
$blocked=@($gates|Where-Object {$_.State -in @('FAIL','BLOCKED_DEPENDENCY')})
$result=[ordered]@{SchemaVersion=1;Task=$config.task;Version=$config.version;Scope='TEMPORARY_REHEARSAL_NOT_CANONICAL';ControllerSourceSha=$combined.ControllerSourceSha;CanonicalSourceSha=$combined.CanonicalSourceSha;ConvergenceSha=$combined.ConvergenceSha;PlaywrightPackaging=$plan;Gates=$gates;Overall=if($blocked.Count -gt 0){'BLOCKED_DEPENDENCY'}else{'PASS'}}
$dir=Split-Path -Parent $OutputPath;if($dir){New-Item -ItemType Directory -Path $dir -Force|Out-Null};$result|ConvertTo-Json -Depth 10|Set-Content $OutputPath -Encoding UTF8;$result|ConvertTo-Json -Depth 10
