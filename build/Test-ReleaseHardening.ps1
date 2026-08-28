[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$temp = Join-Path ([IO.Path]::GetTempPath()) ('pcc-release-hardening-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp -Force | Out-Null
try {
    $payload = Join-Path $temp 'payload'
    New-Item -ItemType Directory -Path $payload -Force | Out-Null
    Set-Content (Join-Path $payload 'PCCExecutive.exe') 'fake-app' -Encoding ascii
    & (Join-Path $PSScriptRoot 'Test-ReleasePayload.ps1') -PayloadRoot $payload

    foreach ($badName in @('Cookies','.env','project.sqlite','developer.log')) {
        $badPath = Join-Path $payload $badName
        Set-Content $badPath 'secret' -Encoding ascii
        $rejected = $false
        try { & (Join-Path $PSScriptRoot 'Test-ReleasePayload.ps1') -PayloadRoot $payload } catch { $rejected = $_.Exception.Message -match 'RELEASE_PAYLOAD_REJECTED' }
        if (-not $rejected) { throw "Secret/profile payload rejection self-test failed for $badName." }
        Remove-Item $badPath -Force
    }

    $installer = Join-Path $temp 'PCCExecutive-0.1.0-Setup-x64.exe'
    Set-Content $installer 'fake-installer' -Encoding ascii
    $app = Join-Path $payload 'PCCExecutive.exe'
    $installerHash = 'sha256:' + (Get-FileHash $installer -Algorithm SHA256).Hash.ToLowerInvariant()
    $appHash = 'sha256:' + (Get-FileHash $app -Algorithm SHA256).Hash.ToLowerInvariant()
    $manifestPath = Join-Path $temp 'manifest.json'
    $manifest = [ordered]@{
        Product='PCC Executive'; Version='0.1.0'; SourceSha='0123456789abcdef0123456789abcdef01234567'; BuildId='selftest'; WorkflowRun='selftest'; Target='win-x64'; Runtime='win-x64'; SelfContained=$true;
        InstallerFile=[IO.Path]::GetFileName($installer); InstallerSha256=$installerHash; ApplicationFileHash=$appHash; GeneratedAt='2026-01-01T00:00:00Z'; DatabaseSchemaTarget='1'; MinimumUpgradeVersion='NONE_FRESH_BASELINE'; SigningState='UNSIGNED_DEV'; SbomReference='sbom.json'
    }
    $manifest | ConvertTo-Json | Set-Content $manifestPath -Encoding UTF8
    & (Join-Path $PSScriptRoot 'Test-ReleaseManifest.ps1') -ManifestPath $manifestPath -InstallerPath $installer -ApplicationPath $app -ExpectedSourceSha $manifest.SourceSha -ExpectedVersion '0.1.0'

    foreach ($mutation in @('hash','product','arch')) {
        $copy = ($manifest | ConvertTo-Json -Depth 8 | ConvertFrom-Json)
        if ($mutation -eq 'hash') { $copy.InstallerSha256='sha256:' + ('0'*64) }
        if ($mutation -eq 'product') { $copy.Product='Wrong Product' }
        if ($mutation -eq 'arch') { $copy.Target='win-arm64' }
        $copy | ConvertTo-Json -Depth 8 | Set-Content $manifestPath -Encoding UTF8
        $failed=$false
        try { & (Join-Path $PSScriptRoot 'Test-ReleaseManifest.ps1') -ManifestPath $manifestPath -InstallerPath $installer -ApplicationPath $app -ExpectedSourceSha $manifest.SourceSha -ExpectedVersion '0.1.0' } catch { $failed=$true }
        if (-not $failed) { throw "Manifest rejection self-test failed for $mutation." }
    }

    $unsigned = Join-Path $temp 'unsigned.ps1'
    Set-Content $unsigned 'Write-Output test' -Encoding ascii
    $devState = (& (Join-Path $PSScriptRoot 'Get-SigningState.ps1') -Files @($unsigned) -Context Dev | Out-String | ConvertFrom-Json).signingState
    if ($devState -ne 'UNSIGNED_DEV') { throw "Unsigned development classification failed: $devState" }
    $ciState = (& (Join-Path $PSScriptRoot 'Get-SigningState.ps1') -Files @($unsigned) -Context CI | Out-String | ConvertFrom-Json).signingState
    if ($ciState -ne 'SIGNING_NOT_CONFIGURED') { throw "CI signing-not-configured classification failed: $ciState" }
    $savedThumbprint = $env:PCCEXECUTIVE_SIGNING_CERT_SHA1
    try {
        $env:PCCEXECUTIVE_SIGNING_CERT_SHA1 = ''
        & (Join-Path $PSScriptRoot 'Sign-Release.ps1') -Files @($unsigned)
        $requiredFailed=$false
        try { & (Join-Path $PSScriptRoot 'Sign-Release.ps1') -Files @($unsigned) -RequireSigned } catch { $requiredFailed=$true }
        if (-not $requiredFailed) { throw 'Signing hook RequireSigned configuration test failed.' }
    } finally { $env:PCCEXECUTIVE_SIGNING_CERT_SHA1 = $savedThumbprint }

    $sbomPath = Join-Path $temp 'sbom.json'
    & (Join-Path $PSScriptRoot 'New-Sbom.ps1') -RepositoryRoot $repoRoot -OutputPath $sbomPath
    $sbom = Get-Content $sbomPath -Raw | ConvertFrom-Json
    $exact = (& git -C $repoRoot rev-parse HEAD).Trim().ToLowerInvariant()
    if ($sbom.sourceSha -ne $exact) { throw 'SBOM exact SHA provenance self-test failed.' }
    if ($sbom.dotnetSdk -notmatch '^10\.') { throw 'SBOM must record .NET 10 SDK.' }

    $upgrade = Get-Content (Join-Path $repoRoot 'release/upgrade-matrix.json') -Raw | ConvertFrom-Json
    $recovery = Get-Content (Join-Path $repoRoot 'release/failure-recovery.json') -Raw | ConvertFrom-Json
    if (@($upgrade.failureScenarios).Count -lt 6) { throw 'Upgrade failure matrix is incomplete.' }
    foreach ($failure in $upgrade.failureScenarios) {
        if ($failure -notin @($recovery.scenarios.failure)) { throw "Failure recovery decision missing for $failure." }
    }
    if ($recovery.rollbackClaimPolicy -ne 'PROVE_BEFORE_SUCCESS') { throw 'Rollback success proof law missing.' }

    $gates = Get-Content (Join-Path $repoRoot 'release/release-gates.json') -Raw | ConvertFrom-Json
    foreach ($required in @('BUILD','UNIT_TESTS','INTEGRATION_TESTS','BROWSER_DETERMINISTIC_TESTS','PERSISTENCE_TESTS','UI_BUILD','PACKAGE_BUILD','PACKAGE_HASH','INSTALL_SMOKE','UPGRADE_SMOKE','DATA_PRESERVATION','UNINSTALL_SMOKE','PROVENANCE','SECURITY_SCAN')) {
        if ($required -notin @($gates.gates.name)) { throw "Missing release gate: $required" }
    }

    $readinessPath = Join-Path $temp 'readiness.json'
    & (Join-Path $PSScriptRoot 'Get-ReleaseReadiness.ps1') -RepositoryRoot $repoRoot -Mode Foundation -ModulesOnly -OutputPath $readinessPath | Out-Null
    $readiness = Get-Content $readinessPath -Raw | ConvertFrom-Json
    if ($readiness.SourceSha -ne $exact) { throw 'Readiness exact SHA provenance self-test failed.' }

    $fakeRepo = Join-Path $temp 'fake-repo'
    New-Item -ItemType Directory -Path (Join-Path $fakeRepo 'release') -Force | Out-Null
    Copy-Item (Join-Path $repoRoot 'release/required-modules.json') (Join-Path $fakeRepo 'release/required-modules.json')
    Copy-Item (Join-Path $repoRoot 'release/release-gates.json') (Join-Path $fakeRepo 'release/release-gates.json')
    Set-Content (Join-Path $fakeRepo 'VERSION') '0.1.0' -Encoding ascii
    $missingFailed=$false
    try { & (Join-Path $PSScriptRoot 'Get-ReleaseReadiness.ps1') -RepositoryRoot $fakeRepo -Mode ProductionCandidate -ModulesOnly -OutputPath (Join-Path $fakeRepo 'readiness.json') | Out-Null } catch { $missingFailed=$_.Exception.Message -match 'BLOCKED_DEPENDENCY' }
    if (-not $missingFailed) { throw 'Missing-module production failure self-test failed.' }

    Write-Host 'RELEASE_HARDENING_TESTS_PASS'
}
finally {
    Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
}
