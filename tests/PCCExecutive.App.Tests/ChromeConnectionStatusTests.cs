using PCCExecutive.App.Presentation;
using Xunit;

namespace PCCExecutive.App.Tests;

public sealed class ChromeConnectionStatusTests
{
    [Theory]
    [InlineData("READY")]
    [InlineData("HIDDEN")]
    [InlineData("VISIBLE")]
    [InlineData("ACTIVE")]
    public void Proven_owned_manager_runtime_reports_chrome_connected_without_fabricating_semantic_health(string runtimeState)
    {
        var snapshot = CreateSnapshot([
            new SessionSummary(
                "manager-runtime",
                "Manager",
                "Manager",
                runtimeState,
                SessionVisibility.Hidden,
                "Not bound to a conversation yet",
                DateTimeOffset.UtcNow,
                IsPccOwned: true,
                ProcessId: 1234,
                Health: HealthState.Unknown)
        ]);

        Assert.Equal(HealthState.Unknown, snapshot.GlobalHealth);
        Assert.True(snapshot.ChromeConnectionProven);
        Assert.Equal("CHROME CONNECTED", snapshot.HealthText);
        Assert.Equal("#6EE7B7", snapshot.HealthAccent);
    }

    [Fact]
    public void Unproven_runtime_does_not_claim_chrome_connection()
    {
        var snapshot = CreateSnapshot([
            new SessionSummary(
                "manager-runtime",
                "Manager",
                "Manager",
                "HIDDEN",
                SessionVisibility.Hidden,
                "Not bound to a conversation yet",
                DateTimeOffset.UtcNow,
                IsPccOwned: false,
                ProcessId: 1234,
                Health: HealthState.Unknown)
        ]);

        Assert.False(snapshot.ChromeConnectionProven);
        Assert.Equal("UNKNOWN", snapshot.HealthText);
        Assert.Equal("#8FA3B8", snapshot.HealthAccent);
    }

    [Fact]
    public void Recovering_owned_runtime_remains_recovering_instead_of_connected()
    {
        var snapshot = CreateSnapshot([
            new SessionSummary(
                "manager-runtime",
                "Manager",
                "Manager",
                "RECOVERING",
                SessionVisibility.Hidden,
                "Not bound to a conversation yet",
                DateTimeOffset.UtcNow,
                IsPccOwned: true,
                ProcessId: 1234,
                Health: HealthState.Recovering)
        ], HealthState.Recovering);

        Assert.False(snapshot.ChromeConnectionProven);
        Assert.Equal("RECOVERING", snapshot.HealthText);
    }

    private static RuntimeSnapshot CreateSnapshot(
        IReadOnlyList<SessionSummary> sessions,
        HealthState globalHealth = HealthState.Unknown) => new(
            GatewayBound: true,
            HasActiveRun: true,
            RuntimeStatus: "Integrated runtime",
            GlobalHealth: globalHealth,
            AutopilotState: "READY",
            CurrentWave: "Manager planning",
            VerifiedCompletion: 0,
            ManagerEstimate: 0,
            CompletionMode: CompletionMode.Running,
            ActiveWorkers: 0,
            P0Count: 0,
            P1Count: 0,
            BlockerCount: 0,
            LoopGuardState: "NORMAL",
            LatestManagerHandoff: "Select a project, connect Chrome, then start Manager.",
            CurrentExecutionFlow: "Project → Manager plan → validate → staged Workers → reconcile → Manager review",
            ApiConfigured: false,
            ProviderMode: ProviderMode.BrowserWeb,
            DispatchSettings: DispatchSettingsSummary.ProductDefaults,
            Update: new UpdateSummary("0.1.0", null, "Release hardening integrated", "Durable data path active", "Schema v1", "Updater rollback contract integrated", false),
            Projects: Array.Empty<ProjectSummary>(),
            Sessions: sessions,
            Workers: Array.Empty<WorkerSummary>(),
            Tasks: Array.Empty<TaskSummary>(),
            EvidenceGates: Array.Empty<EvidenceGateSummary>(),
            AttentionItems: Array.Empty<AttentionSummary>(),
            RecoveryEvents: Array.Empty<RecoveryEventSummary>());
}
