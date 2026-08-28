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
$gates += [ordered]@{Name='BUILD';State=[string]$combined.Build;Details='Every discovered source project was restored and built on the exact rehearsal source.'}
$gates += [ordered]@{Name='TESTS';State=[string]$combined.Tests;Details='Every discovered deterministic test project was executed.'}
$gates += [ordered]@{Name='PUBLISH';State=[string]$combined.Publish;Details='win-x64 self-contained publish is part of Package.ps1.'}
$gates += [ordered]@{Name='INSTALLER';State=[string]$combined.Installer;Details=if($combined.Installer -eq 'PASS'){'Actual Setup EXE built.'}else{'See combined failure evidence.'}}
$gates += [ordered]@{Name='INSTALL_SMOKE';State=[string]$combined.InstallSmoke;Details=[string]$combined.InstallReason}
$gates += [ordered]@{Name='FIRST_RUN';State=[string]$combined.FirstRun;Details=[string]$combined.FirstRunReason}
$gates += [ordered]@{Name='BROWSER_SMOKE';State=if(($gates|Where-Object Name -eq 'BROWSER').State -eq 'PASS' -and $combined.Tests -eq 'PASS'){'PASS'}else{'BLOCKED_DEPENDENCY'};Details='Deterministic PCC-owned runtime/ownership tests; live ChatGPT login is not required.'}
$gates += [ordered]@{Name='DB_SMOKE';State=[string]$combined.DbSmoke;Details=[string]$combined.DbReason}
$gates += [ordered]@{Name='UNINSTALL_SMOKE';State=[string]$combined.UninstallSmoke;Details='Default uninstall must preserve durable data.'}
$gates += [ordered]@{Name='PROVENANCE';State=if([string]$combined.ConvergenceSha -match '^[0-9a-f]{40}$'){'PASS'}else{'FAIL'};Details='Controller SHA, canonical SHA, exact Worker heads, and convergence SHA are recorded.'}
$gates += [ordered]@{Name='SECURITY';State=if($combined.ReleaseHardening -eq 'PASS'){'PASS'}else{'FAIL'};Details='Release payload secret/profile guard and update-integrity hardening tests executed.'}
$gates += [ordered]@{Name='SIGNING';State=if([string]$config.signingState -eq 'UNSIGNED_DEV'){'NOT_APPLICABLE'}else{'PASS'};Details="Rehearsal signing state: $($config.signingState). No production signing claim is made."}
$fails=@($gates|Where-Object {$_.State -eq 'FAIL'});$blocked=@($gates|Where-Object {$_.State -eq 'BLOCKED_DEPENDENCY'})
$overall=if($fails.Count -gt 0){'FAIL'}elseif($blocked.Count -gt 0){'BLOCKED_DEPENDENCY'}else{'PASS'}
$result=[ordered]@{SchemaVersion=1;Task=$config.task;Version=$config.version;Scope='TEMPORARY_REHEARSAL_NOT_CANONICAL';ControllerSourceSha=$combined.ControllerSourceSha;CanonicalSourceSha=$combined.CanonicalSourceSha;ConvergenceSha=$combined.ConvergenceSha;PlaywrightPackaging=$plan;Gates=$gates;Failures=@($combined.Failures);Overall=$overall}
$dir=Split-Path -Parent $OutputPath;if($dir){New-Item -ItemType Directory -Path $dir -Force|Out-Null};$result|ConvertTo-Json -Depth 12|Set-Content $OutputPath -Encoding UTF8;$result|ConvertTo-Json -Depth 12
