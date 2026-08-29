[CmdletBinding()]
param(
    [ValidateSet('Release')] [string]$Configuration = 'Release',
    [ValidateSet('win-x64')] [string]$Runtime = 'win-x64',
    [string]$ArtifactsRoot,
    [string]$InnoCompiler,
    [string]$DatabaseSchemaTarget = $env:PCCEXECUTIVE_DB_SCHEMA_TARGET,
    [string]$MinimumUpgradeVersion = $env:PCCEXECUTIVE_MINIMUM_UPGRADE_VERSION,
    [switch]$RequireSigning
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if ([string]::IsNullOrWhiteSpace($ArtifactsRoot)) { $ArtifactsRoot = Join-Path $repoRoot 'artifacts' }
$version = (Get-Content (Join-Path $repoRoot 'VERSION') -Raw).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw "Invalid VERSION '$version'." }
$baseVersion = ($version -split '-',2)[0]
$fileVersion = "$baseVersion.0"
$sourceSha = (& git -C $repoRoot rev-parse HEAD 2>$null).Trim().ToLowerInvariant()
if ($sourceSha -notmatch '^[0-9a-f]{40}$') { throw "Unable to establish exact SOURCE_SHA: '$sourceSha'." }

$appProject = Join-Path $repoRoot 'src/PCCExecutive.App/PCCExecutive.App.csproj'
if (-not (Test-Path $appProject)) { throw 'INSTALLER_BLOCKED: src/PCCExecutive.App/PCCExecutive.App.csproj is not integrated yet.' }
& (Join-Path $PSScriptRoot 'Build.ps1') -Configuration $Configuration -RequireProduct
if ($LASTEXITCODE -ne 0) { throw 'Build/test orchestration failed.' }

$publishDir = Join-Path $ArtifactsRoot "publish/$Runtime"
$packageDir = Join-Path $ArtifactsRoot 'package'
$evidenceDir = Join-Path $ArtifactsRoot 'release-evidence'
Remove-Item $packageDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $packageDir -Force | Out-Null
New-Item -ItemType Directory -Path $evidenceDir -Force | Out-Null
& (Join-Path $PSScriptRoot 'Publish-Windows.ps1') -Configuration $Configuration -Runtime $Runtime -OutputRoot $publishDir

# The self-contained app must run successfully before any installer is compiled.
& (Join-Path $repoRoot 'tests/installer/Smoke-PublishedApp.ps1') `
    -PublishRoot $publishDir `
    -ExpectedSourceSha $sourceSha `
    -EvidencePath (Join-Path $evidenceDir 'published-app-smoke.json')
if ($LASTEXITCODE -ne 0) { throw 'Published application smoke failed; installer creation is forbidden.' }

if ([string]::IsNullOrWhiteSpace($InnoCompiler)) {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe", "$env:ProgramFiles\Inno Setup 7\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    )
    $InnoCompiler = $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
}
if ([string]::IsNullOrWhiteSpace($InnoCompiler) -or -not (Test-Path $InnoCompiler)) { throw 'INNO_SETUP_NOT_FOUND: install Inno Setup 6+ or pass -InnoCompiler.' }

$workflowRun = if ($env:GITHUB_RUN_ID) { $env:GITHUB_RUN_ID } else { 'local' }
$buildId = if ($env:GITHUB_RUN_ATTEMPT) { "$workflowRun.$($env:GITHUB_RUN_ATTEMPT)" } else { $workflowRun }
$repository = if ($env:GITHUB_REPOSITORY) { $env:GITHUB_REPOSITORY } else { 'walidatiyaai2025-gif/walid' }
$generatedAt = [DateTimeOffset]::UtcNow.ToString('o')
if ([string]::IsNullOrWhiteSpace($DatabaseSchemaTarget)) {
    $schemaPath = Join-Path $repoRoot 'src/PCCExecutive.Infrastructure/SCHEMA_VERSION'
    $DatabaseSchemaTarget = if (Test-Path $schemaPath) { (Get-Content $schemaPath -Raw).Trim() } else { 'UNRESOLVED' }
}
if ([string]::IsNullOrWhiteSpace($MinimumUpgradeVersion)) { $MinimumUpgradeVersion = if ($version -eq '0.1.0') { 'NONE_FRESH_BASELINE' } else { 'UNRESOLVED' } }

$appExe = Join-Path $publishDir 'PCCExecutive.exe'
$signTargets = @($appExe)
$updaterExe = Join-Path $publishDir 'updater/PCCExecutive.Updater.exe'
if (Test-Path $updaterExe) { $signTargets += $updaterExe }
& (Join-Path $PSScriptRoot 'Sign-Release.ps1') -Files $signTargets -RequireSigned:$RequireSigning

[ordered]@{
    Product='PCC Executive'; Repository=$repository; Task='PCCEXECUTIVE-T0001'; Version=$version; SourceSha=$sourceSha; BuildId=$buildId; CiRun=$workflowRun;
    TargetArchitecture=$Runtime; Runtime=$Runtime; SelfContained=$true; GeneratedAt=$generatedAt; DatabaseSchemaTarget=$DatabaseSchemaTarget
} | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $publishDir 'build-provenance.json') -Encoding UTF8

$iss = Join-Path $repoRoot 'installer/PCCExecutive.iss'
$innoArgs = @(
    "/DMyAppVersion=$version",
    "/DMyFileVersion=$fileVersion",
    "/DSourceDir=$((Resolve-Path $publishDir).Path)",
    "/DOutputDir=$((Resolve-Path $packageDir).Path)",
    "/DSourceSha=$sourceSha",
    $iss
)
& $InnoCompiler @innoArgs
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed.' }
$artifactName = "PCCExecutive-$version-Setup-x64.exe"
$installerPath = Join-Path $packageDir $artifactName
if (-not (Test-Path $installerPath)) { throw "Expected installer artifact was not produced: $installerPath" }
if ((Get-Item $installerPath).Length -lt 1MB) { throw "Installer artifact is unrealistically small: $((Get-Item $installerPath).Length) bytes" }
& (Join-Path $PSScriptRoot 'Sign-Release.ps1') -Files @($installerPath) -RequireSigned:$RequireSigning

$context = if ($env:CI -or $env:GITHUB_ACTIONS) { 'CI' } else { 'Dev' }
$signingJson = & (Join-Path $PSScriptRoot 'Get-SigningState.ps1') -Files @($appExe,$installerPath) -Context $context -RequireSigned:$RequireSigning
$signing = ($signingJson -join "`n") | ConvertFrom-Json
if ($signing.signingState -eq 'SIGNATURE_INVALID') { throw 'SIGNATURE_INVALID' }

