[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string[]]$Files,
    [string]$CertificateThumbprint = $env:PCCEXECUTIVE_SIGNING_CERT_SHA1,
    [string]$TimestampUrl = $env:PCCEXECUTIVE_SIGNING_TIMESTAMP_URL,
    [switch]$RequireSigned
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
    if ($RequireSigned) { throw 'SIGNING_NOT_CONFIGURED: CI signing certificate thumbprint is not configured.' }
    Write-Host 'SIGNING_NOT_CONFIGURED'
    exit 0
}

$signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -File -Filter signtool.exe -ErrorAction SilentlyContinue |
    Where-Object FullName -Match '\\x64\\signtool\.exe$' |
    Sort-Object FullName -Descending |
    Select-Object -First 1
if (-not $signtool) { throw 'SIGNING_NOT_CONFIGURED: signtool.exe was not found.' }

foreach ($file in $Files) {
    if (-not (Test-Path $file)) { throw "Signing input missing: $file" }
    $args = @('sign','/sha1',$CertificateThumbprint,'/fd','sha256')
    if (-not [string]::IsNullOrWhiteSpace($TimestampUrl)) { $args += @('/tr',$TimestampUrl,'/td','sha256') }
    $args += (Resolve-Path $file).Path
    & $signtool.FullName @args
    if ($LASTEXITCODE -ne 0) { throw "SIGNATURE_INVALID: signtool failed for $file" }
    $signature = Get-AuthenticodeSignature $file
    if ($signature.Status -ne 'Valid') { throw "SIGNATURE_INVALID: Authenticode verification failed for $file ($($signature.Status))." }
}
Write-Host 'SIGNED'
