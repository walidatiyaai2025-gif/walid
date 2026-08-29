using PCCExecutive.Browser;
using Xunit;

namespace PCCExecutive.Browser.Tests;

public sealed class ResilienceHardeningTests
{
    [Fact]
    public void Global_rate_limit_pauses_new_sends_and_preserves_generations()
    {
        var snapshot = new DeterministicHtmlFixtureProbe().Inspect(ChatGptAdapterFixtures.SendingTooFast);
        var controller = new ChatGptResilienceController();
        var decision = controller.Evaluate(RuntimeResilienceState.Ready, new RuntimeResilienceObservation("r1", snapshot, DateTimeOffset.UtcNow));
        var gate = new GlobalBrowserSendGate();
        controller.ApplyGlobalGate(decision, gate, DateTimeOffset.UtcNow);

        Assert.Equal(RuntimeResilienceState.RateLimited, decision.Current);
        Assert.Equal(FaultScope.Global, decision.Scope);
        Assert.True(decision.PauseUnsafeNewSends);
        Assert.True(decision.PreserveInFlightGenerations);
        Assert.True(gate.Snapshot.IsPaused);
        Assert.NotNull(decision.Cooldown);
    }

    [Fact]
    public void Slow_session_degrades_only_that_session()
    {
        var snapshot = new DeterministicHtmlFixtureProbe().Inspect(ChatGptAdapterFixtures.Generating);
        var now = DateTimeOffset.UtcNow;
        var decision = new ChatGptResilienceController().Evaluate(
            RuntimeResilienceState.Generating,
            new RuntimeResilienceObservation("worker-3", snapshot, now, now.AddMinutes(-3), now.AddMinutes(-3)));

        Assert.Equal(RuntimeResilienceState.Slow, decision.Current);
        Assert.Equal(FaultScope.PerSession, decision.Scope);
        Assert.False(decision.PauseUnsafeNewSends);
        Assert.Equal(RecoveryAction.KeepWaiting, decision.RecoveryAction);
    }

