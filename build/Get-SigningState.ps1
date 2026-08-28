[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string[]]$Files,
    [ValidateSet('Dev','CI','ReleaseCandidate')]
    [string]$Context = 'Dev',
    [switch]$RequireSigned,
    [string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$items = @()
$aggregate = 'SIGNED'
foreach ($file in $Files) {
    if (-not (Test-Path $file)) { throw "Signing-state input missing: $file" }
    $signature = Get-AuthenticodeSignature -FilePath $file
    $state = if ($signature.Status -eq 'Valid') {
        'SIGNED'
    } elseif ($signature.Status -eq 'NotSigned') {
        if ($Context -eq 'Dev') { 'UNSIGNED_DEV' } else { 'SIGNING_NOT_CONFIGURED' }
    } else {
        'SIGNATURE_INVALID'
    }
    if ($state -eq 'SIGNATURE_INVALID') { $aggregate = 'SIGNATURE_INVALID' }
    elseif ($state -eq 'SIGNING_NOT_CONFIGURED' -and $aggregate -ne 'SIGNATURE_INVALID') { $aggregate = 'SIGNING_NOT_CONFIGURED' }
    elseif ($state -eq 'UNSIGNED_DEV' -and $aggregate -eq 'SIGNED') { $aggregate = 'UNSIGNED_DEV' }
    $items += [pscustomobject]@{ file=(Resolve-Path $file).Path; state=$state; authenticodeStatus=[string]$signature.Status; signer=if($signature.SignerCertificate){$signature.SignerCertificate.Subject}else{$null} }
}

$result = [ordered]@{ signingState=$aggregate; files=$items }
if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    New-Item -ItemType Directory -Path (Split-Path $OutputPath -Parent) -Force | Out-Null
    $result | ConvertTo-Json -Depth 6 | Set-Content $OutputPath -Encoding UTF8
}
$result | ConvertTo-Json -Depth 6
if ($RequireSigned -and $aggregate -ne 'SIGNED') { throw "Release requires SIGNED artifacts; current state is $aggregate." }
