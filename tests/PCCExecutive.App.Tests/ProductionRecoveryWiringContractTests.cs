using PCCExecutive.Browser;
using System.IO;
using Xunit;

namespace PCCExecutive.App.Tests;

public sealed class ProductionRecoveryWiringContractTests
{
    [Fact]
    public void Startup_orders_begin_reconstruct_browser_reconciliation_rollover_repair_before_auto_resume()
    {
        var source = ReadSource("src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs");

        AssertOrdered(source,
            "startupRecovery.BeginStartupAsync(run.Id)",
            "startupRecovery.ReconstructAsync(run.Id)",
            "gateway.RecoverStartupBrowserStateAsync()",
            "AutonomousConversationRolloverRuntime.Attach(gateway)",
            "gateway.EnsureAutopilotLoop()");
    }

    [Fact]
    public void Startup_composes_browser_recovery_coordinator_and_safe_automatic_resume()
    {
        var source = ReadSource("src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs");
        var method = Slice(source, "private async Task RecoverStartupBrowserStateAsync", "private static PccExecutiveSettings ParseSettings");

        Assert.Contains("new BrowserStartupRecoveryCoordinator(_runtimeRegistry, _sessions)", method, StringComparison.Ordinal);
        Assert.Contains("_nextActionRouter.Route", method, StringComparison.Ordinal);
        Assert.DoesNotContain("DetectOrphansAsync", method, StringComparison.Ordinal);
        AssertOrdered(method,
            "ReconcileAsync(runId, cancellationToken)",
            "identityReconciler.Reconcile(session, runtime)",
            "if (result.StartupMayContinue && identityConverged)",
            "ResumeNewSendsAsync(\"STARTUP_BROWSER_RECONCILIATION:SAFE_AUTO_RESUME\"");
        Assert.Contains("STARTUP_BROWSER_RECONCILIATION:{reason}", method, StringComparison.Ordinal);
    }

