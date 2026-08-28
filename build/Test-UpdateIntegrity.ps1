[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$temp = Join-Path ([IO.Path]::GetTempPath()) ('pcc-update-integrity-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp -Force | Out-Null
try {
    $package = Join-Path $temp 'PCCExecutive-0.1.0-Setup-x64.exe'
    Set-Content $package 'deterministic-update-test' -Encoding ascii
    $hash = (Get-FileHash $package -Algorithm SHA256).Hash.ToLowerInvariant()
    $manifestPath = Join-Path $temp 'update.manifest.json'
    $base = [ordered]@{
        schemaVersion=2; product='PCC Executive'; repository='walidatiyaai2025-gif/walid'; task='PCCEXECUTIVE-T0001'; version='0.1.0';
        sourceSha='0123456789abcdef0123456789abcdef01234567'; artifactHash="sha256:$hash"; targetArchitecture='win-x64'; runtime='win-x64'; selfContained=$true;
        generatedAt='2026-01-01T00:00:00Z'; workflowRun='selftest'; buildId='selftest.1'; packageIdentity='PCCExecutive/0.1.0/win-x64/0123456789abcdef0123456789abcdef01234567';
        fileName=[IO.Path]::GetFileName($package); applicationFileHash=('sha256:' + ('a'*64)); databaseSchemaTarget='1'; minimumUpgradeVersion='NONE_FRESH_BASELINE';
        signingState='UNSIGNED_DEV'; sbomReference='PCCExecutive-0.1.0-sbom.json'
    }
    $base | ConvertTo-Json -Depth 6 | Set-Content $manifestPath -Encoding UTF8
    $stageRoot = Join-Path $temp 'stage'
    $record = & (Join-Path $repoRoot 'updater/Stage-Update.ps1') -PackagePath $package -ManifestPath $manifestPath -StageRoot $stageRoot
    if (-not (Test-Path $record)) { throw 'Valid update package did not stage.' }
    $staged = Get-Content $record -Raw | ConvertFrom-Json
    if ($staged.State -ne 'VERIFIED_STAGED' -or $staged.AuthenticodeStatus -ne 'NotSigned') { throw 'Unsigned development staging classification mismatch.' }

    foreach ($mutation in @('hash','product','architecture','source','signed-claim')) {
        $copy = ($base | ConvertTo-Json -Depth 8 | ConvertFrom-Json)
        switch ($mutation) {
            'hash' { $copy.artifactHash='sha256:' + ('0'*64) }
            'product' { $copy.product='Wrong Product' }
            'architecture' { $copy.targetArchitecture='win-arm64' }
            'source' { $copy.sourceSha='not-a-sha' }
            'signed-claim' { $copy.signingState='SIGNED' }
        }
        $copy | ConvertTo-Json -Depth 8 | Set-Content $manifestPath -Encoding UTF8
        $rejected = $false
        try { & (Join-Path $repoRoot 'updater/Stage-Update.ps1') -PackagePath $package -ManifestPath $manifestPath -StageRoot (Join-Path $temp "stage-$mutation") | Out-Null } catch { $rejected = $_.Exception.Message -match 'UNVERIFIED_PACKAGE' }
        if (-not $rejected) { throw "Update integrity rejection failed for $mutation." }
    }

    $base | ConvertTo-Json -Depth 6 | Set-Content $manifestPath -Encoding UTF8
    $requireSignedRejected=$false
    try { & (Join-Path $repoRoot 'updater/Stage-Update.ps1') -PackagePath $package -ManifestPath $manifestPath -StageRoot (Join-Path $temp 'signed-required') -RequireSigned | Out-Null } catch { $requireSignedRejected = $_.Exception.Message -match 'UNVERIFIED_PACKAGE' }
    if (-not $requireSignedRejected) { throw 'RequireSigned accepted an unsigned package.' }

    Write-Host 'UPDATE_INTEGRITY_TESTS_PASS'
    $global:LASTEXITCODE = 0
}
finally {
    Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
}
exit 0
