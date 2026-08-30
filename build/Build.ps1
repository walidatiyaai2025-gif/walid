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

$sourceSha = (& git -C $repoRoot rev-parse HEAD 2>$null).Trim().ToLowerInvariant()
if ($sourceSha -notmatch '^[0-9a-f]{40}$') { throw "Exact SOURCE_SHA is required. Detected '$sourceSha'." }
$ciBuild = if ($env:GITHUB_ACTIONS) { 'true' } else { 'false' }

$buildEvidenceRoot = Join-Path $repoRoot 'artifacts\build'
New-Item -ItemType Directory -Path $buildEvidenceRoot -Force | Out-Null
Set-Content (Join-Path $buildEvidenceRoot 'source-sha.txt') $sourceSha -Encoding ascii

function Invoke-DotNetChecked {
    param(
        [Parameter(Mandatory)] [string[]]$Arguments,
        [Parameter(Mandatory)] [string]$FailureMessage
    )
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw $FailureMessage }
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
    Invoke-DotNetChecked -Arguments @('restore', $solution.FullName) -FailureMessage "dotnet restore failed: $($solution.FullName)"
    Invoke-DotNetChecked -Arguments @(
        'build', $solution.FullName,
        '--configuration', $Configuration,
        '--no-restore',
        "-p:Version=$version",
        "-p:ContinuousIntegrationBuild=$ciBuild"
    ) -FailureMessage "dotnet build failed: $($solution.FullName)"
}

# Build every source project explicitly. A stale solution file must never hide a failing Browser,
# Infrastructure, WPF, updater, PCC, GitHub or Application project after cross-worker convergence.
foreach ($project in $srcProjects) {
    Invoke-DotNetChecked -Arguments @('restore', $project.FullName) -FailureMessage "dotnet restore failed: $($project.FullName)"
    Invoke-DotNetChecked -Arguments @(
        'build', $project.FullName,
        '--configuration', $Configuration,
        '--no-restore',
        "-p:Version=$version",
        "-p:ContinuousIntegrationBuild=$ciBuild"
    ) -FailureMessage "dotnet build failed: $($project.FullName)"
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
    $testArgs = @(
        'test', $testProject.FullName,
        '--configuration', $Configuration,
        '--logger', 'trx',
        '--results-directory', $resultsDir,
        "-p:Version=$version",
        "-p:ContinuousIntegrationBuild=$ciBuild"
    )
    if (-not [string]::IsNullOrWhiteSpace($NormalTestFilter)) {
        $testArgs += @('--filter', $NormalTestFilter)
    }
    Invoke-DotNetChecked -Arguments $testArgs -FailureMessage "dotnet test failed: $($testProject.FullName)"
}

$appExe = Join-Path $repoRoot "src\PCCExecutive.App\bin\$Configuration\net10.0-windows\PCCExecutive.exe"
if (Test-Path $appExe) {
    & $appExe '--smoke-test'
    if ($LASTEXITCODE -ne 0) { throw "PCCExecutive WPF integrated startup smoke failed with exit code $LASTEXITCODE." }
}
elseif ($RequireProduct -or (Test-Path (Join-Path $repoRoot 'src\PCCExecutive.App\PCCExecutive.App.csproj'))) {
    throw "PCCExecutive WPF executable was not produced at expected path: $appExe"
}

Write-Host "BUILD_TEST_GATE_PASS sourceSha=$sourceSha version=$version configuration=$Configuration"