    [Fact]
    public void Browser_recovery_telemetry_is_routed_to_runtime_diagnostic_contract()
    {
        var source = ReadSource("src/PCCExecutive.App/Presentation/BrowserRecoveryDiagnosticSink.cs");
        Assert.Contains("IBrowserRecoveryTelemetrySink", source, StringComparison.Ordinal);
        Assert.Contains("RuntimeDiagnosticKind.Recovery", source, StringComparison.Ordinal);
        Assert.Contains("recoveryEvent.CorrelationId", source, StringComparison.Ordinal);
        Assert.Contains("diagnostics.RecordAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Startup_does_not_overwrite_recovered_logical_agent_bindings_with_ready_nulls()
    {
        var source = ReadSource("src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs");
        var method = Slice(source, "private static void PersistLogicalAgents", "private async Task ConnectManagerChromeAsync");

        Assert.Contains("if (store.LoadLogicalAgentAsync(managerAgentId).GetAwaiter().GetResult() is null)", method, StringComparison.Ordinal);
        Assert.Contains("if (store.LoadLogicalAgentAsync(workerId).GetAwaiter().GetResult() is null)", method, StringComparison.Ordinal);
        Assert.Contains("LogicalSessionState.Ready", method, StringComparison.Ordinal);
    }

    [Fact]
    public void Durable_global_rate_limit_and_offline_health_restore_send_gate_before_auto_resume()
    {
        var source = ReadSource("src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs");

        AssertOrdered(source,
            "store.LoadCheckpointAsync($\"runtime-health:{run.Id}\")",
            "_runtimeHealthFault = health.State",
            "_sendGate.Apply(new ResilienceDecision",
            "AutonomousConversationRolloverRuntime.Attach(gateway)",
            "gateway.EnsureAutopilotLoop()");
        Assert.Contains("Enum.TryParse<ChatGptResilienceState>", source, StringComparison.Ordinal);
        Assert.True(Enum.TryParse<ChatGptResilienceState>("RATELIMITED", true, out var rateLimited));
        Assert.Equal(ChatGptResilienceState.RateLimited, rateLimited);
        Assert.True(Enum.TryParse<ChatGptResilienceState>("OFFLINE", true, out var offline));
        Assert.Equal(ChatGptResilienceState.Offline, offline);
    }

    [Fact]
    public void Global_health_restart_requires_fresh_authenticated_healthy_semantic_evidence_before_resume()
    {
        var source = ReadSource("src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs");
        var method = Slice(source, "private async Task<bool> TryResumeAfterFreshSemanticHealthAsync", "private async Task PersistLoopGuardAsync");

        AssertOrdered(method,
            "_browserAdapter.InspectAsync",
            "semantic.Auth.State != AuthState.Authenticated",
            "semantic.Health.State != PageHealth.Healthy",
            "_newSendPause.ResumeNewSendsAsync");
        Assert.Contains("if (runtimes.Length == 0) return false;", method, StringComparison.Ordinal);
    }

    [Fact]
    public void Loop_guard_restart_restores_durable_repetition_state_before_auto_resume()
    {
        var source = ReadSource("src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs");

        AssertOrdered(source,
            "store.LoadCheckpointAsync($\"loop-guard:{run.Id}\")",
            "loop.PlanFingerprints.TakeLast(3)",
            "loop.VerifiedCompletion.TakeLast(3)",
            "_runtimeErrorFingerprint = loop.RuntimeErrorFingerprint",
            "_runtimeErrorCount = loop.RuntimeErrorCount",
            "if (loop.AutoStopped)",
            "AutonomousConversationRolloverRuntime.Attach(gateway)",
            "gateway.EnsureAutopilotLoop()");
    }

    [Fact]
    public void Manager_start_recovers_and_proves_pcc_chrome_before_live_evidence_or_planning()
    {
        var source = ReadSource("src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs");
        var readiness = Slice(source, "private async Task<bool> EnsureManagerChromeReadyAsync", "private async Task StartManagerAsync");
        var start = Slice(source, "private async Task StartManagerAsync", "private string BuildManagerPrompt");

        Assert.Contains("RECOVERING_CHROME", readiness, StringComparison.Ordinal);
        Assert.Contains("ConnectManagerChromeAsync(cancellationToken)", readiness, StringComparison.Ordinal);
        Assert.Contains("_ownership.ProveAsync(runtime", readiness, StringComparison.Ordinal);
        Assert.Contains("CHROME_READY", readiness, StringComparison.Ordinal);
        AssertOrdered(start,
            "EnsureManagerChromeReadyAsync(cancellationToken)",
            "_baseline.BuildAsync");
    }

    [Fact]
    public void Normal_disposal_invokes_safe_shutdown_coordinator()
    {
        var source = ReadSource("src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs");
        var method = Slice(source, "public async ValueTask DisposeAsync()", "private OrchestrationPhase CurrentOrchestrationPhase");

        AssertOrdered(method,
            "_rolloverRuntime.DisposeAsync()",
            "new DurableStartupRecoveryService",
            "new SafeShutdownCoordinator",
            "shutdown.ShutdownAsync");
    }

    [Fact]
    public void Completion_authority_still_caps_manager_closure_below_100_until_independent_fresh_verification()
    {
        var source = ReadSource("src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs");

        Assert.Contains("new VerifiedCompletion(Math.Min(99m", source, StringComparison.Ordinal);
        Assert.Contains("private async Task<bool> RunIndependentFinalVerificationAsync", source, StringComparison.Ordinal);
        Assert.Contains("new VerifiedCompletion(100m)", source, StringComparison.Ordinal);
    }

    private static void AssertOrdered(string source, params string[] tokens)
    {
        var previous = -1;
        foreach (var token in tokens)
        {
            var index = source.IndexOf(token, previous + 1, StringComparison.Ordinal);
            Assert.True(index >= 0, $"Missing production wiring token: {token}");
            Assert.True(index > previous, $"Production wiring token is out of order: {token}");
            previous = index;
        }
    }

    private static string Slice(string source, string startToken, string endToken)
    {
        var start = source.IndexOf(startToken, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing start token: {startToken}");
        var end = source.IndexOf(endToken, start + startToken.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"Missing end token: {endToken}");
        return source[start..end];
    }

    private static string ReadSource(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PCCExecutive.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var path = Path.Combine(directory!.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"Production source not found: {path}");
        return File.ReadAllText(path);
    }
}
