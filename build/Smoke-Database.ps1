[CmdletBinding()]
param(
    [Parameter(Mandatory)] [int]$ExpectedSchemaVersion,
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [string]$OutputRoot = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..')).Path 'artifacts\rehearsal\db-smoke')
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
if($ExpectedSchemaVersion -ne 1){throw "DB_SMOKE_SCHEMA_CONTRACT_UNSUPPORTED expected=$ExpectedSchemaVersion"}
$project=Join-Path $RepositoryRoot 'tests\PCCExecutive.Infrastructure.Tests\PCCExecutive.Infrastructure.Tests.csproj'
if(-not(Test-Path $project)){throw 'DB_SMOKE_PROJECT_MISSING: PCCExecutive.Infrastructure.Tests is required.'}
New-Item -ItemType Directory -Path $OutputRoot -Force|Out-Null
& dotnet test $project --configuration Release --filter 'FullyQualifiedName~Migration_and_core_state_survive_reopen' --logger trx --results-directory $OutputRoot -p:ContinuousIntegrationBuild=true
if($LASTEXITCODE -ne 0){throw 'DB_SMOKE_FAILED: migration/reopen/persistence test failed.'}
Write-Host "DB_SMOKE_PASS schema=$ExpectedSchemaVersion"
