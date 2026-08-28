[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$version = (Get-Content (Join-Path $repoRoot 'VERSION') -Raw).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') {
    throw "Invalid VERSION: '$version'."
}

$requiredFiles = @(
    '.github/workflows/windows-ci.yml',
    'build/Build.ps1',
    'build/Package.ps1',
    'build/Publish-Windows.ps1',
    'build/Test-ReleasePayload.ps1',
    'installer/PCCExecutive.iss',
    'updater/update-manifest.schema.json',
    'src/PCCExecutive.Updater/PCCExecutive.Updater.csproj',
    'src/PCCExecutive.Updater/Program.cs',
    'updater/Stage-Update.ps1',
    'updater/Invoke-Upgrade.ps1',
    'tests/installer/Test-Package.ps1',
    'tests/installer/Smoke-FreshInstall.ps1',
    'tests/installer/Smoke-Repair.ps1',
    'tests/installer/Smoke-Upgrade.ps1',
    'tests/installer/Smoke-FailedUpgrade.ps1',
    'tests/installer/Smoke-Uninstall.ps1'
)

foreach ($relative in $requiredFiles) {
    if (-not (Test-Path (Join-Path $repoRoot $relative))) {
        throw "Required release-infrastructure file is missing: $relative"
    }
}

$parseErrors = New-Object 'System.Collections.Generic.List[object]'
Get-ChildItem -Path $repoRoot -Recurse -File -Filter '*.ps1' |
    Where-Object { $_.FullName -notmatch '[\\/](artifacts|bin|obj)[\\/]' } |
    ForEach-Object {
        $tokens = $null
        $errors = $null
        [System.Management.Automation.Language.Parser]::ParseFile($_.FullName, [ref]$tokens, [ref]$errors) | Out-Null
        foreach ($error in $errors) { $parseErrors.Add($error) }
    }

if ($parseErrors.Count -gt 0) {
    $parseErrors | ForEach-Object { Write-Error $_.Message }
    throw 'PowerShell parser errors were found.'
}

Get-Content (Join-Path $repoRoot 'updater\update-manifest.schema.json') -Raw | ConvertFrom-Json | Out-Null

$installerText = Get-Content (Join-Path $repoRoot 'installer\PCCExecutive.iss') -Raw
foreach ($required in @(
    'ArchitecturesAllowed=x64compatible',
    'ArchitecturesInstallIn64BitMode=x64compatible',
    'UsePreviousAppDir=yes',
    'CloseApplications=yes',
    'PCCExecutive-{#MyAppVersion}-Setup-x64',
    'Description: "Launch PCC Executive"'
)) {
    if (-not $installerText.Contains($required)) {
        throw "Installer invariant missing: $required"
    }
}

# Source-tree scanning catches actual credential-shaped values while allowing deterministic tests to
# mention cookie/header names and explicit fake/redacted fixture values. Release payload scanning is
# independently enforced by Test-ReleasePayload.ps1 after publish.
$forbiddenPatterns = @(
    ('(?i)\b' + 'sk-[A-Za-z0-9_-]{20,}\b'),
    ('(?i)__Secure-' + 'next-auth[^=\r\n]{0,80}=\s*(?!REDACTED\b|FAKE\b|TEST\b)[^;\s\r\n]{20,}'),
    ('(?i)Coo' + 'kie:\s*(?!REDACTED\b|FAKE\b|TEST\b)[^<\r\n]{32,}'),
    ('(?i)Author' + 'ization:\s*Bearer\s+(?:eyJ[A-Za-z0-9_-]{16,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}|sk-[A-Za-z0-9_-]{20,})')
)

$textFiles = Get-ChildItem -Path $repoRoot -Recurse -File |
    Where-Object {
        $_.Extension -in @('.ps1','.psm1','.json','.yml','.yaml','.md','.iss','.cs','.csproj') -and
        $_.FullName -notmatch '[\\/](assets|artifacts|bin|obj)[\\/]'
    }

foreach ($file in $textFiles) {
    $text = Get-Content $file.FullName -Raw
    foreach ($pattern in $forbiddenPatterns) {
        if ($text -match $pattern) {
            throw "Potential credential/session material detected in $($file.FullName)."
        }
    }
}

Write-Host "RELEASE_INFRASTRUCTURE_VALID version=$version"
