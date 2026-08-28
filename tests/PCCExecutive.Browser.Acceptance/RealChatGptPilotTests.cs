using PCCExecutive.Browser;
using Xunit;

namespace PCCExecutive.Browser.Acceptance;

public sealed class RealChatGptPilotTests
{
    [Fact]
    [Trait("Category", "LiveBrowser")]
    public async Task Explicit_live_browser_pilot_progresses_manager_and_workers_only_when_fully_opted_in()
    {
        if (!Enabled("PCCEXECUTIVE_LIVE_BROWSER")) return;

        var sourceSha = Environment.GetEnvironmentVariable("GITHUB_SHA") ?? "live-local";
        var submitEnabled = Enabled("PCCEXECUTIVE_LIVE_PILOT_SUBMIT");
        var managerParsed = LiveConversationIdentity.TryParse(Environment.GetEnvironmentVariable("PCCEXECUTIVE_LIVE_MANAGER_URL"), out var managerBinding);
        var worker1Parsed = LiveConversationIdentity.TryParse(Environment.GetEnvironmentVariable("PCCEXECUTIVE_LIVE_WORKER1_URL"), out _);
        var gate = LivePilotGate.Evaluate(true, OperatingSystem.IsWindows(), submitEnabled, managerParsed, worker1Parsed);
        if (gate.State != LivePilotAcceptanceState.Pass)
        {
            await WriteBlockedArtifactAsync(sourceSha, gate.State, gate.Reason).ConfigureAwait(false);
            return;
        }

        var level = ParseLevel(Environment.GetEnvironmentVariable("PCCEXECUTIVE_LIVE_PILOT_LEVEL"));
        var requestedWorkers = ParseRequestedWorkers(Environment.GetEnvironmentVariable("PCCEXECUTIVE_LIVE_WORKERS"), level);
        var workerCount = LivePilotProgression.ResolveWorkerCount(level, requestedWorkers);
        var workerBindings = new List<LiveConversationBinding>();
        for (var slot = 1; slot <= workerCount; slot++)
        {
            if (!LiveConversationIdentity.TryParse(Environment.GetEnvironmentVariable($"PCCEXECUTIVE_LIVE_WORKER{slot}_URL"), out var binding))
            {
                await WriteBlockedArtifactAsync(sourceSha, LivePilotAcceptanceState.BlockedDependency, $"CONTROLLED_WORKER_{slot}_CONVERSATION_URL_REQUIRED", (int)level).ConfigureAwait(false);
                return;
            }
            workerBindings.Add(binding);
        }

        var profileRoot = Environment.GetEnvironmentVariable("PCCEXECUTIVE_LIVE_PROFILE_ROOT");
        if (string.IsNullOrWhiteSpace(profileRoot))
            profileRoot = Path.Combine(Path.GetTempPath(), "PCCExecutive", "LivePilotProfiles", sourceSha[..Math.Min(12, sourceSha.Length)]);
        Directory.CreateDirectory(profileRoot);

        var allowManualLogin = Enabled("PCCEXECUTIVE_LIVE_ALLOW_MANUAL_LOGIN");
        var driver = new LivePilotRuntimeDriver(profileRoot);
        var transitions = new List<string>();
        var timings = new List<long>();
        var failures = new List<string>();
        var evidence = new List<string>();
        var prepared = new List<LivePreparedSession>();
        LivePilotAcceptanceState finalState = LivePilotAcceptanceState.NotExecuted;
        string finalReason = "NOT_EXECUTED";

        try
        {
            var manager = await driver.PrepareSessionAsync(
                "live-manager", "live-pilot-run", "live-manager-agent", null, "live-manager-pilot",
                managerBinding, allowManualLogin, TimeSpan.FromMinutes(10)).ConfigureAwait(false);
            prepared.Add(manager);
            transitions.Add($"manager:{manager.State}:{manager.Reason}");
            if (manager.State != LivePilotAcceptanceState.Pass)
            {
                finalState = manager.State;
                finalReason = manager.Reason;
                failures.Add(manager.Reason);
                return;
            }

            for (var slot = 1; slot <= workerCount; slot++)
            {
                var workerTaskId = $"live-pilot-task-{slot}";
                var worker = await driver.PrepareSessionAsync(
                    $"live-worker-{slot}", "live-pilot-run", $"live-worker-{slot}-agent", slot.ToString(), workerTaskId,
                    workerBindings[slot - 1], allowManualLogin, TimeSpan.FromMinutes(10)).ConfigureAwait(false);
                prepared.Add(worker);
                transitions.Add($"worker-{slot}:{worker.State}:{worker.Reason}");
                if (worker.State != LivePilotAcceptanceState.Pass)
                {
                    finalState = worker.State;
                    finalReason = worker.Reason;
                    failures.Add(worker.Reason);
                    return;
                }
            }

            for (var slot = 1; slot <= workerCount; slot++)
            {
                if (slot > 1) await Task.Delay(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                var taskId = $"live-pilot-task-{slot}";
                var result = await driver.RunWorkerRoundTripAsync(prepared[slot].Runtime, taskId, slot, TimeSpan.FromMinutes(5)).ConfigureAwait(false);
                transitions.AddRange(result.DispatchTransitions.Select(state => $"worker-{slot}:dispatch:{state}"));
                transitions.Add($"worker-{slot}:roundtrip:{result.State}:{result.Reason}");
                timings.Add(result.ElapsedMilliseconds);
                evidence.AddRange(result.EvidenceCodes.Select(code => $"worker-{slot}:{code}"));
                if (result.State != LivePilotAcceptanceState.Pass)
                {
                    finalState = result.State;
                    finalReason = result.Reason;
                    failures.Add(result.Reason);
                    return;
                }
            }

            if (Enabled("PCCEXECUTIVE_LIVE_TEST_WRONG_CHAT") && workerCount >= 1)
            {
                var wrong = await driver.VerifyWrongChatNoSendAsync(prepared[1].Runtime, "live-pilot-task-1", managerBinding, workerBindings[0]).ConfigureAwait(false);
                transitions.Add($"wrong-chat:{wrong.NoSend}:{wrong.Reason}");
                if (!wrong.NoSend)
                {
                    finalState = LivePilotAcceptanceState.Fail;
                    finalReason = "WRONG_CHAT_GUARD_FAILED";
                    failures.Add(finalReason);
                    return;
                }
                evidence.Add("wrong-chat:no-send-proven");
            }

            if (Enabled("PCCEXECUTIVE_LIVE_TEST_UNCERTAIN_SEND") && workerCount >= 1)
            {
                var uncertain = await driver.RunControlledUncertainSendAsync(prepared[1].Runtime, "live-pilot-task-1", 1).ConfigureAwait(false);
                transitions.Add($"uncertain-send:{uncertain.First.State}->{uncertain.Second.State}:{uncertain.Reconciliation.State}");
                if (!uncertain.NoDuplicate)
                {
                    finalState = LivePilotAcceptanceState.Fail;
                    finalReason = "UNCERTAIN_SEND_DUPLICATE_PROTECTION_FAILED";
                    failures.Add(finalReason);
                    return;
                }
                evidence.Add("uncertain-send:no-duplicate-proven");
            }

            if (Enabled("PCCEXECUTIVE_LIVE_TEST_CRASH_RECOVERY") && workerCount >= 1)
            {
                var recovery = await driver.CrashOwnedRuntimeAndRecoverLogicalIdentityAsync(prepared[1].Runtime.RuntimeId).ConfigureAwait(false);
                transitions.Add($"crash-recovery:{recovery.Succeeded}:{recovery.Reason}");
                if (!recovery.Succeeded)
                {
                    finalState = LivePilotAcceptanceState.Fail;
                    finalReason = recovery.Reason;
                    failures.Add(finalReason);
                    return;
                }
                evidence.Add("crash-recovery:logical-identity-preserved");
            }

            finalState = LivePilotAcceptanceState.Pass;
            finalReason = level switch
            {
                LivePilotLevel.Level1 => "LEVEL_1_REAL_MANAGER_AND_WORKER_PILOT_PASS",
                LivePilotLevel.Level2 => "LEVEL_2_MULTI_SESSION_PILOT_PASS",
                LivePilotLevel.Level3 => "LEVEL_3_UP_TO_FIVE_WORKER_PILOT_PASS",
                _ => "LIVE_PILOT_PASS"
            };
            evidence.Add(finalReason);
        }
        finally
        {
            var artifact = await driver.BuildArtifactAsync(
                "real-chatgpt-web-pilot", sourceSha, finalState, (int)level, transitions, timings, failures,
                evidence.Concat(["privacy:no-conversation-transcript", "privacy:no-cookie-artifact", "privacy:no-profile-artifact", $"final:{finalReason}"]).ToArray()).ConfigureAwait(false);
            await WriteArtifactAsync(artifact).ConfigureAwait(false);
            var shutdown = await driver.ShutdownAsync().ConfigureAwait(false);
            if (finalState == LivePilotAcceptanceState.Pass)
                Assert.Empty(shutdown.SkippedRuntimeReasons);
        }

        Assert.Equal(LivePilotAcceptanceState.Pass, finalState);
    }

    private static async Task WriteBlockedArtifactAsync(string sourceSha, LivePilotAcceptanceState state, string reason, int level = 1)
    {
        var artifact = new LivePilotArtifact(
            "real-chatgpt-web-pilot", sourceSha, PlaywrightChatGptBrowserAdapter.CurrentAdapterVersion,
            state, level, [], [state.ToString()], [], [reason], ["privacy:no-conversation-transcript", "privacy:no-cookie-artifact", "privacy:no-profile-artifact"]);
        await WriteArtifactAsync(artifact).ConfigureAwait(false);
    }

    private static async Task WriteArtifactAsync(LivePilotArtifact artifact)
    {
        var json = LivePilotArtifactSanitizer.SerializeOrThrow(artifact);
        var output = Environment.GetEnvironmentVariable("PCCEXECUTIVE_ACCEPTANCE_OUTPUT");
        if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "real-chatgpt-web-pilot.json"), json).ConfigureAwait(false);
    }

    private static LivePilotLevel ParseLevel(string? value) => value switch
    {
        "2" => LivePilotLevel.Level2,
        "3" => LivePilotLevel.Level3,
        _ => LivePilotLevel.Level1
    };

    private static int ParseRequestedWorkers(string? value, LivePilotLevel level)
    {
        if (int.TryParse(value, out var workers) && workers is >= 1 and <= 5) return workers;
        return level switch { LivePilotLevel.Level1 => 1, LivePilotLevel.Level2 => 2, LivePilotLevel.Level3 => 5, _ => 1 };
    }

    private static bool Enabled(string name) => string.Equals(Environment.GetEnvironmentVariable(name), "1", StringComparison.Ordinal);
}
