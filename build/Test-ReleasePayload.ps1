[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PayloadRoot,
    [string[]]$AllowedRelativePaths = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path $PayloadRoot).Path
$allowed = @{}
foreach ($entry in $AllowedRelativePaths) {
    if (-not [string]::IsNullOrWhiteSpace($entry)) {
        $allowed[$entry.Replace('\\','/').TrimStart('/')] = $true
    }
}

function Is-Allowed([System.IO.FileSystemInfo]$item) {
    $relative = [IO.Path]::GetRelativePath($root, $item.FullName).Replace('\\','/')
    return $allowed.ContainsKey($relative)
}

$forbiddenDirectory = '^(User Data|BrowserProfiles?|ChatGPTProfiles?|auth-state|storage-state|playwright-auth|\.playwright)$'
$forbiddenFileName = '^(Cookies|Cookies-journal|Login Data|Login Data-journal|Web Data|History|Preferences|Secure Preferences)$'
$forbiddenExtension = '^\.(sqlite|sqlite3|db|pdb|cs|csproj|sln|slnx|user)$'
$forbiddenSecretName = '^(\.env(?:\..+)?|.*(?:auth[-_.]?state|storage[-_.]?state|credentials?|tokens?|cookies?)\.(json|ya?ml))$'

$violations = [System.Collections.Generic.List[string]]::new()
foreach ($dir in Get-ChildItem -Path $root -Recurse -Force -Directory -ErrorAction SilentlyContinue) {
    if (-not (Is-Allowed $dir) -and $dir.Name -match $forbiddenDirectory) {
        $violations.Add("forbidden-directory:$([IO.Path]::GetRelativePath($root,$dir.FullName))")
    }
}

foreach ($file in Get-ChildItem -Path $root -Recurse -Force -File -ErrorAction SilentlyContinue) {
    if (Is-Allowed $file) { continue }
    $relative = [IO.Path]::GetRelativePath($root, $file.FullName)
    if ($file.Name -match $forbiddenFileName) { $violations.Add("browser-profile-file:$relative"); continue }
    if ($file.Extension -match $forbiddenExtension) { $violations.Add("forbidden-release-file:$relative"); continue }
    if ($file.Name -match $forbiddenSecretName) { $violations.Add("secret-or-auth-file:$relative"); continue }
    if ($file.Extension -eq '.log') { $violations.Add("developer-log:$relative"); continue }

    if ($file.Length -le 2MB -and $file.Extension -match '^\.(json|ya?ml|xml|config|txt)$') {
        try {
            $text = Get-Content $file.FullName -Raw -ErrorAction Stop
            if ($text -match '(?i)(sk-[A-Za-z0-9_-]{20,}|"(?:access_token|refresh_token|api[_-]?key|authorization|cookie)"\s*:\s*"[^"\s]{8,}")') {
                $violations.Add("credential-pattern:$relative")
            }
        } catch { }
    }
}

if ($violations.Count -gt 0) {
    $message = "RELEASE_PAYLOAD_REJECTED:`n - " + ($violations -join "`n - ")
    throw $message
}

Write-Host "RELEASE_PAYLOAD_VALID root=$root"
