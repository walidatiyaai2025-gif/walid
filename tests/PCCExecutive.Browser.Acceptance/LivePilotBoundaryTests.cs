using PCCExecutive.Browser;
using Xunit;

namespace PCCExecutive.Browser.Acceptance;

public sealed class LivePilotBoundaryTests
{
    [Fact]
    public void Manual_login_boundary_blocks_new_sends_and_requests_foreground()
    {
        var decision = new LiveLoginBoundary().Evaluate(Snapshot(auth: AuthState.LoginRequired, input: InputState.Unknown, health: PageHealth.Unknown));
        Assert.Equal(LivePilotAcceptanceState.BlockedLogin, decision.State);
        Assert.True(decision.BringToFront);
        Assert.True(decision.PauseNewSends);
    }

    [Fact]
    public void Authenticated_ready_is_recognized()
    {
        var decision = new LiveLoginBoundary().Evaluate(Snapshot());
        Assert.Equal(LivePilotAcceptanceState.Pass, decision.State);
        Assert.False(decision.PauseNewSends);
    }

    [Fact]
    public void Challenge_is_detection_only_and_blocks()
    {
        var decision = new LiveLoginBoundary().Evaluate(Snapshot(auth: AuthState.Challenge, input: InputState.Unknown, health: PageHealth.Unknown));
        Assert.Equal(LivePilotAcceptanceState.BlockedChallenge, decision.State);
        Assert.True(decision.PauseNewSends);
    }

    [Fact]
    public void Unknown_semantics_fail_safe()
    {
        var decision = new LiveLoginBoundary().Evaluate(Snapshot(input: InputState.Unknown));
        Assert.Equal(LivePilotAcceptanceState.Fail, decision.State);
        Assert.Equal("BROWSER_ADAPTER_UNCERTAIN", decision.Reason);
    }

    [Fact]
    public void Controlled_conversation_url_parser_accepts_chatgpt_conversation()
    {
        Assert.True(LiveConversationIdentity.TryParse("https://chatgpt.com/c/abcDEF_123456", out var binding));
        Assert.Equal("abcDEF_123456", binding.ConversationId);
        Assert.Equal("https://chatgpt.com/c/abcDEF_123456", binding.CanonicalUrl);
    }

    [Fact]
    public void Controlled_conversation_url_parser_rejects_non_chatgpt_and_homepage()
    {
        Assert.False(LiveConversationIdentity.TryParse("https://example.com/c/abcDEF_123456", out _));
        Assert.False(LiveConversationIdentity.TryParse("https://chatgpt.com/", out _));
    }

    [Fact]
    public void Manager_worker_monitor_evidence_preserves_separate_logical_identity()
    {
        var manager = Runtime("manager", "manager-agent", null, "M-C01");
        var worker = Runtime("worker-1", "worker-1-agent", "1", "W1-C01");
        var managerEvidence = LiveSessionMonitorEvidenceFactory.Create(manager, Snapshot());
        var workerEvidence = LiveSessionMonitorEvidenceFactory.Create(worker, Snapshot());
        Assert.NotEqual(managerEvidence.LogicalAgentId, workerEvidence.LogicalAgentId);
        Assert.Null(managerEvidence.WorkerSlot);
        Assert.Equal("1", workerEvidence.WorkerSlot);
        Assert.True(managerEvidence.OwnedByPcc);
        Assert.True(workerEvidence.OwnedByPcc);
    }

    [Fact]
    public void Harmless_live_prompt_contains_only_machine_readable_acknowledgement_request()
    {
        var prompt = LivePilotPrompt.Create("task-safe", 1);
        Assert.Contains("TASK_ID: task-safe", prompt);
        Assert.Contains("WORKER_SLOT: 1", prompt);
        Assert.Contains("STATUS: ACK", prompt);
        Assert.Contains("NON_DESTRUCTIVE_MARKER: PCC_EXECUTIVE_LIVE_PILOT", prompt);
    }

    [Fact]
    public void Response_association_requires_exact_task_and_worker()
    {
        const string response = "TASK_ID: task-safe\nWORKER_SLOT: 1\nSTATUS: ACK\nNON_DESTRUCTIVE_MARKER: PCC_EXECUTIVE_LIVE_PILOT";
        Assert.True(LivePilotResponseAssociation.Validate(response, "task-safe", 1).Matches);
        Assert.False(LivePilotResponseAssociation.Validate(response, "task-safe", 2).Matches);
    }

