using PCCExecutive.Application;
using Xunit;

namespace PCCExecutive.Application.Tests;

public sealed class GuidedExecutionTests
{
    private readonly GuidedExecutionEvaluator _evaluator = new();

    [Fact]
    public void Fresh_runtime_points_to_Chrome_and_blocks_Manager()
    {
        var state = State(browser: BrowserRecoveryState.Unknown);
        var result = _evaluator.Evaluate(state);
        Assert.Equal(GuidedStepState.Current, result[GuidedStepId.Chrome].State);
        Assert.Equal(GuidedStepState.Blocked, result[GuidedStepId.Manager].State);
        Assert.Contains("01 Chrome", result.NextAction.Instruction);
    }

    [Fact]
    public void Ready_Chrome_advances_to_Project()
    {
        var result = _evaluator.Evaluate(State(browser: BrowserRecoveryState.Ready));
        Assert.Equal(GuidedStepState.Completed, result[GuidedStepId.Chrome].State);
        Assert.Equal(GuidedStepState.Current, result[GuidedStepId.Project].State);
        Assert.Equal(GuidedStepId.Project, result.NextAction.Step);
    }

    [Fact]
    public void Stale_endpoint_preserves_Project_truth_but_blocks_Manager_during_recovery()
    {
        var result = _evaluator.Evaluate(State(BrowserRecoveryState.DegradedEndpointStale, project: true));
        Assert.Equal(GuidedStepState.Recovering, result[GuidedStepId.Chrome].State);
        Assert.Equal(GuidedStepState.Completed, result[GuidedStepId.Project].State);
        Assert.Equal(GuidedStepState.Blocked, result[GuidedStepId.Manager].State);
        Assert.Equal(GuidedActionKind.Automatic, result.NextAction.Kind);
    }

    [Fact]
    public void Navigation_guard_returns_exact_missing_prerequisite()
    {
        var guard = new GuidedNavigationGuard(_evaluator);
        var result = guard.Evaluate(State(BrowserRecoveryState.Ready), GuidedStepId.Manager);
        Assert.False(result.Allowed);
        Assert.Equal(GuidedStepId.Project, result.MissingPrerequisite!.Step);
        Assert.Equal("Open Project", result.MissingPrerequisite.RequiredControl);
    }

    private static GuidedRuntimeState State(BrowserRecoveryState browser, bool project = false) => new(
        GatewayBound: true,
        BrowserProviderSelected: true,
        BrowserState: browser,
        ProjectResolved: project,
        ProjectIdentityKnown: project,
        ProjectRunValid: project,
        ManagerRuntimeAvailable: false,
        ManagerPlanningValid: false,
        DispatchReady: false);
}
