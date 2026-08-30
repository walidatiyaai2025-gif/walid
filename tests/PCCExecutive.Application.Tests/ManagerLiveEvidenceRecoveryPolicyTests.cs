using PCCExecutive.Application;
using Xunit;

namespace PCCExecutive.Application.Tests;

public sealed class ManagerLiveEvidenceRecoveryPolicyTests
{
    [Theory]
    [InlineData("PR_ASSUMPTION_NOT_FOUND")]
    [InlineData("PR_STATE_CHANGED")]
    [InlineData("PR_ALREADY_MERGED")]
    public void CanAutoRepair_AllowsOnlyLivePrDrift(string code)
    {
        var validation = new OrchestrationWaveValidation(
            false,
            false,
            [new ManagerPlanFinding(code, "live evidence changed", PlanFindingSeverity.Block)]);

        Assert.True(ManagerLiveEvidenceRecoveryPolicy.CanAutoRepair(validation));
    }

    [Fact]
    public void CanAutoRepair_RejectsMixedUnsafeBlockingFindings()
    {
        var validation = new OrchestrationWaveValidation(
            false,
            false,
            [
                new ManagerPlanFinding("PR_ASSUMPTION_NOT_FOUND", "missing PR", PlanFindingSeverity.Block),
                new ManagerPlanFinding("WRONG_REPOSITORY", "wrong repository", PlanFindingSeverity.Block)
            ]);

        Assert.False(ManagerLiveEvidenceRecoveryPolicy.CanAutoRepair(validation));
    }

    [Fact]
    public void CanAutoRepair_DoesNotRunForValidWave()
    {
        var validation = new OrchestrationWaveValidation(
            true,
            false,
            [new ManagerPlanFinding("PR_ASSUMPTION_NOT_FOUND", "ignored", PlanFindingSeverity.Info)]);

        Assert.False(ManagerLiveEvidenceRecoveryPolicy.CanAutoRepair(validation));
    }
}