    [Fact]
    public void Completion_requires_stable_multi_signal_response()
    {
        var snapshot = Snapshot(generation: GenerationState.Complete, completeness: ResponseCompleteness.Complete, response: "stable", generationEvidence: new[] { "response-actions:visible" });
        Assert.False(LiveResponseCompletionGate.Evaluate(snapshot, null).Complete);
        Assert.True(LiveResponseCompletionGate.Evaluate(snapshot, "stable").Complete);
    }

    [Fact]
    public void Partial_response_never_completes()
    {
        var snapshot = Snapshot(generation: GenerationState.Complete, completeness: ResponseCompleteness.Partial, response: "partial", generationEvidence: new[] { "response-actions:visible" });
        var decision = LiveResponseCompletionGate.Evaluate(snapshot, "partial");
        Assert.False(decision.Complete);
        Assert.Equal("PARTIAL_RESPONSE", decision.Reason);
    }

    [Fact]
    public void Artifact_sanitizer_rejects_authorization_material()
    {
        var artifact = Artifact(new[] { "Authorization: Bearer secret" });
        Assert.Throws<InvalidOperationException>(() => LivePilotArtifactSanitizer.SerializeOrThrow(artifact));
    }

    [Fact]
    public void Artifact_sanitizer_rejects_token_like_material()
    {
        var artifact = Artifact(new[] { "access_token=topsecret" });
        Assert.Throws<InvalidOperationException>(() => LivePilotArtifactSanitizer.SerializeOrThrow(artifact));
    }

    [Fact]
    public void Artifact_sanitizer_allows_privacy_negative_evidence_codes()
    {
        var json = LivePilotArtifactSanitizer.SerializeOrThrow(Artifact(new[] { "privacy:no-cookie-artifact", "privacy:no-profile-artifact" }));
        Assert.Contains("privacy:no-cookie-artifact", json);
    }

    [Fact]
    public void Live_gate_requires_explicit_browser_opt_in()
    {
        var gate = LivePilotGate.Evaluate(false, true, true, true, true);
        Assert.Equal(LivePilotAcceptanceState.NotExecuted, gate.State);
        Assert.False(gate.MayUseLiveBrowser);
    }

    [Fact]
    public void Live_gate_requires_windows_runner()
    {
        var gate = LivePilotGate.Evaluate(true, false, true, true, true);
        Assert.Equal(LivePilotAcceptanceState.BlockedRunner, gate.State);
        Assert.False(gate.MaySubmit);
    }

    [Fact]
    public void Live_gate_requires_separate_real_submission_opt_in()
    {
        var gate = LivePilotGate.Evaluate(true, true, false, true, true);
        Assert.Equal(LivePilotAcceptanceState.NotExecuted, gate.State);
        Assert.True(gate.MayUseLiveBrowser);
        Assert.False(gate.MaySubmit);
    }

    [Fact]
    public void Progressive_pilot_never_forces_five_before_level_three()
    {
        Assert.Equal(1, LivePilotProgression.ResolveWorkerCount(LivePilotLevel.Level1, 5));
        Assert.Equal(2, LivePilotProgression.ResolveWorkerCount(LivePilotLevel.Level2, 1));
        Assert.Equal(3, LivePilotProgression.ResolveWorkerCount(LivePilotLevel.Level2, 5));
        Assert.Equal(5, LivePilotProgression.ResolveWorkerCount(LivePilotLevel.Level3, 5));
    }

    [Fact]
    public void Restart_reconciliation_requires_exact_persisted_identity()
    {
        var runtime = Runtime("worker-1", "worker-agent", "1", "W1-C01") with { TaskId = "task-1" };
        var restored = new LiveRestartIdentityEnvelope(runtime.ProjectRunId, runtime.LogicalAgentId, runtime.WorkerSlotId, runtime.TaskId, runtime.ConversationIdentity, runtime.RuntimeId, new[] { "dispatch-1" });
        Assert.True(LiveRestartReconciliation.Matches(runtime, restored));
        Assert.False(LiveRestartReconciliation.Matches(runtime, restored with { ConversationIdentity = "wrong" }));
    }

