[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$PackagePath,
    [Parameter(Mandatory)] [string]$ManifestPath,
    [string]$StageRoot = (Join-Path $env:LOCALAPPDATA 'PCC Executive\Updates'),
    [switch]$RequireSigned
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$package = Get-Item -LiteralPath $PackagePath
$manifestFile = Get-Item -LiteralPath $ManifestPath
$manifest = Get-Content $manifestFile.FullName -Raw | ConvertFrom-Json
$required = @('schemaVersion','product','repository','task','version','sourceSha','artifactHash','targetArchitecture','runtime','selfContained','generatedAt','workflowRun','buildId','packageIdentity','fileName','applicationFileHash','databaseSchemaTarget','minimumUpgradeVersion','signingState','sbomReference')
foreach ($name in $required) { if ($null -eq $manifest.PSObject.Properties[$name]) { throw "UNVERIFIED_PACKAGE: manifest field '$name' is missing." } }
if ($manifest.schemaVersion -ne 2) { throw 'UNVERIFIED_PACKAGE: unsupported schemaVersion.' }
if ($manifest.product -ne 'PCC Executive') { throw 'UNVERIFIED_PACKAGE: wrong product identity.' }
if ($manifest.repository -ne 'walidatiyaai2025-gif/walid') { throw 'UNVERIFIED_PACKAGE: wrong repository identity.' }
if ($manifest.task -ne 'PCCEXECUTIVE-T0001') { throw 'UNVERIFIED_PACKAGE: wrong task identity.' }
if ($manifest.version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw 'UNVERIFIED_PACKAGE: invalid version.' }
if ($manifest.sourceSha -notmatch '^[0-9a-f]{40}$') { throw 'UNVERIFIED_PACKAGE: invalid source SHA.' }
if ($manifest.targetArchitecture -ne 'win-x64' -or $manifest.runtime -ne 'win-x64') { throw 'UNVERIFIED_PACKAGE: unsupported architecture/runtime.' }
if ($manifest.selfContained -ne $true) { throw 'UNVERIFIED_PACKAGE: update is not self-contained.' }
if ($manifest.fileName -ne $package.Name) { throw 'UNVERIFIED_PACKAGE: file name does not match manifest.' }
if ($manifest.packageIdentity -ne "PCCExecutive/$($manifest.version)/win-x64/$($manifest.sourceSha)") { throw 'UNVERIFIED_PACKAGE: package identity does not match version/source/architecture.' }
if ($manifest.applicationFileHash -notmatch '^sha256:[0-9a-f]{64}$') { throw 'UNVERIFIED_PACKAGE: invalid application file hash.' }
if (@('UNSIGNED_DEV','SIGNING_NOT_CONFIGURED','SIGNED','SIGNATURE_INVALID') -notcontains [string]$manifest.signingState) { throw 'UNVERIFIED_PACKAGE: invalid signing state.' }
if ($manifest.signingState -eq 'SIGNATURE_INVALID') { throw 'UNVERIFIED_PACKAGE: signature is invalid.' }
if ($RequireSigned -and $manifest.signingState -ne 'SIGNED') { throw "UNVERIFIED_PACKAGE: SIGNED package required, manifest state=$($manifest.signingState)." }
$expected = [string]$manifest.artifactHash
if ($expected -notmatch '^sha256:([0-9a-f]{64})$') { throw 'UNVERIFIED_PACKAGE: artifactHash is not a SHA-256 identity.' }
$expectedHash = $Matches[1]
$actual = (Get-FileHash $package.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $expectedHash) { throw "UNVERIFIED_PACKAGE: SHA-256 mismatch. expected=$expectedHash actual=$actual" }
$parsedDate = [DateTimeOffset]::MinValue
if (-not [DateTimeOffset]::TryParse([string]$manifest.generatedAt, [ref]$parsedDate)) { throw 'UNVERIFIED_PACKAGE: generatedAt is not a valid timestamp.' }

$stageDir = Join-Path $StageRoot $manifest.version
New-Item -ItemType Directory -Path $stageDir -Force | Out-Null
$stagedPackage = Join-Path $stageDir $package.Name
$stagedManifest = Join-Path $stageDir $manifestFile.Name
Copy-Item -LiteralPath $package.FullName -Destination $stagedPackage -Force
Copy-Item -LiteralPath $manifestFile.FullName -Destination $stagedManifest -Force
$stageRecord = [ordered]@{
    State='VERIFIED_STAGED'; Version=$manifest.version; SourceSha=$manifest.sourceSha; ArtifactHash=$manifest.artifactHash; PackageIdentity=$manifest.packageIdentity;
    SigningState=$manifest.signingState; DatabaseSchemaTarget=$manifest.databaseSchemaTarget; VerifiedAt=[DateTimeOffset]::UtcNow.ToString('o'); PackagePath=$stagedPackage; ManifestPath=$stagedManifest
}
$recordPath = Join-Path $stageDir 'verified-stage.json'
$stageRecord | ConvertTo-Json -Depth 4 | Set-Content $recordPath -Encoding UTF8
Write-Output $recordPath