    [Fact]
    public void Adaptive_pacing_increases_under_pressure_and_recovers_gradually()
    {
        var policy = new AdaptivePacingPolicy();
        var start = new AdaptivePacingState(TimeSpan.FromSeconds(10));
        var pressured = policy.Evaluate(start, new AdaptivePacingObservation(RuntimeResilienceState.Slow, 2, false, false));
        Assert.True(pressured.State.CurrentInterval > TimeSpan.FromSeconds(10));

        var current = pressured.State;
        for (var i = 0; i < 12; i++)
            current = policy.Evaluate(current, new AdaptivePacingObservation(RuntimeResilienceState.Ready, 0, false, false)).State;

        Assert.True(current.CurrentInterval < pressured.State.CurrentInterval);
        Assert.True(current.CurrentInterval >= TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Submitted_unknown_cannot_blind_retry_when_evidence_is_uncertain()
    {
        var reconciler = new UncertainSendReconciler(new EvidenceProbe(new ConversationDispatchEvidence(null, false, false, .50, new[] { "fixture:ambiguous" })));
        var dispatch = new DispatchLedgerEntry("d1", "hash", DispatchState.SubmittedUnknown, DateTimeOffset.UtcNow);
        var result = await reconciler.ReconcileAsync("runtime", dispatch);
        Assert.Equal(SendReconciliationState.CannotDetermine, result.State);
        Assert.Equal(RetrySafety.NotSafe, result.RetrySafety);
    }

    [Fact]
    public async Task Submitted_unknown_allows_retry_only_when_absence_is_proven()
    {
        var reconciler = new UncertainSendReconciler(new EvidenceProbe(new ConversationDispatchEvidence(false, false, false, .98, new[] { "fixture:message-absent" })));
        var dispatch = new DispatchLedgerEntry("d1", "hash", DispatchState.SubmittedUnknown, DateTimeOffset.UtcNow);
        var result = await reconciler.ReconcileAsync("runtime", dispatch);
        Assert.Equal(SendReconciliationState.MessageNotPresent, result.State);
        Assert.Equal(RetrySafety.SafeRetry, result.RetrySafety);
    }

    [Fact]
    public void Partial_response_never_reports_done()
    {
        var snapshot = new DeterministicHtmlFixtureProbe().Inspect(ChatGptAdapterFixtures.PartialResponse);
        var result = new ResponseCompletionClassifier().Classify(new ResponseCompletionObservation(snapshot, false, true, false, false, false, true));
        Assert.Equal(ResponseExecutionState.Partial, result.State);
        Assert.False(result.MayReportDone);
    }

    [Fact]
    public void Long_generation_is_slow_before_it_is_stuck()
    {
        var snapshot = new DeterministicHtmlFixtureProbe().Inspect(ChatGptAdapterFixtures.Generating);
        var controller = new ChatGptResilienceController();
        var now = DateTimeOffset.UtcNow;
        var slow = controller.Evaluate(RuntimeResilienceState.Generating, new RuntimeResilienceObservation("r", snapshot, now, now.AddMinutes(-3), now.AddMinutes(-3)));
        var stuck = controller.Evaluate(RuntimeResilienceState.Slow, new RuntimeResilienceObservation("r", snapshot, now, now.AddMinutes(-10), now.AddMinutes(-10)));
        Assert.Equal(RuntimeResilienceState.Slow, slow.Current);
        Assert.Equal(RuntimeResilienceState.Stuck, stuck.Current);
    }

    [Fact]
    public async Task Successful_rollover_switches_only_after_ack_and_identity_validation()
    {
        var active = Conversation("manager", "M-C01", 1);
        var store = new LifecycleStore();
        var coordinator = Coordinator(store, proofValid: true, send: true, createdId: "M-C02");
        var result = await coordinator.RolloverAsync(Request(active));
        Assert.True(result.Succeeded);
        Assert.Equal("M-C02", result.ActiveConversation.ConversationId);
        Assert.Equal("M-C01", result.ActiveConversation.PredecessorConversationId);
        Assert.Equal("M-C02", result.RetiredConversation!.SuccessorConversationId);
        Assert.True(store.Committed);
    }

    [Fact]
    public async Task Failed_rollover_preserves_old_active_conversation()
    {
        var active = Conversation("worker-1", "W1-C01", 1);
        var store = new LifecycleStore();
        var coordinator = Coordinator(store, proofValid: false, send: true, createdId: "W1-C02");
        var result = await coordinator.RolloverAsync(Request(active));
        Assert.False(result.Succeeded);
        Assert.Equal("W1-C01", result.ActiveConversation.ConversationId);
        Assert.Equal(ConversationLifecycleState.Active, result.ActiveConversation.State);
        Assert.False(store.Committed);
        Assert.True(store.FailedRecorded);
    }

    [Theory]
    [InlineData("manager", "M-C01", "M-C02")]
    [InlineData("worker-3", "W3-C01", "W3-C02")]
    public async Task Logical_agent_identity_survives_manager_and_worker_rollover(string agent, string oldId, string newId)
    {
        var active = Conversation(agent, oldId, 1);
        var result = await Coordinator(new LifecycleStore(), true, true, newId).RolloverAsync(Request(active));
        Assert.True(result.Succeeded);
        Assert.Equal(agent, result.ActiveConversation.LogicalAgentId);
        Assert.Equal(2, result.ActiveConversation.Sequence);
    }

    [Fact]
    public void Context_limit_is_recovery_not_project_failure()
    {
        var snapshot = new DeterministicHtmlFixtureProbe().Inspect(ChatGptAdapterFixtures.ContextLimit);
        var controller = new ChatGptResilienceController();
        var decision = controller.Evaluate(RuntimeResilienceState.Ready, new RuntimeResilienceObservation("r", snapshot, DateTimeOffset.UtcNow, ExplicitContextLimitDetected: true));
        var rollover = new PreventiveRolloverPolicy().Evaluate(new ConversationGrowthObservation(20, 10000, 2, TimeSpan.FromHours(1), 0, true, false));
        Assert.Equal(RuntimeResilienceState.ContextLimitDetected, decision.Current);
        Assert.NotEqual(RuntimeResilienceState.Failed, decision.Current);
        Assert.Equal(ConversationHealthState.Rotate, rollover.State);
        Assert.True(rollover.FreezeNewWork);
        Assert.True(rollover.RequestCheckpoint);
        Assert.False(rollover.IsHeuristic);
    }

    [Fact]
    public void Adapter_drift_is_fail_safe()
    {
        var snapshot = new DeterministicHtmlFixtureProbe().Inspect(ChatGptAdapterFixtures.ChangedUnknownUi);
        var drift = new ChatGptAdapterDriftGuard().Evaluate(snapshot);
        Assert.False(drift.IsCertain);
        Assert.Equal("BROWSER_ADAPTER_UNCERTAIN", drift.Reason);
    }

    [Fact]
    public void All_required_deterministic_adapter_fixtures_exist()
    {
        Assert.Equal(18, ChatGptAdapterFixtures.All.Count);
        Assert.Contains(ChatGptAdapterFixtures.All, x => x.Name == "healthy-idle");
        Assert.Contains(ChatGptAdapterFixtures.All, x => x.Name == "response-complete-without-actions");
        Assert.Contains(ChatGptAdapterFixtures.All, x => x.Name == "response-complete-modern-turn");
        Assert.Contains(ChatGptAdapterFixtures.All, x => x.Name == "continuation-failed");
        Assert.Contains(ChatGptAdapterFixtures.All, x => x.Name == "offline");
    }

    [Fact]
    public async Task Login_recovery_preserves_runtime_state_before_attention()
    {
        var port = new PreservationPort();
        var coordinator = new AuthenticationRecoveryCoordinator(port);
        var envelope = new RuntimePreservationEnvelope("run", "agent", "task", "conversation", new[] { "dispatch-1" }, "captured partial text", "LOGIN_REQUIRED", DateTimeOffset.UtcNow);
        var result = await coordinator.PauseSafelyAsync("runtime", envelope, RuntimeResilienceState.LoginRequired);
        Assert.True(result.Required);
        Assert.NotNull(port.Saved);
        Assert.Equal("dispatch-1", port.Saved!.DispatchIds.Single());
        Assert.Equal("captured partial text", port.Saved.CapturedResponse);
    }

    [Fact]
    public void Recovery_ladder_never_restarts_active_generation_without_evidence()
    {
        var ladder = new RecoveryLadder();
        var preserve = ladder.Decide(new RecoveryAttemptContext(4, RuntimeResilienceState.Generating, true, false, true, true));
        var restart = ladder.Decide(new RecoveryAttemptContext(4, RuntimeResilienceState.Stuck, false, true, true, true));
        var noOwnership = ladder.Decide(new RecoveryAttemptContext(4, RuntimeResilienceState.Stuck, false, true, true, false));
        Assert.Equal(RecoveryAction.KeepWaiting, preserve.Action);
        Assert.Equal(RecoveryAction.RestartPccSession, restart.Action);
        Assert.Equal(RecoveryAction.Escalate, noOwnership.Action);
    }

    [Fact]
    public async Task Archived_runtime_retirement_requires_lineage_and_positive_pcc_ownership()
    {
        var registry = new InMemoryBrowserRuntimeRegistry();
        var owned = Runtime("owned", createdByPcc: true) with { State = BrowserSessionState.Archived, IsArchived = true };
        var personal = Runtime("personal", createdByPcc: false) with { State = BrowserSessionState.Archived, IsArchived = true };
        await registry.UpsertAsync(owned); await registry.UpsertAsync(personal);
        var host = new RetirementHost();
        var sessions = new BrowserSessionController(registry, host, new RetirementOwnershipProof(), new NullMarkers(), new NullProcesses());
        var service = new ArchivedConversationRuntimeRetirementService(registry, sessions, new ArchiveEvidence());
        var result = await service.RetireArchivedAsync();
        Assert.Contains("owned", result.RetiredRuntimeIds);
        Assert.DoesNotContain("personal", result.SkippedReasons["personal"]);
        Assert.Equal("NO_PCC_OWNERSHIP_FLAG", result.SkippedReasons["personal"]);
        Assert.Equal(new[] { "owned" }, host.Killed);
    }

    [Fact]
    public void Continuation_packet_is_compact_and_requires_live_fetch()
    {
        var packet = new ContinuationPacketBuilder().Build(Packet("cp", "old"));
        Assert.Contains("PROJECT: PCCEXECUTIVE", packet);
        Assert.Contains("PREVIOUS_CONVERSATION_ID: old", packet);
        Assert.Contains("FETCH LIVE STATE BEFORE MAKING NEW CONCLUSIONS.", packet);
        Assert.DoesNotContain("full transcript", packet, StringComparison.OrdinalIgnoreCase);
    }

    private static ConversationRecord Conversation(string agent, string id, int sequence) => new()
    {
        ConversationId = id,
        LogicalAgentId = agent,
        ProjectRunId = "run-1",
        Sequence = sequence,
        UrlOrProviderIdentity = $"https://chatgpt.com/c/{id}",
        CreatedAt = DateTimeOffset.UtcNow,
        State = ConversationLifecycleState.Active
    };

    private static RolloverRequest Request(ConversationRecord active) => new(active, "growth", checkpoint => Packet(checkpoint, active.ConversationId));

    private static ContinuationPacketData Packet(string checkpoint, string previous) => new(
        "PCCEXECUTIVE", "walidatiyaai2025-gif/walid", "worker", "task", "wave-1", "worker/branch", "abc123", "#5",
        new[] { "browser runtime" }, new[] { "none" }, new[] { "browser-first" }, checkpoint, previous, "continue hardening");

    private static ConversationRolloverCoordinator Coordinator(LifecycleStore store, bool proofValid, bool send, string createdId) => new(
        new Checkpoint(), new Creator(createdId), new Sender(send), new Proof(proofValid), store, new Journal());

    private static BrowserRuntimeRecord Runtime(string id, bool createdByPcc) => new()
    {
        RuntimeId = id,
        ProjectRunId = "run",
        LogicalAgentId = "agent",
        TaskId = "task",
        ProcessId = createdByPcc ? 1001 : 2002,
        ProcessStartIdentity = createdByPcc ? "pid:1001:start:1" : "pid:2002:start:2",
        ContextIdentity = createdByPcc ? "ctx-owned" : "ctx-personal",
        ProfilePath = Path.Combine(Path.GetTempPath(), "pcc-retirement", id),
        CreatedByPcc = createdByPcc,
        AdoptedExplicitly = false,
        ConversationIdentity = $"conversation-{id}",
        ProviderConversationIdentity = $"https://chatgpt.com/c/{id}",
        Visibility = BrowserVisibility.Hidden,
        State = BrowserSessionState.Archived,
        LastHeartbeatAt = DateTimeOffset.UtcNow,
        LastActivityAt = DateTimeOffset.UtcNow,
        OwnershipNonce = createdByPcc ? "nonce-owned" : "nonce-personal"
    };

    private sealed class EvidenceProbe(ConversationDispatchEvidence evidence) : IConversationEvidenceProbe
    {
        public Task<ConversationDispatchEvidence> InspectDispatchAsync(string runtimeId, string dispatchId, string contentHash, CancellationToken cancellationToken = default) => Task.FromResult(evidence);
    }

    private sealed class Checkpoint : IConversationCheckpointPort
    {
        public Task<string> CreateCheckpointAsync(ConversationRecord activeConversation, CancellationToken cancellationToken = default) => Task.FromResult("checkpoint-1");
    }

    private sealed class Creator(string id) : IConversationCreator
    {
        public Task<ConversationCreationResult> CreateAsync(ConversationRecord predecessor, CancellationToken cancellationToken = default) => Task.FromResult(new ConversationCreationResult(id, $"https://chatgpt.com/c/{id}"));
    }

    private sealed class Sender(bool result) : IContinuationSender
    {
        public Task<bool> SendContinuationAsync(ConversationRecord candidate, string checkpointId, string continuationPacket, CancellationToken cancellationToken = default) => Task.FromResult(result);
    }

    private sealed class Proof(bool valid) : IContinuationProofPort
    {
        public Task<ContinuationProof> ValidateAsync(ConversationRecord candidate, string checkpointId, CancellationToken cancellationToken = default) => Task.FromResult(new ContinuationProof(true, true, valid, valid, valid, valid, valid, new[] { valid ? "fixture:continuation-valid" : "fixture:continuation-invalid" }));
    }

    private sealed class Journal : IRolloverJournalPort
    {
        public Task RecordAsync(string logicalAgentId, string conversationId, RolloverStage stage, string reason, DateTimeOffset occurredAt, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class LifecycleStore : IConversationLifecycleStore
    {
        public bool Committed { get; private set; }
        public bool FailedRecorded { get; private set; }
        public Task SaveCandidateAsync(ConversationRecord candidate, string checkpointId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CommitRolloverAsync(ConversationRecord predecessorArchived, ConversationRecord successorActive, string checkpointId, CancellationToken cancellationToken = default) { Committed = true; return Task.CompletedTask; }
        public Task RecordFailedRolloverAsync(ConversationRecord predecessorStillActive, ConversationRecord? failedCandidate, string reason, CancellationToken cancellationToken = default) { FailedRecorded = true; return Task.CompletedTask; }
    }

    private sealed class PreservationPort : IRuntimePreservationPort
    {
        public RuntimePreservationEnvelope? Saved { get; private set; }
        public Task PreserveAsync(RuntimePreservationEnvelope envelope, CancellationToken cancellationToken = default) { Saved = envelope; return Task.CompletedTask; }
    }

    private sealed class ArchiveEvidence : IConversationArchiveEvidencePort
    {
        public Task<bool> IsLineageSafelyArchivedAsync(string logicalAgentId, string conversationIdentity, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class RetirementOwnershipProof : IOwnershipProofService
    {
        public Task<OwnershipProof> ProveAsync(BrowserRuntimeRecord runtime, CancellationToken cancellationToken = default) => Task.FromResult(runtime.CreatedByPcc || runtime.AdoptedExplicitly ? OwnershipProof.Proven(runtime.RuntimeId) : OwnershipProof.Denied(runtime.RuntimeId, "NO_PCC_OWNERSHIP_FLAG"));
    }

    private sealed class RetirementHost : IBrowserRuntimeHost
    {
        public List<string> Killed { get; } = new();
        public Task<BrowserRuntimeRecord> LaunchAsync(BrowserSessionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> RecoverAsync(BrowserRuntimeRecord runtime, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task SetVisibilityAsync(BrowserRuntimeRecord runtime, BrowserVisibility visibility, bool bringToFront, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task KillAsync(BrowserRuntimeRecord runtime, OwnershipProof proof, CancellationToken cancellationToken = default) { Assert.True(proof.IsProven); Killed.Add(runtime.RuntimeId); return Task.CompletedTask; }
        public Task<BrowserRuntimeTelemetry> GetTelemetryAsync(BrowserRuntimeRecord runtime, CancellationToken cancellationToken = default) => Task.FromResult(new BrowserRuntimeTelemetry(runtime.RuntimeId, true, 1, 0, TimeSpan.Zero, runtime.LastHeartbeatAt, true, runtime.IsArchived));
    }

    private sealed class NullMarkers : IOwnershipMarkerStore
    {
        public Task WriteAsync(OwnershipMarker marker, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<OwnershipMarker?> ReadAsync(string profilePath, CancellationToken cancellationToken = default) => Task.FromResult<OwnershipMarker?>(null);
    }

    private sealed class NullProcesses : IProcessInspector
    {
        public bool IsAlive(int processId) => true;
        public string? GetStartIdentity(int processId) => null;
    }
}
