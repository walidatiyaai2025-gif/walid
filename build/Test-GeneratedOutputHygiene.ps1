[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

$tracked = @(& git -C $repoRoot ls-files)
if ($LASTEXITCODE -ne 0) { throw 'Unable to enumerate tracked repository files.' }

$forbiddenPatterns = @(
    '(^|/)(bin|obj)/',
    '\.(dll|pdb)$',
    '(^|/)(TestResults|artifacts/tmp|tmp|temp)/',
    '(^|/)(\.nuget|nuget-cache|packages)/',
    '\.(tmp|temp|log)$'
)

$violations = [System.Collections.Generic.List[string]]::new()
foreach ($path in $tracked) {
    $normalized = $path.Replace('\\','/')
    foreach ($pattern in $forbiddenPatterns) {
        if ($normalized -match $pattern) {
            $violations.Add($normalized)
            break
        }
    }
}

if ($violations.Count -gt 0) {
    throw "GENERATED_OUTPUT_REJECTED:`n - " + (($violations | Sort-Object -Unique) -join "`n - ")
}

Write-Host "GENERATED_OUTPUT_SCAN_PASS tracked=$($tracked.Count)"
