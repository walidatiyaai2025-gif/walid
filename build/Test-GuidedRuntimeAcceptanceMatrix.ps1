[CmdletBinding()]
param(
    [string]$MatrixPath = (Join-Path $PSScriptRoot '..\tests\guided-runtime\acceptance-matrix.json')
)

$ErrorActionPreference = 'Stop'
$matrix = Get-Content -Raw -LiteralPath $MatrixPath | ConvertFrom-Json

if ($matrix.schemaVersion -ne 1) {
    throw "Unsupported guided-runtime acceptance schema: $($matrix.schemaVersion)."
}

$cases = @($matrix.cases)
if ($cases.Count -ne $matrix.requiredCaseCount -or $cases.Count -ne 55) {
    throw "Guided-runtime acceptance must define exactly 55 cases; found $($cases.Count)."
}

$duplicateIds = $cases | Group-Object id | Where-Object Count -gt 1
if ($duplicateIds) {
    throw "Duplicate acceptance case IDs: $(($duplicateIds.Name) -join ', ')."
}

$invalidCases = $cases | Where-Object {
    [string]::IsNullOrWhiteSpace($_.id) -or
    [string]::IsNullOrWhiteSpace($_.area) -or
    [string]::IsNullOrWhiteSpace($_.requirement)
}
if ($invalidCases) {
    throw "Every acceptance case requires a non-empty id, area and requirement."
}

$expectedPrefixes = [ordered]@{ GR001 = 10; GR002 = 10; GR003 = 12; GR004 = 10; GR005 = 13 }
foreach ($entry in $expectedPrefixes.GetEnumerator()) {
    $actual = @($cases | Where-Object id -Like "$($entry.Key)-*").Count
    if ($actual -ne $entry.Value) {
        throw "$($entry.Key) must contain $($entry.Value) cases; found $actual."
    }
}

[pscustomobject]@{
    Result = 'VALID'
    Cases = $cases.Count
    Matrix = (Resolve-Path -LiteralPath $MatrixPath).Path
}
