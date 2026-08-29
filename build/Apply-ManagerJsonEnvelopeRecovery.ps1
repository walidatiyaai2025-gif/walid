$ErrorActionPreference = 'Stop'

$path = 'src/PCCExecutive.Application/ManagerOrchestration.cs'
$text = Get-Content -Raw -LiteralPath $path
$old = '            wire = JsonSerializer.Deserialize<WirePlan>(content, _json);'
$new = '            wire = JsonSerializer.Deserialize<WirePlan>(ManagerPlanJsonEnvelope.ExtractSinglePlanObject(content), _json);'

if (-not $text.Contains($old)) {
    throw 'StructuredManagerPlanParser deserialize boundary was not found at the expected exact source line.'
}

$text = $text.Replace($old, $new)
Set-Content -LiteralPath $path -Value $text -Encoding utf8NoBOM

Write-Host 'StructuredManagerPlanParser now extracts exactly one bounded JSON plan object before deserialization.'
