[CmdletBinding()]param()
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$repo=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path;$config=Get-Content (Join-Path $repo 'release\integration-rehearsal.json') -Raw|ConvertFrom-Json
if($config.task -ne 'PCCEXECUTIVE-T0001' -or $config.version -ne '0.1.0'){throw 'Integration rehearsal identity mismatch.'}
if(@($config.chains).Count -ne 5){throw 'All five Worker chains must be represented.'}
foreach($chain in $config.chains){if([string]$chain.head -notmatch '^[0-9a-f]{40}$'){throw "Invalid exact head for $($chain.owner)."};if($chain.state -notin @('FOUND','PENDING_PR','BLOCKED','INCOMPATIBLE')){throw "Invalid chain state for $($chain.owner)."}}
$temp=Join-Path ([IO.Path]::GetTempPath()) ('pcc-rehearsal-test-'+[Guid]::NewGuid().ToString('N'));New-Item -ItemType Directory -Path $temp -Force|Out-Null
try{
    $matrixPath=Join-Path $temp 'matrix.json';& (Join-Path $PSScriptRoot 'Get-IntegrationMatrix.ps1') -OutputPath $matrixPath|Out-Null;$matrix=Get-Content $matrixPath -Raw|ConvertFrom-Json
    $expected=@{DOMAIN='FOUND';APPLICATION='FOUND';PCC='FOUND';GITHUB='FOUND';PERSISTENCE='BLOCKED';BROWSER='FOUND';UI='FOUND';UPDATER='FOUND';INSTALLER='FOUND';TESTS='FOUND'}
    foreach($name in $expected.Keys){$actual=($matrix.Modules|Where-Object Name -eq $name|Select-Object -First 1).State;if($actual -ne $expected[$name]){throw "Integration matrix mismatch for $name expected=$($expected[$name]) actual=$actual"}}
    if($matrix.Overall -ne 'BLOCKED_DEPENDENCY'){throw 'Current rehearsal must remain BLOCKED_DEPENDENCY until Worker 2 persistence lands.'}
    $w1=$config.chains|Where-Object owner -eq 'WORKER_1';if($w1.state -ne 'PENDING_PR' -or $w1.includeInPartialCandidate){throw 'Current Worker 1 follow-on must be validated independently and excluded from accepted combined candidate until routed.'}
    $browser=($config.chains|Where-Object owner -eq 'WORKER_3').head;$planPath=Join-Path $temp 'playwright.json';& (Join-Path $PSScriptRoot 'Get-PlaywrightPackagingPlan.ps1') -BrowserHead $browser -OutputPath $planPath|Out-Null;$plan=Get-Content $planPath -Raw|ConvertFrom-Json
    if($plan.Strategy -ne 'SYSTEM_CHROME_CDP' -or $plan.PlaywrightManagedBrowserInstallRequired){throw 'Playwright packaging strategy does not match current Worker 3 implementation.'}
    Write-Host 'INTEGRATED_REHEARSAL_CONTRACT_TESTS_PASS';$global:LASTEXITCODE=0
}finally{Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue}
exit 0
