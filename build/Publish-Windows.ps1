[CmdletBinding()]
param(
    [ValidateSet('Release')] [string]$Configuration = 'Release',
    [ValidateSet('win-x64')] [string]$Runtime = 'win-x64',
    [string]$OutputRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = Join-Path $repoRoot "artifacts/publish/$Runtime" }
$version = (Get-Content (Join-Path $repoRoot 'VERSION') -Raw).Trim()
$appProject = Join-Path $repoRoot 'src/PCCExecutive.App/PCCExecutive.App.csproj'
if (-not (Test-Path $appProject)) { throw 'BLOCKED_DEPENDENCY: PCCExecutive.App is required for production publish.' }
$sha = (& git -C $repoRoot rev-parse HEAD 2>$null).Trim().ToLowerInvariant()
if ($sha -notmatch '^[0-9a-f]{40}$') { throw 'Exact source SHA is required.' }

Remove-Item $OutputRoot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
$publishArgs = @(
    'publish', $appProject,
    '--configuration', $Configuration,
    '--runtime', $Runtime,
    '--self-contained', 'true',
    '--output', $OutputRoot,
    '-p:Version=' + $version,
    '-p:SelfContained=true',
    '-p:PublishTrimmed=false',
    '-p:PublishSingleFile=false',
    '-p:DebugSymbols=false',
    '-p:DebugType=None',
    '-p:Deterministic=true',
    '-p:ContinuousIntegrationBuild=true'
)
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw 'PCCExecutive.App self-contained publish failed.' }
$appExe = Join-Path $OutputRoot 'PCCExecutive.exe'
if (-not (Test-Path $appExe)) { throw 'Publish contract requires PCCExecutive.exe.' }

$updaterProject = Join-Path $repoRoot 'src/PCCExecutive.Updater/PCCExecutive.Updater.csproj'
if (Test-Path $updaterProject) {
    $updaterDir = Join-Path $OutputRoot 'updater'
    New-Item -ItemType Directory -Path $updaterDir -Force | Out-Null
    & dotnet publish $updaterProject --configuration $Configuration --runtime $Runtime --self-contained true --output $updaterDir -p:Version=$version -p:PublishTrimmed=false -p:DebugSymbols=false -p:DebugType=None -p:Deterministic=true -p:ContinuousIntegrationBuild=true
    if ($LASTEXITCODE -ne 0) { throw 'PCCExecutive.Updater self-contained publish failed.' }
    foreach ($script in @('Stage-Update.ps1','Invoke-Upgrade.ps1','update-manifest.schema.json')) {
        Copy-Item (Join-Path $repoRoot "updater/$script") $updaterDir -Force
    }
}

& (Join-Path $PSScriptRoot 'Test-ReleasePayload.ps1') -PayloadRoot $OutputRoot
$appHash = 'sha256:' + (Get-FileHash $appExe -Algorithm SHA256).Hash.ToLowerInvariant()
[ordered]@{
    Product='PCC Executive'; Version=$version; SourceSha=$sha; Target=$Runtime; Runtime=$Runtime; SelfContained=$true;
    DotNetSdk=(& dotnet --version).Trim(); ApplicationFile='PCCExecutive.exe'; ApplicationFileHash=$appHash
} | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $OutputRoot 'publish-manifest.json') -Encoding UTF8
Write-Host "PUBLISH_ROOT=$OutputRoot"
