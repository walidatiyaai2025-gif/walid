namespace PCCExecutive.App.Presentation;

public static class DurableProviderAttentionPolicy
{
    public static string? Classify(bool active, string? state, string? reason)
    {
        if (!active) return null;
        if (reason?.Contains("CHALLENGE", StringComparison.OrdinalIgnoreCase) == true)
            return "CHALLENGE";
        if (string.Equals(state, "LOGIN_REQUIRED", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(state, "LOGINREQUIRED", StringComparison.OrdinalIgnoreCase) ||
            reason?.Contains("LOGIN_REQUIRED", StringComparison.OrdinalIgnoreCase) == true)
            return "LOGIN_REQUIRED";
        return null;
    }
}
