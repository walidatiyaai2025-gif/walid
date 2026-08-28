Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

param(
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Release',
    [switch]$RequireProduct,
    [string]$NormalTestFilter = 'Category!=LiveBrowser'
)

$repoRoot = Split-Path -Parent $PSScriptRoot
$version = (Get-Content (Join-Path $repoRoot 'VERSION') -Raw).Trim()
$solution = Join-Path $repoRoot 'PCCExecutive.sln'

if (Test-Path $solution) {
    & dotnet restore $solution
    if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed.' }

    & dotnet build $solution --configuration $Configuration --no-restore -p:Version=$version -p:ContinuousIntegrationBuild=$([bool]$env:GITHUB_ACTIONS)
    if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed.' }
}
elseif ($RequireProduct) {
    throw 'PCCExecutive.sln is required for a product build.'
}
else {
    Write-Host 'PCCExecutive.sln not present yet; validating any integrated .NET projects directly.'
}

$srcProjects = @(Get-ChildItem (Join-Path $repoRoot 'src') -Recurse -Filter '*.csproj' -File -ErrorAction SilentlyContinue | Sort-Object FullName -Unique)
foreach ($project in $srcProjects) {
    & dotnet restore $project.FullName
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed: $($project.FullName)" }

    & dotnet build $project.FullName --configuration $Configuration --no-restore -p:Version=$version -p:ContinuousIntegrationBuild=$([bool]$env:GITHUB_ACTIONS)
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed: $($project.FullName)" }
}

$testProjects = @(Get-ChildItem (Join-Path $repoRoot 'tests') -Recurse -Filter '*.csproj' -File -ErrorAction SilentlyContinue | Sort-Object FullName -Unique)
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
        ("-p:Version=$version"),
        ("-p:ContinuousIntegrationBuild=$([bool]$env:GITHUB_ACTIONS)")
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
