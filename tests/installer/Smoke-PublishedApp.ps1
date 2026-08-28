[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$PublishRoot,
    [Parameter(Mandatory)] [string]$ExpectedSourceSha,
    [string]$EvidencePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path $PublishRoot).Path
$app = Join-Path $root 'PCCExecutive.exe'
$manifestPath = Join-Path $root 'publish-manifest.json'
if (-not (Test-Path $app)) { throw 'PUBLISHED_APP_MISSING: PCCExecutive.exe was not produced.' }
if (-not (Test-Path $manifestPath)) { throw 'PUBLISHED_MANIFEST_MISSING: publish-manifest.json was not produced.' }
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
if ([string]$manifest.SourceSha -ne $ExpectedSourceSha) {
    throw "PUBLISHED_SOURCE_SHA_MISMATCH expected=$ExpectedSourceSha actual=$($manifest.SourceSha)"
}

$process = Start-Process -FilePath $app -ArgumentList @('--installer-smoke') -PassThru
$windowObserved = $false
$deadline = [DateTimeOffset]::UtcNow.AddSeconds(15)
try {
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $process.Refresh()
        if ($process.HasExited) {
            if ($process.ExitCode -ne 0) { throw "PUBLISHED_APP_CRASH exit=$($process.ExitCode)" }
            break
        }
        if ($process.MainWindowHandle -ne [IntPtr]::Zero) {
            $windowObserved = $true
            break
        }
        Start-Sleep -Milliseconds 250
    }

    $process.Refresh()
    if ($process.HasExited -and $process.ExitCode -ne 0) { throw "PUBLISHED_APP_CRASH exit=$($process.ExitCode)" }
    if (-not $windowObserved) { throw 'PUBLISHED_WPF_WINDOW_NOT_OBSERVED: process did not expose a top-level WPF window within the smoke window.' }

    $db = Join-Path $env:LOCALAPPDATA 'PCC Executive\state\pcc-executive.db'
    if (-not (Test-Path $db)) { throw "PUBLISHED_SQLITE_NOT_INITIALIZED: expected $db" }
    if ((Get-Item $db).Length -le 0) { throw 'PUBLISHED_SQLITE_EMPTY: durable database exists but is empty.' }

    $evidence = [ordered]@{
        State='PASS'; SourceSha=$ExpectedSourceSha; PublishRoot=$root; ProcessId=$process.Id;
        WindowObserved=$windowObserved; DatabasePath=$db; DatabaseBytes=(Get-Item $db).Length;
        ObservedAt=[DateTimeOffset]::UtcNow.ToString('o')
    }
    if (-not [string]::IsNullOrWhiteSpace($EvidencePath)) {
        $parent = Split-Path $EvidencePath -Parent
        if ($parent) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
        $evidence | ConvertTo-Json -Depth 4 | Set-Content $EvidencePath -Encoding UTF8
    }
    Write-Host "PUBLISHED_APP_SMOKE_PASS sourceSha=$ExpectedSourceSha pid=$($process.Id)"
}
finally {
    if (-not $process.HasExited) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        $process.WaitForExit(5000) | Out-Null
    }
}
