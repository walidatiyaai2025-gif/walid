[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Gate,
    [ValidateSet('PASS','FAIL','NOT_APPLICABLE')] [string]$Status = 'PASS',
    [string]$RepositoryRoot,
    [string]$EvidenceRoot,
    [string]$Details = ''
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) { $RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path }
if ([string]::IsNullOrWhiteSpace($EvidenceRoot)) { $EvidenceRoot = Join-Path $RepositoryRoot 'artifacts/release-evidence' }
$config = Get-Content (Join-Path $RepositoryRoot 'release/release-gates.json') -Raw | ConvertFrom-Json
if ($Gate -notin @($config.gates.name)) { throw "Unknown release gate '$Gate'." }
$sha = (& git -C $RepositoryRoot rev-parse HEAD 2>$null).Trim().ToLowerInvariant()
if ($sha -notmatch '^[0-9a-f]{40}$') { throw 'Gate evidence requires exact source SHA.' }
New-Item -ItemType Directory -Path $EvidenceRoot -Force | Out-Null
[ordered]@{ Gate=$Gate; Status=$Status; SourceSha=$sha; GeneratedAt=[DateTimeOffset]::UtcNow.ToString('o'); Details=$Details } |
    ConvertTo-Json -Depth 4 | Set-Content (Join-Path $EvidenceRoot "$Gate.json") -Encoding UTF8
