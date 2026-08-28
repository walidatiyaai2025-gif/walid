[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$InstallRoot,
    [string]$DataRoot = (Join-Path $env:LOCALAPPDATA 'PCC Executive'),
    [string]$EvidencePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$app = Join-Path $InstallRoot 'PCCExecutive.exe'
if (-not (Test-Path $app)) { throw 'PERSISTENCE_SMOKE_APP_MISSING.' }
$db = Join-Path $DataRoot 'state\pcc-executive.db'

function Invoke-SmokeRun([string]$phase) {
    $run = Start-Process -FilePath $app -ArgumentList @('--smoke-test') -Wait -PassThru
    if ($run.ExitCode -ne 0) { throw "PERSISTENCE_SMOKE_APP_FAILED phase=$phase exit=$($run.ExitCode)" }
    if (-not (Test-Path $db)) { throw "PERSISTENCE_SMOKE_DB_MISSING phase=$phase path=$db" }
    $item = Get-Item $db
    if ($item.Length -le 0) { throw "PERSISTENCE_SMOKE_DB_EMPTY phase=$phase" }
    return [ordered]@{Phase=$phase;DatabaseBytes=$item.Length;CreationTimeUtc=$item.CreationTimeUtc.ToString('o');LastWriteTimeUtc=$item.LastWriteTimeUtc.ToString('o')}
}

$first = Invoke-SmokeRun 'first-open'
$creation = $first.CreationTimeUtc
Start-Sleep -Milliseconds 300
$second = Invoke-SmokeRun 'reopen'
if ($second.CreationTimeUtc -ne $creation) {
    throw "PERSISTENCE_SMOKE_DB_REPLACED first=$creation second=$($second.CreationTimeUtc)"
}

$evidence = [ordered]@{
    State='PASS'; DatabasePath=$db; First=$first; Reopen=$second;
    Proof='The same durable SQLite file survived process close and a second integrated startup smoke.';
    ObservedAt=[DateTimeOffset]::UtcNow.ToString('o')
}
if (-not [string]::IsNullOrWhiteSpace($EvidencePath)) {
    $parent = Split-Path $EvidencePath -Parent
    if ($parent) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    $evidence | ConvertTo-Json -Depth 6 | Set-Content $EvidencePath -Encoding UTF8
}
Write-Host "PERSISTENCE_REOPEN_SMOKE_PASS db=$db bytes=$($second.DatabaseBytes)"
