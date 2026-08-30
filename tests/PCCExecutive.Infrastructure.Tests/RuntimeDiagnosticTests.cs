using System.Text.Json;
using PCCExecutive.Application;
using PCCExecutive.Infrastructure;
using Xunit;

namespace PCCExecutive.Infrastructure.Tests;

public sealed class RuntimeDiagnosticTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pcc-runtime-diagnostic-tests", Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(_directory, "diagnostics.db");

    [Fact]
    public async Task CorrelatedRecoveryChainSurvivesRestart()
    {
        var store = new SqliteRuntimeDiagnosticStore(Database, new(100, TimeSpan.FromDays(7), 1));
        var collector = new RuntimeDiagnosticCollector(store, store);
        var correlation = collector.BeginCorrelation();
        foreach (var kind in new[] { RuntimeDiagnosticKind.UserAction, RuntimeDiagnosticKind.GuardDecision, RuntimeDiagnosticKind.Command, RuntimeDiagnosticKind.Recovery, RuntimeDiagnosticKind.StateTransition })
            await collector.RecordAsync(collector.Create(kind, "CHAIN", kind.ToString(), correlation));

        var reopened = new SqliteRuntimeDiagnosticStore(Database);
        var events = await reopened.ReadRecentAsync(20);
        Assert.Equal(5, events.Count);
        Assert.All(events, e => Assert.Equal(correlation, e.Event.CorrelationId));
        Assert.True(events.Select(e => e.Sequence).Distinct().Count() == 5);
    }

    [Fact]
    public async Task RetentionPrunesByCountWithoutBreakingNewestSequence()
    {
        var store = new SqliteRuntimeDiagnosticStore(Database, new(3, TimeSpan.FromDays(30), 1));
        var collector = new RuntimeDiagnosticCollector(store, store);
        for (var i = 0; i < 8; i++)
            await collector.RecordAsync(collector.Create(RuntimeDiagnosticKind.Navigation, $"NAV_{i}", $"navigation {i}"));

        var events = await store.ReadRecentAsync(100);
        Assert.Equal(3, events.Count);
        Assert.Equal(new[] { "NAV_7", "NAV_6", "NAV_5" }, events.Select(e => e.Event.ReasonCode));
    }

    [Theory]
    [InlineData("Authorization: Bearer abc.def.ghi")]
    [InlineData("Cookie=session=super-secret")]
    [InlineData("https://x.test/?access_token=secret-value&ok=yes")]
    [InlineData("password=hunter2")]
    public void RedactorRemovesKnownSecretPatterns(string input)
    {
        var result = new DiagnosticRedactor().Redact(input);
        Assert.Contains("[REDACTED]", result);
        Assert.DoesNotContain("hunter2", result);
        Assert.DoesNotContain("secret-value", result);
        Assert.DoesNotContain("super-secret", result);
    }

    [Fact]
    public async Task ExportIsBoundedAndRedactsDetailPayloads()
    {
        var memory = new InMemoryRuntimeDiagnosticStore(20);
        var collector = new RuntimeDiagnosticCollector(memory, memory);
        for (var i = 0; i < 12; i++)
            await collector.RecordAsync(collector.Create(RuntimeDiagnosticKind.Command, "SEND", $"send {i}", details: [new("authorization", "Bearer top-secret-token")]));
        var state = new StaticStateSource();
        var json = await new RuntimeDiagnosticSnapshotService(memory, state).CreateJsonAsync("1.2.3", "sha-123", 5);
        using var parsed = JsonDocument.Parse(json);
        Assert.Equal(5, parsed.RootElement.GetProperty("RecentEvents").GetArrayLength());
        Assert.DoesNotContain("top-secret-token", json);
        Assert.Contains("[REDACTED]", json);
        Assert.Contains("pcc-runtime-diagnostic/v1", json);
    }

    [Fact]
    public async Task BrowserConnectionRefusalRetainsClassificationAndReason()
    {
        var memory = new InMemoryRuntimeDiagnosticStore();
        var collector = new RuntimeDiagnosticCollector(memory, memory);
        var correlation = collector.BeginCorrelation();
        await collector.RecordAsync(collector.Create(RuntimeDiagnosticKind.Exception, "DEVTOOLS_CONNECTION_REFUSED", "DevTools endpoint refused the connection.", correlation, exceptionClassification: "ECONNREFUSED"));

        var diagnostic = Assert.Single(await collector.ReadRecentAsync(5));
        Assert.Equal("ECONNREFUSED", diagnostic.ExceptionClassification);
        Assert.Equal("DEVTOOLS_CONNECTION_REFUSED", diagnostic.Event.ReasonCode);
        Assert.Equal(correlation, diagnostic.Event.CorrelationId);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private sealed class StaticStateSource : IRuntimeInspectorStateSource
    {
        public Task<RuntimeInspectorState> CaptureAsync(CancellationToken cancellationToken = default) => Task.FromResult(new RuntimeInspectorState(
            "run-1", "BrowserWeb", "Ready", "Ready", "2 active", 3, "RUNNING",
            new GuidedNextAction(GuidedStepId.Orchestration, GuidedActionKind.Automatic, "CONTINUE", "Continue automatically"),
            [new(GuidedStepId.Chrome, "Chrome ready", true, "READY", true, false)],
            [new("GPTDeskTop", "runtime-1", "Manager", 42, "healthy", "owned", true)]));
    }
}
