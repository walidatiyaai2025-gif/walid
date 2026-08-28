$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
& (Join-Path $PSScriptRoot 'Apply-FinalInternalBlockersV2.ps1')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
