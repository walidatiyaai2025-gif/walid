[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$ManifestPath,
    [string]$InstallerPath,
    [string]$ApplicationPath,
    [string]$ExpectedSourceSha,
    [string]$ExpectedVersion,
    [string]$ExpectedProduct = 'PCC Executive',
    [string]$ExpectedTarget = 'win-x64'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$manifest = Get-Content $ManifestPath -Raw | ConvertFrom-Json
$required = @('Product','Version','SourceSha','BuildId','WorkflowRun','Target','Runtime','SelfContained','InstallerFile','InstallerSha256','ApplicationFileHash','GeneratedAt','DatabaseSchemaTarget','MinimumUpgradeVersion','SigningState','SbomReference')
foreach ($field in $required) {
    if ($null -eq $manifest.$field -or [string]::IsNullOrWhiteSpace([string]$manifest.$field)) { throw "RELEASE_MANIFEST_INVALID: missing $field" }
}
if ($manifest.Product -ne $ExpectedProduct) { throw "WRONG_PRODUCT: '$($manifest.Product)'" }
if ($manifest.Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw "INVALID_VERSION: '$($manifest.Version)'" }
if (-not [string]::IsNullOrWhiteSpace($ExpectedVersion) -and $manifest.Version -ne $ExpectedVersion) { throw "VERSION_MISMATCH: '$($manifest.Version)' != '$ExpectedVersion'" }
if ($manifest.Target -ne $ExpectedTarget -or $manifest.Runtime -ne $ExpectedTarget) { throw "WRONG_ARCHITECTURE: target=$($manifest.Target) runtime=$($manifest.Runtime)" }
if ($manifest.SelfContained -ne $true) { throw 'PUBLISH_NOT_SELF_CONTAINED' }
if ($manifest.SourceSha -notmatch '^[0-9a-fA-F]{40}$') { throw 'INVALID_SOURCE_SHA' }
if (-not [string]::IsNullOrWhiteSpace($ExpectedSourceSha) -and $manifest.SourceSha.ToLowerInvariant() -ne $ExpectedSourceSha.ToLowerInvariant()) { throw 'SOURCE_SHA_MISMATCH' }
if (@('UNSIGNED_DEV','SIGNING_NOT_CONFIGURED','SIGNED','SIGNATURE_INVALID') -notcontains [string]$manifest.SigningState) { throw "INVALID_SIGNING_STATE: $($manifest.SigningState)" }
if ($manifest.SigningState -eq 'SIGNATURE_INVALID') { throw 'SIGNATURE_INVALID' }

if (-not [string]::IsNullOrWhiteSpace($InstallerPath)) {
    if (-not (Test-Path $InstallerPath)) { throw 'INSTALLER_MISSING' }
    $actual = 'sha256:' + (Get-FileHash $InstallerPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne ([string]$manifest.InstallerSha256).ToLowerInvariant()) { throw "PACKAGE_HASH_MISMATCH expected=$($manifest.InstallerSha256) actual=$actual" }
    if ([IO.Path]::GetFileName($InstallerPath) -ne $manifest.InstallerFile) { throw 'INSTALLER_FILE_NAME_MISMATCH' }
}
if (-not [string]::IsNullOrWhiteSpace($ApplicationPath)) {
    if (-not (Test-Path $ApplicationPath)) { throw 'APPLICATION_FILE_MISSING' }
    $actualApp = 'sha256:' + (Get-FileHash $ApplicationPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualApp -ne ([string]$manifest.ApplicationFileHash).ToLowerInvariant()) { throw 'APPLICATION_HASH_MISMATCH' }
}
Write-Host 'RELEASE_MANIFEST_VALID'
