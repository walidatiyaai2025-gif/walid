[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$InstallRoot,
    [string]$DataRoot = (Join-Path $env:LOCALAPPDATA 'PCC Executive'),
    [switch]$FullCleanup
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$uninstaller = Get-ChildItem -Path $InstallRoot -File -Filter 'unins*.exe' | Sort-Object Name | Select-Object -First 1
if (-not $uninstaller) { throw "Uninstaller not found under $InstallRoot" }

$seedDir = Join-Path $DataRoot 'Data'
New-Item -ItemType Directory -Path $seedDir -Force | Out-Null
$seedPath = Join-Path $seedDir 'uninstall-preserve-smoke.txt'
Set-Content $seedPath 'preserve-me' -Encoding UTF8

$args = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART')
if ($FullCleanup) { $args += '/FULLCLEANUP=1' }

$uninstall = Start-Process -FilePath $uninstaller.FullName -ArgumentList $args -Wait -PassThru
if ($uninstall.ExitCode -ne 0) { throw "Uninstall failed: exit=$($uninstall.ExitCode)" }

if (Test-Path (Join-Path $InstallRoot 'PCCExecutive.exe')) { throw 'Application binary remains after uninstall.' }

if ($FullCleanup) {
    if (Test-Path $DataRoot) { throw 'Full cleanup was requested but durable data root remains.' }
}
else {
    if (-not (Test-Path $seedPath)) { throw 'Default uninstall did not preserve durable user data.' }
}

Write-Host "UNINSTALL_SMOKE_PASS fullCleanup=$([bool]$FullCleanup)"
