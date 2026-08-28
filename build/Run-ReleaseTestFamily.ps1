[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Unit','Integration','Browser Deterministic','Persistence')]
    [string]$Family,
    [string]$RepositoryRoot,
    [switch]$Require,
    [string]$EvidenceRoot
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) { $RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path }
$config = Get-Content (Join-Path $RepositoryRoot 'release/required-modules.json') -Raw | ConvertFrom-Json
$definition = $config.testFamilies | Where-Object Name -eq $Family | Select-Object -First 1
if (-not $definition) { throw "Unknown test family '$Family'." }
$projects = @()
foreach ($pattern in $definition.patterns) {
    $projects += @(Get-ChildItem -Path (Join-Path $RepositoryRoot ([string]$pattern)) -File -ErrorAction SilentlyContinue)
}
$projects = @($projects | Sort-Object FullName -Unique)
if ($projects.Count -eq 0) {
    if ($Require) { throw "BLOCKED_DEPENDENCY: no projects found for required test family '$Family'." }
    Write-Host "BLOCKED_DEPENDENCY family=$Family"
    exit 0
}
$filter = if ($Family -eq 'Browser Deterministic') { 'Category!=LiveBrowser' } else { $null }
foreach ($project in $projects) {
    $args = @('test',$project.FullName,'--configuration','Release','--no-restore')
    if ($filter) { $args += @('--filter',$filter) }
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "Release test family '$Family' failed: $($project.FullName)" }
}
$gate = switch ($Family) {
    'Unit' { 'UNIT_TESTS' }
    'Integration' { 'INTEGRATION_TESTS' }
    'Browser Deterministic' { 'BROWSER_DETERMINISTIC_TESTS' }
    'Persistence' { 'PERSISTENCE_TESTS' }
}
& (Join-Path $PSScriptRoot 'Write-GateEvidence.ps1') -Gate $gate -RepositoryRoot $RepositoryRoot -EvidenceRoot $EvidenceRoot -Details "$Family projects=$($projects.Count)"
Write-Host "TEST_FAMILY_PASS family=$Family projects=$($projects.Count)"