$sbomPath = Join-Path $packageDir "PCCExecutive-$version-sbom.json"
& (Join-Path $PSScriptRoot 'New-Sbom.ps1') -RepositoryRoot $repoRoot -OutputPath $sbomPath -InstallerCompilerPath $InnoCompiler
$appHash = 'sha256:' + (Get-FileHash $appExe -Algorithm SHA256).Hash.ToLowerInvariant()
$installerHash = 'sha256:' + (Get-FileHash $installerPath -Algorithm SHA256).Hash.ToLowerInvariant()
$packageIdentity = "PCCExecutive/$version/$Runtime/$sourceSha"

$updateManifest = [ordered]@{
    schemaVersion=2; product='PCC Executive'; repository=$repository; task='PCCEXECUTIVE-T0001'; version=$version; sourceSha=$sourceSha;
    artifactHash=$installerHash; targetArchitecture=$Runtime; runtime=$Runtime; selfContained=$true; generatedAt=$generatedAt; workflowRun=$workflowRun; buildId=$buildId;
    packageIdentity=$packageIdentity; fileName=$artifactName; applicationFileHash=$appHash; databaseSchemaTarget=$DatabaseSchemaTarget; minimumUpgradeVersion=$MinimumUpgradeVersion;
    signingState=[string]$signing.signingState; sbomReference=[IO.Path]::GetFileName($sbomPath)
}
$manifestPath = Join-Path $packageDir "PCCExecutive-$version-Setup-x64.manifest.json"
$updateManifest | ConvertTo-Json -Depth 6 | Set-Content $manifestPath -Encoding UTF8

$releaseManifest = [ordered]@{
    Product='PCC Executive'; Version=$version; SourceSha=$sourceSha; BuildId=$buildId; WorkflowRun=$workflowRun; Target=$Runtime; Runtime=$Runtime; SelfContained=$true;
    InstallerFile=$artifactName; InstallerSha256=$installerHash; ApplicationFileHash=$appHash; GeneratedAt=$generatedAt; DatabaseSchemaTarget=$DatabaseSchemaTarget;
    MinimumUpgradeVersion=$MinimumUpgradeVersion; SigningState=[string]$signing.signingState; SbomReference=[IO.Path]::GetFileName($sbomPath)
}
$releaseManifestPath = Join-Path $packageDir "PCCExecutive-$version-release-manifest.json"
$releaseManifest | ConvertTo-Json -Depth 6 | Set-Content $releaseManifestPath -Encoding UTF8

& (Join-Path $PSScriptRoot 'Test-ReleaseManifest.ps1') -ManifestPath $releaseManifestPath -InstallerPath $installerPath -ApplicationPath $appExe -ExpectedSourceSha $sourceSha -ExpectedVersion $version
& (Join-Path $repoRoot 'tests/installer/Test-Package.ps1') -InstallerPath $installerPath -ManifestPath $manifestPath -ExpectedSourceSha $sourceSha
Write-Host "INSTALLER=$installerPath"
Write-Host "UPDATE_MANIFEST=$manifestPath"
Write-Host "RELEASE_MANIFEST=$releaseManifestPath"
Write-Host "SBOM=$sbomPath"
Write-Host "SIGNING_STATE=$($signing.signingState)"
Write-Host "SHA256=$($installerHash.Substring(7))"
