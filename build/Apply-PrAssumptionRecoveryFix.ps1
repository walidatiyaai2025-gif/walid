[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$gatewayPath = Join-Path $repoRoot 'src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs'
$text = (Get-Content $gatewayPath -Raw).Replace("`r`n", "`n")

$parseAnchor = '        var parsed = new StructuredManagerPlanParser().Parse(semantic.CapturedResponseText);'
$fingerprintAnchor = '        var planFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", parsed.Plan.Tasks.Select(x => x.Task.Fingerprint))))).ToLowerInvariant();'
$resetLine = '        await ResetManagerFormatRepairStateAsync(run, cancellationToken).ConfigureAwait(false);'
$validationAnchor = '        var validation = new ManagerWaveValidator().Validate('
$countMarker = "`n`n        // Count repetition only after a fresh wave is accepted."

$parseIndex = $text.IndexOf($parseAnchor, [StringComparison]::Ordinal)
if ($parseIndex -lt 0) { throw 'PATCH_CONTRACT_MISMATCH: Manager parse anchor not found.' }
$fingerprintIndex = $text.IndexOf($fingerprintAnchor, $parseIndex, [StringComparison]::Ordinal)
if ($fingerprintIndex -lt 0) { throw 'PATCH_CONTRACT_MISMATCH: Manager plan fingerprint anchor not found.' }
$resetIndex = $text.IndexOf($resetLine, $parseIndex, [StringComparison]::Ordinal)
if ($resetIndex -lt 0 -or $resetIndex -gt $fingerprintIndex) { throw 'PATCH_CONTRACT_MISMATCH: bounded repair reset was not found between parse and fingerprint.' }

# Do not clear the bounded repair checkpoint merely because the response parsed as JSON.
# It is cleared only after the fresh live-evidence wave validation succeeds.
$removeLength = $resetLine.Length
if ($resetIndex + $removeLength -lt $text.Length -and $text.Substring($resetIndex + $removeLength, 1) -eq "`n") { $removeLength++ }
$text = $text.Remove($resetIndex, $removeLength)
Write-Host 'PATCHED: preserve bounded Manager repair state through live wave validation'

$validationIndex = $text.IndexOf($validationAnchor, $parseIndex, [StringComparison]::Ordinal)
if ($validationIndex -lt 0) { throw 'PATCH_CONTRACT_MISMATCH: Manager wave validation anchor not found.' }
$ifIndex = $text.IndexOf('        if (!validation.IsValid)', $validationIndex, [StringComparison]::Ordinal)
if ($ifIndex -lt 0) { throw 'PATCH_CONTRACT_MISMATCH: invalid-wave branch not found after ManagerWaveValidator.' }
$markerIndex = $text.IndexOf($countMarker, $ifIndex, [StringComparison]::Ordinal)
if ($markerIndex -lt 0) { throw 'PATCH_CONTRACT_MISMATCH: accepted-wave repetition marker not found.' }

$existing = $text.Substring($ifIndex, $markerIndex - $ifIndex)
if ($existing.Contains('ManagerLiveEvidenceRecoveryPolicy.CanAutoRepair', [StringComparison]::Ordinal)) {
    throw 'PATCH_CONTRACT_MISMATCH: live PR recovery block is already present; build-time patch must remain single-application.'
}

$newValidation = @'
        if (!validation.IsValid)
        {
            if (ManagerLiveEvidenceRecoveryPolicy.CanAutoRepair(validation))
            {
                // The validator is authoritative. Never dispatch a stale plan. Refresh the
                // Manager repair context to the same fresh baseline that rejected this wave,
                // then ask the existing Manager conversation for one bounded corrected plan.
                _managerBaseline = baselineResult.Value;
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

        await ResetManagerFormatRepairStateAsync(run, cancellationToken).ConfigureAwait(false);
'@
$newValidation = $newValidation.Replace('\"', '"')

$text = $text.Substring(0, $ifIndex) + $newValidation + $text.Substring($markerIndex)
Set-Content -Path $gatewayPath -Value $text -Encoding utf8 -NoNewline
Write-Host 'PATCHED: stale/missing PR assumptions trigger one fresh-evidence Manager replan before Loop Guard'
Write-Host 'PR_ASSUMPTION_RECOVERY_FIX_APPLIED'
