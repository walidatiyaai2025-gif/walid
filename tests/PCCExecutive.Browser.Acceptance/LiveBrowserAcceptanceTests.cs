using PCCExecutive.Browser;
using Xunit;

namespace PCCExecutive.Browser.Acceptance;

public sealed class LiveBrowserAcceptanceTests
{
    [Fact]
    [Trait("Category", "LiveBrowser")]
    public async Task Opt_in_pcc_owned_chrome_launches_manager_and_workers_probes_auth_and_closes_owned_runtimes()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("PCCEXECUTIVE_LIVE_BROWSER"), "1", StringComparison.Ordinal))
            return;

        Assert.True(OperatingSystem.IsWindows(), "Live browser acceptance requires the dedicated Windows runner.");
        var workerCount = ParseWorkerCount(Environment.GetEnvironmentVariable("PCCEXECUTIVE_LIVE_WORKERS"));
        var root = Environment.GetEnvironmentVariable("PCCEXECUTIVE_LIVE_PROFILE_ROOT");
        if (string.IsNullOrWhiteSpace(root))
            root = Path.Combine(Path.GetTempPath(), "PCCExecutive", "LiveAcceptanceProfiles");

        Directory.CreateDirectory(root);
        var registry = new InMemoryBrowserRuntimeRegistry();
        var markers = new FileOwnershipMarkerStore();
        var processes = new SystemProcessInspector();
        var host = new PlaywrightChromeRuntimeHost(root);
        var ownership = new OwnershipProofService(root, markers, processes);
        var sessions = new BrowserSessionController(registry, host, ownership, markers, processes);
        var adapter = new PlaywrightChatGptBrowserAdapter(host);
        var created = new List<BrowserRuntimeRecord>();
        var states = new List<string>();

        try
        {
            var manager = await sessions.CreateAsync(new BrowserSessionRequest(
                "live-acceptance-run", "live-manager-agent", null, "live-manager-bootstrap",
                "live-manager-bootstrap", "https://chatgpt.com/", BrowserVisibility.Hidden, "live-manager"));
            created.Add(manager);
            states.Add(await ProbeAuthAsync(adapter, manager));

            for (var slot = 1; slot <= workerCount; slot++)
            {
                var runtime = await sessions.CreateAsync(new BrowserSessionRequest(
                    "live-acceptance-run", $"live-worker-{slot}-agent", slot.ToString(), $"live-worker-{slot}-bootstrap",
                    $"live-worker-{slot}-bootstrap", "https://chatgpt.com/", BrowserVisibility.Hidden, $"live-worker-{slot}"));
                created.Add(runtime);
                states.Add(await ProbeAuthAsync(adapter, runtime));
            }

            Assert.Equal(workerCount + 1, created.Count);
            Assert.All(created, runtime => Assert.True(runtime.CreatedByPcc));
            Assert.All(created, runtime => Assert.Equal(BrowserVisibility.Hidden, runtime.Visibility));
            Assert.All(states, state => Assert.True(
                state is "AUTHENTICATED_READY" or "LOGIN_REQUIRED" or "CHALLENGE",
                $"Unexpected live auth classification: {state}"));

            var report = new AcceptanceScenarioReport(
                "live-browser-manager-workers",
                Environment.GetEnvironmentVariable("GITHUB_SHA") ?? "live-local",
                adapter.AdapterVersion,
                created.Select(x => x.RuntimeId).ToArray(),
                created.Select(x => x.LogicalAgentId).ToArray(),
                states,
                [],
                [],
                ["privacy:no-conversation-body", "privacy:no-cookie-artifact", "privacy:no-profile-artifact"]);
            var json = AcceptanceArtifactWriter.Serialize(report);
            var output = Environment.GetEnvironmentVariable("PCCEXECUTIVE_ACCEPTANCE_OUTPUT");
            if (!string.IsNullOrWhiteSpace(output))
            {
                Directory.CreateDirectory(output);
                await File.WriteAllTextAsync(Path.Combine(output, "live-browser-acceptance.json"), json);
            }
        }
        finally
        {
            var killed = await sessions.KillAllPccSessionsAsync();
            Assert.Equal(created.Count, killed.KilledRuntimeIds.Count);
            Assert.Empty(killed.SkippedRuntimeReasons);
        }
    }

    private static async Task<string> ProbeAuthAsync(PlaywrightChatGptBrowserAdapter adapter, BrowserRuntimeRecord runtime)
    {
        var expectation = new BrowserDispatchExpectation(
            runtime.ProjectRunId,
            runtime.LogicalAgentId,
            runtime.TaskId ?? "bootstrap",
            runtime.ConversationIdentity ?? "bootstrap",
            "https://chatgpt.com/c/acceptance-unbound");

        ChatGptSemanticSnapshot? last = null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            last = await adapter.InspectAsync(runtime, expectation);
            if (last.Auth.State == AuthState.LoginRequired) return "LOGIN_REQUIRED";
            if (last.Auth.State == AuthState.Challenge) return "CHALLENGE";
            if (last.Auth.State == AuthState.Authenticated && last.Input.State == InputState.Ready) return "AUTHENTICATED_READY";
            await Task.Delay(500);
        }

        throw new InvalidOperationException(
            $"Live ChatGPT page did not reach an accepted auth state. LastAuth={last?.Auth.State}; LastInput={last?.Input.State}; Adapter={adapter.AdapterVersion}");
    }

    private static int ParseWorkerCount(string? value) =>
        int.TryParse(value, out var parsed) && parsed is >= 1 and <= 5 ? parsed : 1;
}
