[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [string]$ConfigPath = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..')).Path 'release\integration-rehearsal.json'),
    [string]$OutputPath = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..')).Path 'artifacts\rehearsal\integration-matrix.json')
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$config = Get-Content (Resolve-Path $ConfigPath) -Raw | ConvertFrom-Json
$repo = (Resolve-Path $RepositoryRoot).Path

function Test-GitPath([string]$sha, [string]$path) {
    if ($sha -notmatch '^[0-9a-f]{40}$') { return $false }
    $items = @(& git -C $repo ls-tree -r --name-only $sha -- $path 2>$null)
    return ($LASTEXITCODE -eq 0 -and $items.Count -gt 0)
}

$currentSha = (& git -C $repo rev-parse HEAD).Trim().ToLowerInvariant()
if ($LASTEXITCODE -ne 0) { throw 'Unable to resolve exact checked-out source SHA.' }
$canonicalSha = [string]$config.canonical.sha
$chainsByOwner = @{}
foreach ($chain in $config.chains) { $chainsByOwner[[string]$chain.owner] = $chain }

$matrix = @()
foreach ($module in $config.expectedModules) {
    $name = [string]$module.name; $path = [string]$module.path; $owner = [string]$module.owner
    $state = 'BLOCKED'; $sourceSha = $null; $reason = ''
    if (Test-GitPath $canonicalSha $path) {
        $state = 'FOUND'; $sourceSha = $canonicalSha; $reason = 'Present on canonical task branch.'
    }
    elseif ($owner -eq 'MULTI') {
        $found = $config.chains | Where-Object { $_.includeInPartialCandidate -and (Test-GitPath ([string]$_.head) $path) } | Select-Object -First 1
        if ($null -ne $found) { $state = 'PENDING_PR'; $sourceSha = [string]$found.head; $reason = 'Present in at least one consumable Worker chain but not canonical.' }
        else { $reason = 'No consumable chain currently provides this path.' }
    }
    else {
        $chain = $chainsByOwner[$owner]
        if ($null -eq $chain) { $state = 'INCOMPATIBLE'; $reason = "No chain definition exists for owner $owner." }
        elseif ([string]$chain.state -eq 'BLOCKED') { $state = 'BLOCKED'; $sourceSha = [string]$chain.head; $reason = [string]$chain.reason }
        elseif (Test-GitPath ([string]$chain.head) $path) { $state = 'PENDING_PR'; $sourceSha = [string]$chain.head; $reason = 'Module exists on the current Worker chain head but is not canonical.' }
        else { $state = 'INCOMPATIBLE'; $sourceSha = [string]$chain.head; $reason = 'Worker chain is marked consumable but expected module path is absent.' }
    }
    $matrix += [pscustomobject]@{ Name=$name; Path=$path; Owner=$owner; State=$state; SourceSha=$sourceSha; Reason=$reason }
}

$blocking = @($matrix | Where-Object { $_.State -in @('BLOCKED','INCOMPATIBLE') })
$result = [ordered]@{
    SchemaVersion = 1
    Task = [string]$config.task
    Version = [string]$config.version
    ControllerSourceSha = $currentSha
    CanonicalSourceSha = $canonicalSha
    Overall = if ($blocking.Count -gt 0) { 'BLOCKED_DEPENDENCY' } else { 'READY_FOR_TEMP_CONVERGENCE' }
    Chains = @($config.chains | ForEach-Object { [ordered]@{ Owner=$_.owner; Name=$_.name; Head=$_.head; State=$_.state; PRs=@($_.prs); DependsOn=@($_.dependsOn); Reason=$_.reason } })
    Modules = $matrix
}
$dir = Split-Path -Parent $OutputPath
if ($dir) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
$result | ConvertTo-Json -Depth 10 | Set-Content $OutputPath -Encoding UTF8
$result | ConvertTo-Json -Depth 10
