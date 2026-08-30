namespace PCCExecutive.Application;

public enum PrePlanAutoRecoveryMode { EvidenceRefresh, ExistingManagerResponse }

public static class PrePlanAutoRecoveryPolicy
{
    public static PrePlanAutoRecoveryMode Classify(string? runtimeErrorFingerprint)
    {
        if (string.IsNullOrWhiteSpace(runtimeErrorFingerprint)) return PrePlanAutoRecoveryMode.EvidenceRefresh;
        return runtimeErrorFingerprint.Contains("MANAGER_PLAN_NOT_STRUCTURED", StringComparison.OrdinalIgnoreCase) ||
               runtimeErrorFingerprint.Contains("Manager response rejected", StringComparison.OrdinalIgnoreCase)
            ? PrePlanAutoRecoveryMode.ExistingManagerResponse
            : PrePlanAutoRecoveryMode.EvidenceRefresh;
    }
}
