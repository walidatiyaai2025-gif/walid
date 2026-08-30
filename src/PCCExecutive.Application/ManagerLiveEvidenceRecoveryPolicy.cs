namespace PCCExecutive.Application;

public static class ManagerLiveEvidenceRecoveryPolicy
{
    private static readonly HashSet<string> RecoverableBlockingCodes = new(StringComparer.Ordinal)
    {
        "PR_ASSUMPTION_NOT_FOUND",
        "PR_STATE_CHANGED",
        "PR_ALREADY_MERGED"
    };

    public static bool CanAutoRepair(OrchestrationWaveValidation validation)
    {
        ArgumentNullException.ThrowIfNull(validation);
        if (validation.IsValid) return false;

        var blocking = validation.Findings
            .Where(x => x.Severity == PlanFindingSeverity.Block)
            .ToArray();

        return blocking.Length > 0 && blocking.All(IsRecoverableFinding);
    }

    public static bool IsRecoverableFinding(ManagerPlanFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        return finding.Severity == PlanFindingSeverity.Block && RecoverableBlockingCodes.Contains(finding.Code);
    }
}
