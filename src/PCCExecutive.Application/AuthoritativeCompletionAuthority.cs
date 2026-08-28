using PCCExecutive.Domain;

namespace PCCExecutive.Application;

public enum AuthoritativeVerificationState
{
    Passed,
    Missing,
    Failed
}

public sealed record AuthoritativeCompletionEvidence(
    string? ExpectedHead,
    string? ActualHead,
    string? ChecksHead,
    DateTimeOffset CapturedAt,
    EvidenceFreshness Freshness,
    AuthoritativeVerificationState Ci,
    AuthoritativeVerificationState Tests,
    bool RequiredFamiliesGreen,
    IReadOnlyList<string> Blockers);

public sealed record AuthoritativeCompletionDecision(
    VerifiedCompletion VerifiedCompletion,
    ProjectCompletionMode Mode,
    bool IsAuthoritativelyVerified,
    IReadOnlyList<string> Reasons);

/// <summary>
/// The only policy object authorized to turn terminal closure evidence into 100% verified completion.
/// Manager prose and estimates are explicitly non-authoritative inputs.
/// </summary>
public sealed class AuthoritativeCompletionAuthority
{
    public AuthoritativeCompletionDecision Reconcile(
        ManagerEstimate managerEstimate,
        VerifiedCompletion currentVerified,
        bool finalVerificationRequested,
        AuthoritativeCompletionEvidence? evidence,
        DateTimeOffset now,
        TimeSpan? maximumEvidenceAge = null)
    {
        var reasons = new List<string>();
        var cap = new VerifiedCompletion(Math.Min(99m, currentVerified.Percent));
        var closureRequested = finalVerificationRequested || managerEstimate.Percent >= 99m || currentVerified.Percent >= 99m;

        if (!finalVerificationRequested)
            reasons.Add("FINAL_VERIFICATION_NOT_REQUESTED");
        if (evidence is null)
            reasons.Add("AUTHORITATIVE_EVIDENCE_MISSING");

        if (evidence is not null)
        {
            var maxAge = maximumEvidenceAge ?? TimeSpan.FromMinutes(10);
            if (evidence.Freshness != EvidenceFreshness.Current)
                reasons.Add("EVIDENCE_STALE");
            if (evidence.CapturedAt > now || now - evidence.CapturedAt > maxAge)
                reasons.Add("EVIDENCE_NOT_FRESH");
            if (!IsExactSha(evidence.ExpectedHead) || !IsExactSha(evidence.ActualHead) || !IsExactSha(evidence.ChecksHead))
                reasons.Add("EXACT_HEAD_EVIDENCE_MISSING");
            else if (!string.Equals(evidence.ExpectedHead, evidence.ActualHead, StringComparison.OrdinalIgnoreCase) ||
                     !string.Equals(evidence.ActualHead, evidence.ChecksHead, StringComparison.OrdinalIgnoreCase))
                reasons.Add("STALE_GITHUB_HEAD");

            if (evidence.Ci != AuthoritativeVerificationState.Passed)
                reasons.Add(evidence.Ci == AuthoritativeVerificationState.Missing ? "CI_EVIDENCE_MISSING" : "CI_FAILED");
            if (evidence.Tests != AuthoritativeVerificationState.Passed)
                reasons.Add(evidence.Tests == AuthoritativeVerificationState.Missing ? "TEST_EVIDENCE_MISSING" : "TESTS_FAILED");
            if (!evidence.RequiredFamiliesGreen)
                reasons.Add("REQUIRED_FAMILIES_NOT_GREEN");
            if (evidence.Blockers.Count > 0)
                reasons.Add("LIVE_BLOCKERS_PRESENT");
        }

        if (finalVerificationRequested && evidence is not null && reasons.Count == 0)
            return new(new VerifiedCompletion(100m), ProjectCompletionMode.VerifiedComplete, true, Array.Empty<string>());

        return new(
            cap,
            closureRequested ? ProjectCompletionMode.ClosureMode : ProjectCompletionMode.Active,
            false,
            reasons.Distinct(StringComparer.Ordinal).ToArray());
    }

    public static AuthoritativeCompletionEvidence FromBaseline(ProjectBaselineSnapshot baseline)
    {
        var checks = baseline.Checks;
        var ci = checks is null
            ? AuthoritativeVerificationState.Missing
            : IsGreen(checks.CombinedState) ? AuthoritativeVerificationState.Passed : AuthoritativeVerificationState.Failed;

        var testChecks = checks?.Checks
            .Where(x => x.Name.Contains("test", StringComparison.OrdinalIgnoreCase))
            .ToArray() ?? Array.Empty<GitHubCheckSnapshot>();
        var tests = testChecks.Length == 0
            ? AuthoritativeVerificationState.Missing
            : testChecks.All(x => IsGreen(x.Conclusion) || IsGreen(x.State))
                ? AuthoritativeVerificationState.Passed
                : AuthoritativeVerificationState.Failed;

        var terminalFamilies = baseline.CanonicalTasks.Count > 0 && baseline.CanonicalTasks.All(x => IsTerminalCanonicalState(x.State));
        return new(
            baseline.DefaultHeadSha,
            baseline.DefaultHeadSha,
            checks?.CommitSha,
            baseline.CapturedAt,
            baseline.Freshness,
            ci,
            tests,
            terminalFamilies,
            baseline.KnownBlockers);
    }

    private static bool IsExactSha(string? value) =>
        value is { Length: 40 } && value.All(Uri.IsHexDigit);

    private static bool IsGreen(string? value) =>
        string.Equals(value, "success", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "green", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "passed", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "completed", StringComparison.OrdinalIgnoreCase);

    private static bool IsTerminalCanonicalState(string state) =>
        state.ToUpperInvariant() is "DONE" or "COMPLETE" or "COMPLETED" or "VERIFIED" or "MERGED" or "CLOSED" or "ACCEPTED";
}
