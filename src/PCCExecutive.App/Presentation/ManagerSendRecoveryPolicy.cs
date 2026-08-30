namespace PCCExecutive.App.Presentation;

public enum ManagerSendRecoveryAction
{
    None,
    GlobalRateLimitCooldown
}

public static class ManagerSendRecoveryPolicy
{
    public static ManagerSendRecoveryAction Classify(string? errorCode, string? providerEvidence = null)
    {
        var normalized = string.Concat(errorCode, " ", providerEvidence)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal);

        return normalized.Contains("RATELIMIT", StringComparison.OrdinalIgnoreCase)
            ? ManagerSendRecoveryAction.GlobalRateLimitCooldown
            : ManagerSendRecoveryAction.None;
    }
}
