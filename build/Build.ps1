[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Release',
    [switch]$RequireProduct,
    [string]$NormalTestFilter = 'Category!=LiveBrowser'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$versionPath = Join-Path $repoRoot 'VERSION'
if (-not (Test-Path $versionPath)) { throw 'VERSION is required.' }

$version = (Get-Content $versionPath -Raw).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') {
    throw "VERSION '$version' is not valid semantic version syntax."
}

$dotnetVersion = (& dotnet --version).Trim()
if ($LASTEXITCODE -ne 0 -or $dotnetVersion -notmatch '^10\.') {
    throw ".NET 10 SDK is required. Detected '$dotnetVersion'."
}

$solutions = @(
    Get-ChildItem -Path $repoRoot -Recurse -File -Filter '*.slnx' |
        Where-Object { $_.FullName -notmatch '[\\/](artifacts|bin|obj)[\\/]' } |
        Sort-Object FullName
)
if ($solutions.Count -eq 0) {
    $solutions = @(
        Get-ChildItem -Path $repoRoot -Recurse -File -Filter '*.sln' |
            Where-Object { $_.FullName -notmatch '[\\/](artifacts|bin|obj)[\\/]' } |
            Sort-Object FullName
    )
}

$srcProjects = @(
    Get-ChildItem -Path (Join-Path $repoRoot 'src') -Recurse -File -Filter '*.csproj' -ErrorAction SilentlyContinue |
        Sort-Object FullName
)

if ($solutions.Count -eq 0 -and $srcProjects.Count -eq 0) {
    if ($RequireProduct) { throw 'PRODUCT_SOURCE_NOT_PRESENT: no .NET solution/project exists yet on this branch.' }
    Write-Host 'PRODUCT_SOURCE_NOT_PRESENT: release infrastructure validated; application build is blocked on integrated source.'
    exit 0
}

foreach ($solution in $solutions) {
    & dotnet restore $solution.FullName
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed: $($solution.FullName)" }
    & dotnet build $solution.FullName --configuration $Configuration --no-restore -p:Version=$version -p:ContinuousIntegrationBuild=$([bool]$env:GITHUB_ACTIONS)
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed: $($solution.FullName)" }
}

# Build every source project explicitly. This prevents a stale solution file from silently omitting
# Browser, Infrastructure, WPF, updater, PCC or GitHub modules after cross-worker integration.
foreach ($project in $srcProjects) {
    & dotnet restore $project.FullName
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed: $($project.FullName)" }
    & dotnet build $project.FullName --configuration $Configuration --no-restore -p:Version=$version -p:ContinuousIntegrationBuild=$([bool]$env:GITHUB_ACTIONS)
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed: $($project.FullName)" }
}

$testProjects = @(
    Get-ChildItem -Path (Join-Path $repoRoot 'tests') -Recurse -File -Filter '*.csproj' -ErrorAction SilentlyContinue |
        Sort-Object FullName
)

if ($testProjects.Count -eq 0) {
    $appProject = Join-Path $repoRoot 'src\PCCExecutive.App\PCCExecutive.App.csproj'
    if ($RequireProduct -or (Test-Path $appProject)) { throw 'TEST_INFRASTRUCTURE_NOT_INTEGRATED: product source exists but no test projects were found.' }
    Write-Host 'PARTIAL_SOURCE_BUILD_VALID: owned infrastructure projects built; product test lane awaits PCCExecutive.App integration.'
    exit 0
}

$resultsDir = Join-Path $repoRoot 'artifacts\test-results'
New-Item -ItemType Directory -Path $resultsDir -Force | Out-Null

foreach ($testProject in $testProjects) {
    $args = @(
        'test', $testProject.FullName,
        '--configuration', $Configuration,
        '--logger', 'trx',
        '--results-directory', $resultsDir,
        ('-p:Version=' + $version),
        ('-p:ContinuousIntegrationBuild=' + [bool]$env:GITHUB_ACTIONS)
    )
    if (-not [string]::IsNullOrWhiteSpace($NormalTestFilter)) { $args += @('--filter', $NormalTestFilter) }
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet test failed: $($testProject.FullName)" }
}

$appExe = Join-Path $repoRoot "src\PCCExecutive.App\bin\$Configuration\net10.0-windows\PCCExecutive.exe"
if (Test-Path $appExe) {
    & $appExe --smoke-test
    if ($LASTEXITCODE -ne 0) { throw "PCCExecutive WPF integrated startup smoke failed with exit code $LASTEXITCODE." }
}
elseif ($RequireProduct -or (Test-Path (Join-Path $repoRoot 'src\PCCExecutive.App\PCCExecutive.App.csproj'))) {
    throw "PCCExecutive WPF executable was not produced at expected path: $appExe"
}
