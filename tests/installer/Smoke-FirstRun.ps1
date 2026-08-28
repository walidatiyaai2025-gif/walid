[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$InstallRoot,
    [Parameter(Mandatory)] [int]$ExpectedSchemaVersion,
    [string]$DataRoot = (Join-Path $env:LOCALAPPDATA 'PCC Executive')
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$app=Join-Path $InstallRoot 'PCCExecutive.exe'
if(-not(Test-Path $app)){throw 'FIRST_RUN_APP_MISSING: installed PCCExecutive.exe was not found.'}
$db=Join-Path $DataRoot 'state\pcc-executive.db'
if(-not(Test-Path $db)){throw "FIRST_RUN_DB_NOT_INITIALIZED: expected durable database was not created at $db after install launch."}
if((Get-Item $db).Length -le 0){throw 'FIRST_RUN_DB_EMPTY: durable database exists but is empty.'}
if($ExpectedSchemaVersion -lt 1){throw 'FIRST_RUN_SCHEMA_TARGET_INVALID.'}
Write-Host "FIRST_RUN_SMOKE_PASS db=$db schemaTarget=$ExpectedSchemaVersion"