    [Fact]
    public async Task Fault_injector_turns_triggered_submission_into_submitted_unknown_once()
    {
        var inner = new StubAdapter(new AdapterSubmissionResult(true, true, false, "SUBMISSION_PROVEN", new[] { "triggered" }));
        var adapter = new FaultInjectingSubmissionAdapter(inner, 1);
        var runtime = Runtime("worker", "worker-agent", "1", "W1-C01");
        var expectation = new BrowserDispatchExpectation(runtime.ProjectRunId, runtime.LogicalAgentId, runtime.TaskId!, runtime.ConversationIdentity!, runtime.ProviderConversationIdentity!);
        var first = await adapter.SubmitAsync(runtime, expectation, "prompt");
        var second = await adapter.SubmitAsync(runtime, expectation, "prompt");
        Assert.True(first.SubmittedUnknown);
        Assert.True(second.ProvenSubmitted);
        Assert.Equal(2, inner.SubmitCalls);
    }

    [Fact]
    public async Task Recording_dispatch_ledger_records_state_transition_chain()
    {
        var ledger = new RecordingDispatchLedger();
        await ledger.ReserveAsync("d1", "hash");
        await ledger.UpdateAsync("d1", DispatchState.Submitting);
        await ledger.UpdateAsync("d1", DispatchState.Submitted);
        Assert.Equal(new[] { DispatchState.Prepared, DispatchState.Submitting, DispatchState.Submitted }, ledger.History("d1"));
    }

    private static LivePilotArtifact Artifact(IReadOnlyList<string> evidence) => new(
        "scenario", "source-sha", "adapter-v2", LivePilotAcceptanceState.NotExecuted, 1,
        new[] { LiveSessionMonitorEvidenceFactory.Create(Runtime("runtime", "agent", "1", "W1-C01"), Snapshot()) },
        new[] { "READY" }, new long[] { 1 }, Array.Empty<string>(), evidence);

    private static BrowserRuntimeRecord Runtime(string runtimeId, string agent, string? slot, string conversation) => new()
    {
        RuntimeId = runtimeId,
        ProjectRunId = "run",
        LogicalAgentId = agent,
        WorkerSlotId = slot,
        TaskId = slot is null ? "manager-task" : $"worker-{slot}-task",
        ProcessId = 1000,
        ProcessStartIdentity = "pid:1000:start:1",
        ContextIdentity = $"ctx-{runtimeId}",
        ProfilePath = Path.Combine(Path.GetTempPath(), "pcc-live-boundary", runtimeId),
        CreatedByPcc = true,
        ConversationIdentity = conversation,
        ProviderConversationIdentity = $"https://chatgpt.com/c/{conversation}",
        Visibility = BrowserVisibility.Hidden,
        State = BrowserSessionState.Hidden,
        LastHeartbeatAt = DateTimeOffset.UtcNow,
        LastActivityAt = DateTimeOffset.UtcNow,
        OwnershipNonce = "nonce"
    };

    private static ChatGptSemanticSnapshot Snapshot(
        InputState input = InputState.Ready,
        GenerationState generation = GenerationState.Idle,
        AuthState auth = AuthState.Authenticated,
        ConversationMatch conversation = ConversationMatch.Match,
        PageHealth health = PageHealth.Healthy,
        ResponseCompleteness completeness = ResponseCompleteness.None,
        string? response = null,
        IReadOnlyList<string>? generationEvidence = null) => new(
        SemanticDetection<InputState>.Create(input, input == InputState.Unknown ? .10 : .95, "test", "input"),
        SemanticDetection<GenerationState>.Create(generation, generation == GenerationState.Unknown ? .10 : .95, "test", (generationEvidence ?? new[] { "generation" }).ToArray()),
        SemanticDetection<AuthState>.Create(auth, auth == AuthState.Unknown ? .10 : .95, "test", "auth"),
        SemanticDetection<ConversationMatch>.Create(conversation, conversation == ConversationMatch.Unknown ? .10 : .95, "test", "conversation"),
        SemanticDetection<PageHealth>.Create(health, health == PageHealth.Unknown ? .10 : .95, "test", "health"),
        completeness, response is null ? 0 : 1, response, DateTimeOffset.UtcNow, "test");

    private sealed class StubAdapter(AdapterSubmissionResult submission) : IChatGptBrowserAdapter
    {
        public int SubmitCalls { get; private set; }
        public string AdapterVersion => "stub";
        public Task<ChatGptSemanticSnapshot> InspectAsync(BrowserRuntimeRecord runtime, BrowserDispatchExpectation expectation, CancellationToken cancellationToken = default) => Task.FromResult(Snapshot());
        public Task<AdapterSubmissionResult> SubmitAsync(BrowserRuntimeRecord runtime, BrowserDispatchExpectation expectation, string prompt, CancellationToken cancellationToken = default)
        {
            SubmitCalls++;
            return Task.FromResult(submission);
        }
    }
}
