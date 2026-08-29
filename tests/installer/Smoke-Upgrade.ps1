[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PreviousInstallerPath,
    [Parameter(Mandatory)]
    [string]$CandidateInstallerPath,
    [Parameter(Mandatory)]
    [string]$CandidateManifestPath,
    [Parameter(Mandatory)]
    [string]$ExpectedVersion,
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'PCC Executive Smoke\Upgrade'),
    [string]$DataRoot = (Join-Path $env:LOCALAPPDATA 'PCC Executive')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Remove-Item $InstallRoot -Recurse -Force -ErrorAction SilentlyContinue

$oldInstall = Start-Process -FilePath (Resolve-Path $PreviousInstallerPath).Path -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/CURRENTUSER',"/DIR=`"$InstallRoot`"") -Wait -PassThru
if ($oldInstall.ExitCode -ne 0) { throw "Previous-version install failed: exit=$($oldInstall.ExitCode)" }

$seedDir = Join-Path $DataRoot 'Data'
New-Item -ItemType Directory -Path $seedDir -Force | Out-Null
$seedPath = Join-Path $seedDir 'upgrade-smoke-preserve.txt'
$seed = "preserve-$([Guid]::NewGuid().ToString('N'))"
Set-Content $seedPath $seed -Encoding UTF8

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
& (Join-Path $repoRoot 'updater\Invoke-Upgrade.ps1') `
    -PackagePath $CandidateInstallerPath `
    -ManifestPath $CandidateManifestPath `
    -InstallRoot $InstallRoot `
    -DataRoot $DataRoot

if ($LASTEXITCODE -ne 0) { throw 'Upgrade orchestrator failed.' }

$provenance = Get-Content (Join-Path $InstallRoot 'build-provenance.json') -Raw | ConvertFrom-Json
if ($provenance.Version -ne $ExpectedVersion) { throw 'Candidate version was not installed.' }
if (-not (Test-Path $seedPath)) { throw 'Seeded user data was deleted during upgrade.' }
if ((Get-Content $seedPath -Raw).Trim() -ne $seed) { throw 'Seeded user data changed during upgrade.' }

Write-Host "UPGRADE_SMOKE_PASS version=$ExpectedVersion"
