using PCCExecutive.Application;
using Xunit;

namespace PCCExecutive.Application.Tests;

public sealed class AutonomousRuntimeRoutingTests
{
    private readonly AutonomousNextActionRouter _router = new(new GuidedExecutionEvaluator());

    [Theory]
    [InlineData("LOGIN_REQUIRED")]
    [InlineData("CAPTCHA")]
    [InlineData("ACCOUNT_CHALLENGE")]
    public void Human_only_boundary_produces_one_action(string code)
    {
        var decision = _router.Route(State(BrowserRecoveryState.LoginRequired), new("manager", BrowserRecoveryState.LoginRequired, code, "01 Chrome"));

        Assert.Equal(AutonomousRuntimeAction.RequireHumanAttention, decision.Action);
        Assert.NotNull(decision.Attention);
        Assert.False(string.IsNullOrWhiteSpace(decision.Attention!.RequiredAction));
        Assert.False(string.IsNullOrWhiteSpace(decision.Attention.ExactLocation));
    }

    [Fact]
    public void Recoverable_failure_routes_to_automation_without_attention()
    {
        var decision = _router.Route(State(BrowserRecoveryState.RecoveryFailed), new("manager", BrowserRecoveryState.RecoveryFailed, "ECONNREFUSED"));

        Assert.Equal(AutonomousRuntimeAction.RecoverBrowser, decision.Action);
        Assert.False(decision.RequiresHumanAttention);
    }

    [Fact]
    public void Active_recovery_is_singular_wait_not_an_owner_task()
    {
        var decision = _router.Route(State(BrowserRecoveryState.RecoveringRuntime), new("manager", BrowserRecoveryState.RecoveringRuntime, "STALE_ENDPOINT", RecoveryInProgress: true));

        Assert.Equal(AutonomousRuntimeAction.WaitForAutomation, decision.Action);
        Assert.Contains("No operator action", decision.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void Recovery_leases_dedupe_concurrent_and_completed_fingerprints()
    {
        var leases = new RuntimeRecoveryLeaseCoordinator();
        Assert.True(leases.TryAcquire("manager", "endpoint:1", out var first));
        Assert.False(leases.TryAcquire("manager", "endpoint:1", out _));
        first!.Dispose();
        Assert.False(leases.TryAcquire("manager", "endpoint:1", out _));
        Assert.True(leases.TryAcquire("manager", "endpoint:2", out var second));
        second!.Dispose();
    }

    [Fact]
    public void Startup_reconciliation_can_repeat_after_completed_pass_but_never_concurrently()
    {
        var leases = new RuntimeRecoveryLeaseCoordinator();
        const string run = "run-31";
        const string startupFingerprint = "startup:run-31";

        Assert.True(leases.TryAcquire(run, startupFingerprint, out var first));
        Assert.False(leases.TryAcquire(run, startupFingerprint, out _));
        first!.Dispose();

        Assert.True(leases.TryAcquire(run, startupFingerprint, out var second));
        Assert.False(leases.TryAcquire(run, startupFingerprint, out _));
        second!.Dispose();

        Assert.True(leases.TryAcquire(run, startupFingerprint, out var third));
        third!.Dispose();
    }

    [Fact]
    public void Fully_ready_runtime_routes_safe_automatic_resume()
    {
        var decision = _router.Route(State(BrowserRecoveryState.Ready), new("manager", BrowserRecoveryState.Ready, SafeToResume: true));
        Assert.Equal(AutonomousRuntimeAction.ResumeOrchestration, decision.Action);
        Assert.False(decision.RequiresHumanAttention);
    }

    private static GuidedRuntimeState State(BrowserRecoveryState state) => new(
        GatewayBound: true, BrowserProviderSelected: true, BrowserState: state,
        ProjectResolved: true, ProjectIdentityKnown: true, ProjectRunValid: true,
        ManagerRuntimeAvailable: true, ManagerPlanningValid: true, DispatchReady: true);
}
