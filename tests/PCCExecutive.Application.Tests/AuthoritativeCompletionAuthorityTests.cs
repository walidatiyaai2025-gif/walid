using PCCExecutive.Application;
using PCCExecutive.Domain;
using Xunit;

namespace PCCExecutive.Application.Tests;

public sealed class AuthoritativeCompletionAuthorityTests
{
    private static readonly string Head = new('a', 40);
    private readonly AuthoritativeCompletionAuthority _authority = new();

    [Fact]
    public void Manager_close_at_99_with_stale_evidence_stays_below_100()
    {
        var evidence = GoodEvidence() with { Freshness = EvidenceFreshness.Stale };
        var result = Reconcile(evidence);
        Assert.False(result.IsAuthoritativelyVerified);
        Assert.True(result.VerifiedCompletion.Percent <= 99m);
        Assert.Contains("EVIDENCE_STALE", result.Reasons);
    }

    [Fact]
    public void Manager_close_with_missing_tests_stays_below_100()
    {
        var result = Reconcile(GoodEvidence() with { Tests = AuthoritativeVerificationState.Missing });
        Assert.True(result.VerifiedCompletion.Percent <= 99m);
        Assert.Contains("TEST_EVIDENCE_MISSING", result.Reasons);
    }

    [Fact]
    public void Manager_close_with_failing_ci_stays_below_100()
    {
        var result = Reconcile(GoodEvidence() with { Ci = AuthoritativeVerificationState.Failed });
        Assert.True(result.VerifiedCompletion.Percent <= 99m);
        Assert.Contains("CI_FAILED", result.Reasons);
    }

    [Fact]
    public void Manager_close_with_stale_github_head_stays_below_100()
    {
        var result = Reconcile(GoodEvidence() with { ActualHead = new string('b', 40) });
        Assert.True(result.VerifiedCompletion.Percent <= 99m);
        Assert.Contains("STALE_GITHUB_HEAD", result.Reasons);
    }

    [Fact]
    public void Exact_head_fresh_all_green_evidence_is_the_only_terminal_100_path()
    {
        var result = Reconcile(GoodEvidence());
        Assert.True(result.IsAuthoritativelyVerified);
        Assert.Equal(100m, result.VerifiedCompletion.Percent);
        Assert.Equal(ProjectCompletionMode.VerifiedComplete, result.Mode);
        Assert.Empty(result.Reasons);
    }

    [Fact]
    public void Manager_prose_or_estimate_cannot_set_terminal_100_without_final_verification_request()
    {
        var result = _authority.Reconcile(new ManagerEstimate(100), new VerifiedCompletion(99), false, GoodEvidence(), DateTimeOffset.UtcNow);
        Assert.False(result.IsAuthoritativelyVerified);
        Assert.Equal(99m, result.VerifiedCompletion.Percent);
        Assert.Contains("FINAL_VERIFICATION_NOT_REQUESTED", result.Reasons);
    }

    [Fact]
    public void Old_but_otherwise_green_evidence_is_not_fresh_enough_for_100()
    {
        var now = DateTimeOffset.UtcNow;
        var evidence = GoodEvidence() with { CapturedAt = now.AddHours(-1) };
        var result = _authority.Reconcile(new ManagerEstimate(100), new VerifiedCompletion(99), true, evidence, now, TimeSpan.FromMinutes(10));
        Assert.False(result.IsAuthoritativelyVerified);
        Assert.Contains("EVIDENCE_NOT_FRESH", result.Reasons);
    }

    private AuthoritativeCompletionDecision Reconcile(AuthoritativeCompletionEvidence evidence) =>
        _authority.Reconcile(new ManagerEstimate(100), new VerifiedCompletion(99), true, evidence, DateTimeOffset.UtcNow);

    private static AuthoritativeCompletionEvidence GoodEvidence() => new(
        Head,
        Head,
        Head,
        DateTimeOffset.UtcNow,
        EvidenceFreshness.Current,
        AuthoritativeVerificationState.Passed,
        AuthoritativeVerificationState.Passed,
        true,
        Array.Empty<string>());
}
