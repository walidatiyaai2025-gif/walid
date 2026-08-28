$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = Split-Path -Parent $PSScriptRoot
$sourcePath = Join-Path $PSScriptRoot 'Apply-FinalInternalBlockersV2.ps1'
$source = [IO.File]::ReadAllText($sourcePath)
$source = [regex]::Replace($source, '(?i)\$host(?![A-Za-z0-9_])', [System.Text.RegularExpressions.MatchEvaluator]{ param($m) '$hostText' })
$source = $source.Replace('$root = Split-Path -Parent $PSScriptRoot', '$root = $env:PCC_FINAL_REPO_ROOT')
$tempPath = Join-Path $env:RUNNER_TEMP 'Apply-FinalInternalBlockersV2.run.ps1'
$env:PCC_FINAL_REPO_ROOT = $repoRoot
[IO.File]::WriteAllText($tempPath, $source, [Text.UTF8Encoding]::new($false))
try {
    & $tempPath
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
finally {
    Remove-Item $tempPath -Force -ErrorAction SilentlyContinue
    Remove-Item Env:PCC_FINAL_REPO_ROOT -ErrorAction SilentlyContinue
}
