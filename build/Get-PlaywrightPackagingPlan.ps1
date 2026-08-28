[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$BrowserHead,
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [string]$OutputPath = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..')).Path 'artifacts\rehearsal\playwright-packaging.json')
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo=(Resolve-Path $RepositoryRoot).Path
if ($BrowserHead -notmatch '^[0-9a-f]{40}$') { throw 'BrowserHead must be an exact 40-character SHA.' }
$project = (& git -C $repo show "${BrowserHead}:src/PCCExecutive.Browser/PCCExecutive.Browser.csproj") -join "`n"
if ($LASTEXITCODE -ne 0) { throw 'Browser project is unavailable at the requested exact head.' }
$runtimeHostText = (& git -C $repo show "${BrowserHead}:src/PCCExecutive.Browser/PlaywrightChromeRuntimeHost.cs") -join "`n"
if ($LASTEXITCODE -ne 0) { throw 'Browser runtime host is unavailable at the requested exact head.' }
$version = if ($project -match 'Microsoft\.Playwright" Version="([^"]+)"') { $Matches[1] } else { 'UNRESOLVED' }
$systemChrome = $runtimeHostText -match 'ChromeExecutableLocator' -and $runtimeHostText -match 'ConnectOverCDPAsync'
$result=[ordered]@{
    SchemaVersion=1
    BrowserSourceSha=$BrowserHead
    MicrosoftPlaywrightVersion=$version
    Strategy=if($systemChrome){'SYSTEM_CHROME_CDP'}else{'PLAYWRIGHT_BROWSER_RUNTIME_REVIEW_REQUIRED'}
    PlaywrightManagedBrowserInstallRequired= -not $systemChrome
    ChromePrerequisite=$systemChrome
    NuGetDriverAssetsMustRemainInPublish=$true
    PersonalProfileBundlingAllowed=$false
    PccOwnedProfilesAreRuntimeData=$true
    LiveChatGptLoginRequiredForInstallSmoke=$false
    Evidence=if($systemChrome){'Runtime locates installed Google Chrome and attaches with Playwright ConnectOverCDP; no Playwright-managed Chromium download is required by current Worker 3 host.'}else{'Current browser host does not prove system-Chrome CDP strategy.'}
}
$dir=Split-Path -Parent $OutputPath; if($dir){New-Item -ItemType Directory -Path $dir -Force|Out-Null}
$result|ConvertTo-Json -Depth 6|Set-Content $OutputPath -Encoding UTF8
$result|ConvertTo-Json -Depth 6
