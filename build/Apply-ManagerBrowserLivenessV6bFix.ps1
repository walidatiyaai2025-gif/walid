[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'Apply-ManagerBrowserLivenessV6cFix.ps1')
