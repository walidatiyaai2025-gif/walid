[CmdletBinding()]
param(
    [ValidateSet('Release')]
    [string]$Configuration = 'Release',
    [ValidateSet('win-x64')]
    [string]$Runtime = 'win-x64',
    [string]$ArtifactsRoot,
    [string]$InnoCompiler
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if ([string]::IsNullOrWhiteSpace($ArtifactsRoot)) {
    $ArtifactsRoot = Join-Path $repoRoot 'artifacts'
}

$version = (Get-Content (Join-Path $repoRoot 'VERSION') -Raw).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') {
    throw "Invalid VERSION '$version'."
}
$baseVersion = ($version -split '-', 2)[0]
$fileVersion = "$baseVersion.0"

$appProject = Join-Path $repoRoot 'src\PCCExecutive.App\PCCExecutive.App.csproj'
if (-not (Test-Path $appProject)) {
    throw 'INSTALLER_BLOCKED: src/PCCExecutive.App/PCCExecutive.App.csproj is not integrated yet.'
}

& (Join-Path $PSScriptRoot 'Build.ps1') -Configuration $Configuration -RequireProduct
if ($LASTEXITCODE -ne 0) { throw 'Build/test orchestration failed.' }

$publishDir = Join-Path $ArtifactsRoot "publish\$Runtime"
$packageDir = Join-Path $ArtifactsRoot 'package'
Remove-Item $publishDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $packageDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
New-Item -ItemType Directory -Path $packageDir -Force | Out-Null

& dotnet publish $appProject --configuration $Configuration --runtime $Runtime --self-contained true --output $publishDir -p:Version=$version -p:DebugSymbols=false -p:DebugType=None
if ($LASTEXITCODE -ne 0) { throw 'PCCExecutive.App publish failed.' }

$appExe = Join-Path $publishDir 'PCCExecutive.exe'
if (-not (Test-Path $appExe)) {
    throw 'INSTALLER_CONTRACT_BLOCKED: app publish must produce PCCExecutive.exe (set AssemblyName accordingly).'
}

$updaterProject = Join-Path $repoRoot 'src\PCCExecutive.Updater\PCCExecutive.Updater.csproj'
if (Test-Path $updaterProject) {
    $updaterDir = Join-Path $publishDir 'updater'
    New-Item -ItemType Directory -Path $updaterDir -Force | Out-Null
    & dotnet publish $updaterProject --configuration $Configuration --runtime $Runtime --self-contained true --output $updaterDir -p:Version=$version -p:DebugSymbols=false -p:DebugType=None
    if ($LASTEXITCODE -ne 0) { throw 'PCCExecutive.Updater publish failed.' }

    Copy-Item (Join-Path $repoRoot 'updater\Stage-Update.ps1') $updaterDir -Force
    Copy-Item (Join-Path $repoRoot 'updater\Invoke-Upgrade.ps1') $updaterDir -Force
    Copy-Item (Join-Path $repoRoot 'updater\update-manifest.schema.json') $updaterDir -Force
}

$forbiddenNames = @('Cookies','Login Data','Web Data','History','Preferences')
foreach ($name in $forbiddenNames) {
    if (Get-ChildItem -Path $publishDir -Recurse -Force -ErrorAction SilentlyContinue | Where-Object { $_.Name -eq $name }) {
        throw "Release payload contains forbidden browser/session material: $name"
    }
}

if (Get-ChildItem -Path $publishDir -Recurse -Force -Directory | Where-Object { $_.Name -match '^(User Data|BrowserProfiles?|ChatGPTProfiles?)$' }) {
    throw 'Release payload contains a browser profile directory.'
}

if (Get-ChildItem -Path $publishDir -Recurse -Force -File | Where-Object { $_.Extension -match '^\.(db|sqlite|sqlite3)$' }) {
    throw 'Release payload contains a SQLite/database file; durable user data must never be packaged.'
}

$sourceSha = $null
try {
    $sourceSha = (& git -C $repoRoot rev-parse HEAD 2>$null).Trim()
}
catch {
    $sourceSha = $env:GITHUB_SHA
}
if ([string]::IsNullOrWhiteSpace($sourceSha)) {
    $sourceSha = $env:GITHUB_SHA
}
if ($sourceSha -notmatch '^[0-9a-fA-F]{40}$') {
    throw "Unable to establish exact SOURCE_SHA. Got '$sourceSha'."
}

$generatedAt = [DateTimeOffset]::UtcNow.ToString('o')
$workflowRun = if ($env:GITHUB_RUN_ID) { $env:GITHUB_RUN_ID } else { 'local' }
$repository = if ($env:GITHUB_REPOSITORY) { $env:GITHUB_REPOSITORY } else { 'walidatiyaai2025-gif/walid' }
$buildId = if ($env:GITHUB_RUN_ATTEMPT) { "$workflowRun.$($env:GITHUB_RUN_ATTEMPT)" } else { $workflowRun }

$installedProvenance = [ordered]@{
    Product = 'PCC Executive'
    Repository = $repository
    Task = 'PCCEXECUTIVE-T0001'
    Version = $version
    SourceSha = $sourceSha
    BuildId = $buildId
    CiRun = $workflowRun
    TargetArchitecture = $Runtime
    GeneratedAt = $generatedAt
}
$installedProvenance | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $publishDir 'build-provenance.json') -Encoding UTF8

if ([string]::IsNullOrWhiteSpace($InnoCompiler)) {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 7\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    )
    $InnoCompiler = $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
}
if ([string]::IsNullOrWhiteSpace($InnoCompiler) -or -not (Test-Path $InnoCompiler)) {
    throw 'INNO_SETUP_NOT_FOUND: install Inno Setup 6+ or pass -InnoCompiler.'
}

$iss = Join-Path $repoRoot 'installer\PCCExecutive.iss'
$publishFull = (Resolve-Path $publishDir).Path
$packageFull = (Resolve-Path $packageDir).Path

& $InnoCompiler `
    "/DMyAppVersion=$version" `
    "/DMyFileVersion=$fileVersion" `
    "/DSourceDir=$publishFull" `
    "/DOutputDir=$packageFull" `
    "/DSourceSha=$sourceSha" `
    $iss
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed.' }

$artifactName = "PCCExecutive-$version-Setup-x64.exe"
$installerPath = Join-Path $packageDir $artifactName
if (-not (Test-Path $installerPath)) {
    throw "Expected installer artifact was not produced: $installerPath"
}

$hash = (Get-FileHash $installerPath -Algorithm SHA256).Hash.ToLowerInvariant()
$manifest = [ordered]@{
    schemaVersion = 1
    product = 'PCC Executive'
    repository = $repository
    task = 'PCCEXECUTIVE-T0001'
    version = $version
    sourceSha = $sourceSha.ToLowerInvariant()
    artifactHash = "sha256:$hash"
    targetArchitecture = $Runtime
    generatedAt = $generatedAt
    workflowRun = $workflowRun
    buildId = $buildId
    packageIdentity = "PCCExecutive/$version/$Runtime/$($sourceSha.ToLowerInvariant())"
    fileName = $artifactName
}
$manifestPath = Join-Path $packageDir "PCCExecutive-$version-Setup-x64.manifest.json"
$manifest | ConvertTo-Json -Depth 4 | Set-Content $manifestPath -Encoding UTF8

Write-Host "INSTALLER=$installerPath"
Write-Host "MANIFEST=$manifestPath"
Write-Host "SHA256=$hash"
