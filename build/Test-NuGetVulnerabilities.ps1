[CmdletBinding()]
param(
    [string]$RepositoryRoot
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) { $RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path }
$projects = @(Get-ChildItem (Join-Path $RepositoryRoot 'src') -Recurse -File -Filter '*.csproj' -ErrorAction SilentlyContinue | Sort-Object FullName)
if ($projects.Count -eq 0) { throw 'SECURITY_SCAN_BLOCKED: no source projects found.' }

function Count-Vulnerabilities($node) {
    if ($null -eq $node) { return 0 }
    $count = 0
    if ($node -is [System.Collections.IEnumerable] -and $node -isnot [string]) {
        foreach ($item in $node) { $count += Count-Vulnerabilities $item }
        return $count
    }
    if ($node.PSObject) {
        foreach ($property in $node.PSObject.Properties) {
            if ($property.Name -eq 'vulnerabilities' -and $null -ne $property.Value) {
                $count += @($property.Value).Count
            } else {
                $count += Count-Vulnerabilities $property.Value
            }
        }
    }
    return $count
}

$total = 0
foreach ($project in $projects) {
    $raw = & dotnet list $project.FullName package --vulnerable --include-transitive --format json 2>&1
    if ($LASTEXITCODE -ne 0) { throw "SECURITY_SCAN_BLOCKED: NuGet vulnerability query failed for $($project.FullName): $($raw -join ' ')" }
    try { $json = ($raw -join "`n") | ConvertFrom-Json } catch { throw "SECURITY_SCAN_BLOCKED: vulnerability output was not valid JSON for $($project.FullName)." }
    $total += Count-Vulnerabilities $json
}
if ($total -gt 0) { throw "SECURITY_SCAN_FAILED: $total vulnerable NuGet dependency record(s) reported." }
Write-Host "NUGET_VULNERABILITY_SCAN_PASS projects=$($projects.Count)"
