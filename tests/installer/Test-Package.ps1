[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$InstallerPath,
    [Parameter(Mandatory)]
    [string]$ManifestPath,
    [string]$ExpectedSourceSha = $env:GITHUB_SHA
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$installer = Get-Item -LiteralPath $InstallerPath
$manifest = Get-Content (Get-Item -LiteralPath $ManifestPath).FullName -Raw | ConvertFrom-Json
$hash = (Get-FileHash $installer.FullName -Algorithm SHA256).Hash.ToLowerInvariant()

if ($manifest.fileName -ne $installer.Name) { throw 'Package filename/manifest mismatch.' }
if ($manifest.targetArchitecture -ne 'win-x64') { throw 'Package architecture mismatch.' }
if ($manifest.artifactHash -ne "sha256:$hash") { throw 'Package SHA-256 mismatch.' }
if ($manifest.sourceSha -notmatch '^[0-9a-f]{40}$') { throw 'Manifest source SHA is invalid.' }
if ($ExpectedSourceSha -and $manifest.sourceSha -ne $ExpectedSourceSha.ToLowerInvariant()) {
    throw "Package source SHA does not match exact candidate head. expected=$ExpectedSourceSha actual=$($manifest.sourceSha)"
}
if ($installer.Name -notmatch "^PCCExecutive-$([regex]::Escape($manifest.version))-Setup-x64\.exe$") {
    throw 'Package name is not version-governed.'
}

Write-Host "PACKAGE_VERIFIED version=$($manifest.version) source=$($manifest.sourceSha) sha256=$hash"
