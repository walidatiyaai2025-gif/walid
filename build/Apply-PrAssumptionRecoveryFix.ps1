[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

function Read-NormalizedText([string]$Path) {
    return (Get-Content $Path -Raw).Replace("`r`n", "`n")
}

function Replace-RequiredLiteral {
    param(
        [Parameter(Mandatory)][string]$RelativePath,
        [Parameter(Mandatory)][AllowEmptyString()][string]$Old,
        [Parameter(Mandatory)][AllowEmptyString()][string]$New,
        [Parameter(Mandatory)][string]$Description
    )
    $path = Join-Path $repoRoot $RelativePath
    $text = Read-NormalizedText $path
    $count = ([regex]::Matches($text, [regex]::Escape($Old))).Count
    if ($count -ne 1) { throw "PATCH_CONTRACT_MISMATCH: $Description expected exactly one literal match in $RelativePath, found $count." }
    Set-Content -Path $path -Value $text.Replace($Old, $New) -Encoding utf8 -NoNewline
    Write-Host "PATCHED: $Description"
}

$gateway = 'src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs'

$oldValidation = @'
        if (!validation.IsValid)
            throw new InvalidOperationException($"Manager wave rejected: {string.Join("; ", validation.Findings.Select(x => $"{x.Code}:{x.Message}"))}");
'@

# PowerShell single-quoted here-strings do not need C# quote escaping. Normalize the
# JSON-style escapes above into the exact C# source text before matching.
$oldValidation = $oldValidation.Replace('\"', '"')

$newValidation = @'
        if (!validation.IsValid)
        {
            if (ManagerLiveEvidenceRecoveryPolicy.CanAutoRepair(validation))
            {
                var liveEvidenceRepair = new ManagerPlanParseResult(false, parsed.Plan, validation.Findings);
                if (await TryRepairManagerResponseFormatAsync(run, managerAgentId, runtime, semantic, liveEvidenceRepair, cancellationToken).ConfigureAwait(false))
                {
                    _autopilot = "PLANNING";
                    _latestManagerHandoff = $"RECOVERING_LIVE_ASSUMPTION — Manager referenced PR state contradicted by fresh GitHub evidence ({string.Join("; ", validation.Findings.Where(ManagerLiveEvidenceRecoveryPolicy.IsRecoverableFinding).Select(x => $"{x.Code}:{x.Message}"))}). PCC submitted/reconciled one bounded evidence-correction prompt automatically; no Worker dispatch occurred.";
                    _recovery.Insert(0, new RecoveryEventSummary(DateTimeOffset.UtcNow, "RECOVERING_LIVE_ASSUMPTION", _latestManagerHandoff, true));
                    await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }
            }

            throw new InvalidOperationException($"Manager wave rejected: {string.Join("; ", validation.Findings.Select(x => $"{x.Code}:{x.Message}"))}");
        }
'@
$newValidation = $newValidation.Replace('\"', '"')

Replace-RequiredLiteral -RelativePath $gateway `
    -Old $oldValidation `
    -New $newValidation `
    -Description 'Auto-correct recoverable live PR drift before Loop Guard counts a runtime failure'

Write-Host 'PR_ASSUMPTION_RECOVERY_FIX_APPLIED'
