[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$InstallerPath,
    [Parameter(Mandatory)]
    [string]$ExpectedVersion,
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'PCC Executive Smoke\Fresh')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Remove-Item $InstallRoot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $InstallRoot -Force | Out-Null

$install = Start-Process -FilePath (Resolve-Path $InstallerPath).Path -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/CURRENTUSER',"/DIR=`"$InstallRoot`"") -Wait -PassThru
if ($install.ExitCode -ne 0) { throw "Fresh installer failed: exit=$($install.ExitCode)" }

$app = Join-Path $InstallRoot 'PCCExecutive.exe'
$provenancePath = Join-Path $InstallRoot 'build-provenance.json'
if (-not (Test-Path $app)) { throw 'Fresh install did not produce PCCExecutive.exe.' }
if (-not (Test-Path $provenancePath)) { throw 'Fresh install did not produce build-provenance.json.' }

$provenance = Get-Content $provenancePath -Raw | ConvertFrom-Json
if ($provenance.Version -ne $ExpectedVersion) {
    throw "Installed version mismatch. expected=$ExpectedVersion actual=$($provenance.Version)"
}

$env:PCCEXECUTIVE_SMOKE_MODE = '1'
$process = Start-Process -FilePath $app -ArgumentList @('--installer-smoke') -PassThru
Start-Sleep -Seconds 3
if ($process.HasExited -and $process.ExitCode -ne 0) {
    throw "Installed application failed to launch: exit=$($process.ExitCode)"
}
if (-not $process.HasExited) {
    Stop-Process -Id $process.Id -Force
}

Write-Host "FRESH_INSTALL_SMOKE_PASS version=$ExpectedVersion installRoot=$InstallRoot"
