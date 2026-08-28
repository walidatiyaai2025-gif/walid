[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$InstallRoot,
    [Parameter(Mandatory)] [int]$ExpectedSchemaVersion,
    [string]$DataRoot = (Join-Path $env:LOCALAPPDATA 'PCC Executive'),
    [string]$EvidencePath
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$app=Join-Path $InstallRoot 'PCCExecutive.exe'
if(-not(Test-Path $app)){throw 'FIRST_RUN_APP_MISSING: installed PCCExecutive.exe was not found.'}
if($ExpectedSchemaVersion -lt 1){throw 'FIRST_RUN_SCHEMA_TARGET_INVALID.'}

$run=Start-Process -FilePath $app -ArgumentList @('--smoke-test') -Wait -PassThru
if($run.ExitCode -ne 0){throw "FIRST_RUN_APP_SMOKE_FAILED: exit=$($run.ExitCode)"}

$db=Join-Path $DataRoot 'state\pcc-executive.db'
if(-not(Test-Path $db)){throw "FIRST_RUN_DB_NOT_INITIALIZED: expected durable database was not created at $db after install launch."}
$dbItem=Get-Item $db
if($dbItem.Length -le 0){throw 'FIRST_RUN_DB_EMPTY: durable database exists but is empty.'}

$evidence=[ordered]@{
    State='PASS';
    ExpectedSchemaVersion=$ExpectedSchemaVersion;
    DatabasePath=$db;
    DatabaseBytes=$dbItem.Length;
    InstallRoot=$InstallRoot;
    SmokeExitCode=$run.ExitCode;
    ObservedAt=[DateTimeOffset]::UtcNow.ToString('o')
}
if(-not[string]::IsNullOrWhiteSpace($EvidencePath)){
    $parent=Split-Path $EvidencePath -Parent
    if($parent){New-Item -ItemType Directory -Path $parent -Force|Out-Null}
    $evidence|ConvertTo-Json -Depth 4|Set-Content $EvidencePath -Encoding UTF8
}
Write-Host "FIRST_RUN_SMOKE_PASS db=$db schemaTarget=$ExpectedSchemaVersion bytes=$($dbItem.Length)"
