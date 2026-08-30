namespace PCCExecutive.App.Presentation;

public enum ManagerSendRecoveryAction
{
    None,
    GlobalRateLimitCooldown,
    BrowserAdapterReprobe
}

public static class ManagerSendRecoveryPolicy
{
    public static ManagerSendRecoveryAction Classify(string? errorCode, string? providerEvidence = null)
    {
        var normalized = string.Concat(errorCode, " ", providerEvidence)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal);

        if (normalized.Contains("RATELIMIT", StringComparison.OrdinalIgnoreCase))
            return ManagerSendRecoveryAction.GlobalRateLimitCooldown;
        if (normalized.Contains("BROWSERADAPTERUNCERTAIN", StringComparison.OrdinalIgnoreCase))
            return ManagerSendRecoveryAction.BrowserAdapterReprobe;
        return ManagerSendRecoveryAction.None;
    }
}
