[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'Apply-ManagerBrowserLivenessV6cFix.ps1')
if ($LASTEXITCODE -ne 0) { throw "Manager Browser liveness V6c patch failed: exit=$LASTEXITCODE" }
