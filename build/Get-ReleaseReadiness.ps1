[CmdletBinding()]
param(
    [string]$RepositoryRoot,
    [string]$EvidenceRoot,
    [string]$OutputPath,
    [ValidateSet('Foundation','ReviewCandidate','ProductionCandidate')]
    [string]$Mode = 'Foundation',
    [switch]$ModulesOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) { $RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path }
$RepositoryRoot = (Resolve-Path $RepositoryRoot).Path
if ([string]::IsNullOrWhiteSpace($EvidenceRoot)) { $EvidenceRoot = Join-Path $RepositoryRoot 'artifacts/release-evidence' }
if ([string]::IsNullOrWhiteSpace($OutputPath)) { $OutputPath = Join-Path $RepositoryRoot 'artifacts/release/readiness.json' }

$moduleConfig = Get-Content (Join-Path $RepositoryRoot 'release/required-modules.json') -Raw | ConvertFrom-Json
$gateConfig = Get-Content (Join-Path $RepositoryRoot 'release/release-gates.json') -Raw | ConvertFrom-Json
$sha = 'UNKNOWN'
try {
    $rawSha = & git -C $RepositoryRoot rev-parse HEAD 2>$null
    if ($LASTEXITCODE -eq 0 -and $rawSha) {
        $candidateSha = ([string]($rawSha | Select-Object -First 1)).Trim().ToLowerInvariant()
        if ($candidateSha -match '^[0-9a-f]{40}$') { $sha = $candidateSha }
    }
} catch { $sha = 'UNKNOWN' }

$modules = @()
$moduleState = @{}
foreach ($module in $moduleConfig.requiredForIntegratedCandidate) {
    $present = Test-Path (Join-Path $RepositoryRoot ([string]$module.path))
    $state = if ($present) { 'FOUND' } else { 'MISSING' }
    $moduleState[[string]$module.name] = $state
    $modules += [pscustomobject]@{ Name=[string]$module.name; Path=[string]$module.path; State=$state }
}

$testFamilies = @()
foreach ($family in $moduleConfig.testFamilies) {
    $matches = @()
    foreach ($pattern in $family.patterns) {
        $matches += @(Get-ChildItem -Path (Join-Path $RepositoryRoot ([string]$pattern)) -File -ErrorAction SilentlyContinue)
    }
    $testFamilies += [pscustomobject]@{ Name=[string]$family.name; State=if($matches.Count -gt 0){'FOUND'}else{'MISSING'}; Projects=@($matches | ForEach-Object { [IO.Path]::GetRelativePath($RepositoryRoot,$_.FullName).Replace('\\','/') } | Sort-Object -Unique) }
}

$gates = @()
if (-not $ModulesOnly) {
    foreach ($gate in $gateConfig.gates) {
        $missingDependencies = @($gate.dependencies | Where-Object { $moduleState[[string]$_] -ne 'FOUND' })
        $status = 'PENDING'
        $reason = ''
        if ($missingDependencies.Count -gt 0) {
            $status = 'BLOCKED_DEPENDENCY'
            $reason = 'Missing modules: ' + ($missingDependencies -join ', ')
        } else {
            $evidencePath = Join-Path $EvidenceRoot "$($gate.name).json"
            if (Test-Path $evidencePath) {
                try {
                    $evidence = Get-Content $evidencePath -Raw | ConvertFrom-Json
                    if ($evidence.Gate -ne $gate.name) { $status='FAIL'; $reason='Evidence gate identity mismatch' }
                    elseif ($sha -ne 'UNKNOWN' -and ([string]$evidence.SourceSha).ToLowerInvariant() -ne $sha) { $status='FAIL'; $reason='Evidence source SHA mismatch' }
                    elseif (@('PASS','FAIL','NOT_APPLICABLE') -contains [string]$evidence.Status) { $status=[string]$evidence.Status; $reason=[string]$evidence.Details }
                    else { $status='FAIL'; $reason='Invalid evidence status' }
                } catch { $status='FAIL'; $reason='Unreadable gate evidence' }
            }
        }
        $gates += [pscustomobject]@{ Name=[string]$gate.name; Status=$status; Reason=$reason }
    }
}

$missingModules = @($modules | Where-Object State -eq 'MISSING')
$overall = if ($missingModules.Count -gt 0) { 'BLOCKED_DEPENDENCY' } elseif ($ModulesOnly) { 'MODULES_READY' } elseif (@($gates | Where-Object { $_.Status -notin @('PASS','NOT_APPLICABLE') }).Count -eq 0) { 'READY' } else { 'NOT_READY' }
$report = [ordered]@{
    Product='PCC Executive'
    Version=(Get-Content (Join-Path $RepositoryRoot 'VERSION') -Raw).Trim()
    SourceSha=$sha
    Mode=$Mode
    Overall=$overall
    Modules=$modules
    TestFamilies=$testFamilies
    Gates=$gates
}
New-Item -ItemType Directory -Path (Split-Path $OutputPath -Parent) -Force | Out-Null
$report | ConvertTo-Json -Depth 8 | Set-Content $OutputPath -Encoding UTF8
$report | ConvertTo-Json -Depth 8

if ($Mode -eq 'ProductionCandidate') {
    if ($ModulesOnly -and $missingModules.Count -gt 0) { throw "BLOCKED_DEPENDENCY: missing required modules: $($missingModules.Name -join ', ')" }
    if (-not $ModulesOnly -and $overall -ne 'READY') { throw "RELEASE_GATE_BLOCKED: production candidate is $overall." }
}
