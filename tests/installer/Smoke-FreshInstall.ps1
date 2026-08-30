[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$InstallerPath,
    [Parameter(Mandatory)] [string]$ExpectedVersion,
    [string]$ExpectedSourceSha,
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'PCC Executive Smoke\Fresh'),
    [string]$EvidencePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Remove-Item $InstallRoot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $InstallRoot -Force | Out-Null

$installer = (Resolve-Path $InstallerPath).Path
$installArgs = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/CURRENTUSER',"/DIR=`"$InstallRoot`"")
$install = Start-Process -FilePath $installer -ArgumentList $installArgs -Wait -PassThru
if ($install.ExitCode -ne 0) { throw "Fresh installer failed: exit=$($install.ExitCode)" }

$app = Join-Path $InstallRoot 'PCCExecutive.exe'
$provenancePath = Join-Path $InstallRoot 'build-provenance.json'
if (-not (Test-Path $app)) { throw 'Fresh install did not produce PCCExecutive.exe.' }
if (-not (Test-Path $provenancePath)) { throw 'Fresh install did not produce build-provenance.json.' }

$provenance = Get-Content $provenancePath -Raw | ConvertFrom-Json
if ([string]$provenance.Version -ne $ExpectedVersion) {
    throw "Installed version mismatch. expected=$ExpectedVersion actual=$($provenance.Version)"
}
if (-not [string]::IsNullOrWhiteSpace($ExpectedSourceSha) -and [string]$provenance.SourceSha -ne $ExpectedSourceSha) {
    throw "Installed source SHA mismatch. expected=$ExpectedSourceSha actual=$($provenance.SourceSha)"
}
if ([string]$provenance.TargetArchitecture -ne 'win-x64') {
    throw "Installed architecture mismatch. expected=win-x64 actual=$($provenance.TargetArchitecture)"
}

$startMenuShortcut = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\PCC Executive.lnk'
if (-not (Test-Path $startMenuShortcut)) { throw "Start Menu shortcut missing: $startMenuShortcut" }

$env:PCCEXECUTIVE_SMOKE_MODE = '1'
$process = Start-Process -FilePath $app -ArgumentList @('--installer-smoke') -PassThru
$windowObserved = $false
$deadline = [DateTimeOffset]::UtcNow.AddSeconds(15)
try {
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $process.Refresh()
        if ($process.HasExited) {
            if ($process.ExitCode -ne 0) { throw "Installed application failed to launch: exit=$($process.ExitCode)" }
            break
        }
        if ($process.MainWindowHandle -ne [IntPtr]::Zero) {
            $windowObserved = $true
            break
        }
        Start-Sleep -Milliseconds 250
    }
    $process.Refresh()
    if ($process.HasExited -and $process.ExitCode -ne 0) { throw "Installed application failed to launch: exit=$($process.ExitCode)" }
    if (-not $windowObserved) { throw 'Installed application process started but no WPF top-level window was observed.' }
}
finally {
    if (-not $process.HasExited) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        $process.WaitForExit(5000) | Out-Null
    }
}

$evidence = [ordered]@{
    State='PASS'; Version=$ExpectedVersion; SourceSha=[string]$provenance.SourceSha; Architecture=[string]$provenance.TargetArchitecture;
    InstallRoot=$InstallRoot; StartMenuShortcut=$startMenuShortcut; WindowObserved=$windowObserved;
    ObservedAt=[DateTimeOffset]::UtcNow.ToString('o')
}
if (-not [string]::IsNullOrWhiteSpace($EvidencePath)) {
    $parent = Split-Path $EvidencePath -Parent
    if ($parent) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    $evidence | ConvertTo-Json -Depth 4 | Set-Content $EvidencePath -Encoding UTF8
}
Write-Host "FRESH_INSTALL_SMOKE_PASS version=$ExpectedVersion installRoot=$InstallRoot sourceSha=$($provenance.SourceSha)"
