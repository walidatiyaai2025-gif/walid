using PCCExecutive.Browser;
using Xunit;

namespace PCCExecutive.Browser.Acceptance;

public sealed class DeterministicBrowserAcceptanceTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public async Task Independent_worker_topologies_dispatch_exactly_once_and_consolidate_once(int workerCount)
    {
        var harness = new ControlledBrowserAcceptanceHarness();
        var result = await harness.RunIndependentWaveAsync(workerCount);

        Assert.Equal(workerCount, result.WorkerDispatches.Count);
        Assert.All(result.WorkerDispatches, dispatch => Assert.Equal(BrowserDispatchOutcome.Submitted, dispatch.Outcome));
        Assert.Equal(workerCount, result.Handoffs.Count);
        Assert.Equal(workerCount, result.Handoffs.Select(x => x.WorkerSlot).Distinct().Count());
        Assert.Equal(BrowserDispatchOutcome.Submitted, result.ManagerSummaryDispatch.Outcome);
        Assert.Equal(1, result.SubmitCounts["manager-runtime"]);
        for (var slot = 1; slot <= workerCount; slot++)
            Assert.Equal(1, result.SubmitCounts[$"worker-{slot}-runtime"]);
    }

    [Fact]
    public async Task Wrong_chat_blocks_worker_2_but_worker_3_continues()
    {
        var harness = new ControlledBrowserAcceptanceHarness();
        await harness.CreateTopologyAsync(3);
        var worker2 = await harness.BindTaskAsync(new AcceptanceTask("task-w2", 2, "w2", "scope-w2"));
        harness.Adapter.SetSnapshot(worker2.RuntimeId, AcceptanceSnapshots.Healthy(ConversationMatch.Mismatch));
        harness.Adapter.SetSubmission(worker2.RuntimeId, new(true, true, false, "SHOULD_NOT_RUN", ["wrong-chat"]));

        var blocked = await new BrowserChatProvider(harness.Registry, harness.Adapter, harness.Ledger, new WrongChatGuard(), harness.GlobalGate)
            .SendAsync(worker2.RuntimeId, harness.Request(worker2, "task-w2", "dispatch-w2", "w2"));

        var worker3 = await harness.BindTaskAsync(new AcceptanceTask("task-w3", 3, "w3", "scope-w3"));
        harness.Adapter.SetSnapshot(worker3.RuntimeId, AcceptanceSnapshots.Healthy());
        harness.Adapter.SetSubmission(worker3.RuntimeId, new(true, true, false, "SUBMISSION_PROVEN", ["worker3:healthy"]));
        var continued = await new BrowserChatProvider(harness.Registry, harness.Adapter, harness.Ledger, new WrongChatGuard(), harness.GlobalGate)
            .SendAsync(worker3.RuntimeId, harness.Request(worker3, "task-w3", "dispatch-w3", "w3"));

        Assert.Equal(BrowserDispatchOutcome.NotSent, blocked.Outcome);
        Assert.Equal("PROVIDER_CONVERSATION_MISMATCH", blocked.Reason);
        Assert.False(harness.Adapter.SubmitCounts.ContainsKey(worker2.RuntimeId));
        Assert.Equal(BrowserDispatchOutcome.Submitted, continued.Outcome);
    }

    [Fact]
    public async Task Unknown_adapter_is_fail_safe_and_moves_to_recovery_path()
    {
        var harness = new ControlledBrowserAcceptanceHarness();
        await harness.CreateTopologyAsync(1);
        var worker = await harness.BindTaskAsync(new AcceptanceTask("task-unknown", 1, "unknown", "scope-unknown"));
        var snapshot = AcceptanceSnapshots.Unknown();
        harness.Adapter.SetSnapshot(worker.RuntimeId, snapshot);
        harness.Adapter.SetSubmission(worker.RuntimeId, new(true, true, false, "SHOULD_NOT_RUN", ["blind-click"]));

        var result = await new BrowserChatProvider(harness.Registry, harness.Adapter, harness.Ledger, new WrongChatGuard(), harness.GlobalGate)
            .SendAsync(worker.RuntimeId, harness.Request(worker, "task-unknown", "dispatch-unknown", "unknown"));
        var resilience = new ChatGptResilienceController().Evaluate(RuntimeResilienceState.Ready,
            new RuntimeResilienceObservation(worker.RuntimeId, snapshot, DateTimeOffset.UtcNow));

        Assert.Equal(BrowserDispatchOutcome.NotSent, result.Outcome);
        Assert.Equal("BROWSER_ADAPTER_UNCERTAIN", result.Reason);
        Assert.Equal(RuntimeResilienceState.Paused, resilience.Current);
        Assert.Equal(RecoveryAction.Reinspect, resilience.RecoveryAction);
        Assert.False(harness.Adapter.SubmitCounts.ContainsKey(worker.RuntimeId));
    }

    [Fact]
    public async Task Uncertain_send_reconciles_before_any_second_submission()
    {
        var harness = new ControlledBrowserAcceptanceHarness();
        await harness.CreateTopologyAsync(1);
        var worker = await harness.BindTaskAsync(new AcceptanceTask("task-uncertain", 1, "uncertain", "scope"));
        harness.Adapter.SetSnapshot(worker.RuntimeId, AcceptanceSnapshots.Healthy());
        harness.Adapter.SetSubmission(worker.RuntimeId, new(true, false, true, "SUBMITTED_UNKNOWN", ["enter-triggered", "delivery-unproven"]));
        var provider = new BrowserChatProvider(harness.Registry, harness.Adapter, harness.Ledger, new WrongChatGuard(), harness.GlobalGate);
        var request = harness.Request(worker, "task-uncertain", "dispatch-uncertain", "uncertain");

        var first = await provider.SendAsync(worker.RuntimeId, request);
        var duplicate = await provider.SendAsync(worker.RuntimeId, request);
        var ledger = await harness.Ledger.GetAsync(request.DispatchId);
        var probe = new FixedConversationProbe(new ConversationDispatchEvidence(null, false, false, .45, ["ambiguous"]));
        var coordinator = new DispatchReconciliationCoordinator(new UncertainSendReconciler(probe), harness.Ledger);
        var unresolved = await coordinator.ReconcileAsync(worker.RuntimeId, ledger!);

        Assert.Equal(BrowserDispatchOutcome.SubmittedUnknown, first.Outcome);
        Assert.Equal(BrowserDispatchOutcome.DuplicateBlocked, duplicate.Outcome);
        Assert.Equal(RetrySafety.NotSafe, unresolved.RetrySafety);
        Assert.Equal(1, harness.Adapter.SubmitCounts[worker.RuntimeId]);

        probe.Evidence = new ConversationDispatchEvidence(false, false, false, .99, ["message-absence-proven"]);
        ledger = await harness.Ledger.GetAsync(request.DispatchId);
        var safe = await coordinator.ReconcileAsync(worker.RuntimeId, ledger!);
        harness.Adapter.SetSubmission(worker.RuntimeId, new(true, true, false, "SUBMISSION_PROVEN", ["retry-after-proof"]));
        var retry = await provider.SendAsync(worker.RuntimeId, request);

        Assert.Equal(RetrySafety.SafeRetry, safe.RetrySafety);
        Assert.Equal(BrowserDispatchOutcome.Submitted, retry.Outcome);
        Assert.Equal(2, harness.Adapter.SubmitCounts[worker.RuntimeId]);
    }

    [Fact]
    public async Task Proven_submission_is_duplicate_send_protected()
    {
        var harness = new ControlledBrowserAcceptanceHarness();
        await harness.CreateTopologyAsync(1);
        var worker = await harness.BindTaskAsync(new AcceptanceTask("task-dedupe", 1, "dedupe", "scope"));
        harness.Adapter.SetSnapshot(worker.RuntimeId, AcceptanceSnapshots.Healthy());
        harness.Adapter.SetSubmission(worker.RuntimeId, new(true, true, false, "SUBMISSION_PROVEN", ["submitted"]));
        var provider = new BrowserChatProvider(harness.Registry, harness.Adapter, harness.Ledger, new WrongChatGuard(), harness.GlobalGate);
        var request = harness.Request(worker, "task-dedupe", "dispatch-dedupe", "dedupe");

        var first = await provider.SendAsync(worker.RuntimeId, request);
        var second = await provider.SendAsync(worker.RuntimeId, request);

        Assert.Equal(BrowserDispatchOutcome.Submitted, first.Outcome);
        Assert.Equal(BrowserDispatchOutcome.DuplicateBlocked, second.Outcome);
        Assert.Equal(1, harness.Adapter.SubmitCounts[worker.RuntimeId]);
    }

    [Fact]
    public void Slow_worker_is_not_stuck_and_other_worker_can_remain_ready()
    {
        var controller = new ChatGptResilienceController();
        var now = DateTimeOffset.UtcNow;
        var slow = controller.Evaluate(RuntimeResilienceState.Generating,
            new RuntimeResilienceObservation("worker-4", AcceptanceSnapshots.Healthy(generation: GenerationState.Generating), now,
                GenerationStartedAt: now.AddMinutes(-3), LastGenerationProgressAt: now.AddMinutes(-3)));
        var healthy = controller.Evaluate(RuntimeResilienceState.Ready,
            new RuntimeResilienceObservation("worker-1", AcceptanceSnapshots.Healthy(), now));

        Assert.Equal(RuntimeResilienceState.Slow, slow.Current);
        Assert.Equal(RecoveryAction.KeepWaiting, slow.RecoveryAction);
        Assert.Equal(FaultScope.PerSession, slow.Scope);
        Assert.Equal(RuntimeResilienceState.Ready, healthy.Current);
    }

    [Fact]
    public void Stuck_worker_requires_no_progress_threshold_not_just_long_generation()
    {
        var controller = new ChatGptResilienceController();
        var now = DateTimeOffset.UtcNow;
        var slowWithProgress = controller.Evaluate(RuntimeResilienceState.Generating,
            new RuntimeResilienceObservation("worker-4", AcceptanceSnapshots.Healthy(generation: GenerationState.Generating), now,
                GenerationStartedAt: now.AddMinutes(-20), LastGenerationProgressAt: now.AddMinutes(-3)));
        var stuck = controller.Evaluate(RuntimeResilienceState.Generating,
            new RuntimeResilienceObservation("worker-4", AcceptanceSnapshots.Healthy(generation: GenerationState.Generating), now,
                GenerationStartedAt: now.AddMinutes(-20), LastGenerationProgressAt: now.AddMinutes(-9)));

        Assert.Equal(RuntimeResilienceState.Slow, slowWithProgress.Current);
        Assert.Equal(RuntimeResilienceState.Stuck, stuck.Current);
        Assert.Equal(RecoveryAction.Reinspect, stuck.RecoveryAction);
    }

    [Fact]
    public async Task Global_rate_limit_pauses_new_sends_retains_queue_and_resumes_gradually()
    {
        var harness = new ControlledBrowserAcceptanceHarness();
        await harness.CreateTopologyAsync(3);
        var provider = new BrowserChatProvider(harness.Registry, harness.Adapter, harness.Ledger, new WrongChatGuard(), harness.GlobalGate);

        var worker2 = await harness.BindTaskAsync(new AcceptanceTask("task-2", 2, "two", "scope-2"));
        harness.Adapter.SetSnapshot(worker2.RuntimeId, AcceptanceSnapshots.Healthy());
        harness.Adapter.SetSubmission(worker2.RuntimeId, new(true, true, false, "SUBMISSION_PROVEN", ["worker2:submitted"]));
        var submitted = await provider.SendAsync(worker2.RuntimeId, harness.Request(worker2, "task-2", "dispatch-2", "two"));
        Assert.Equal(BrowserDispatchOutcome.Submitted, submitted.Outcome);

        var controller = new ChatGptResilienceController();
        var now = DateTimeOffset.UtcNow;
        var limit = controller.Evaluate(RuntimeResilienceState.Ready,
            new RuntimeResilienceObservation(worker2.RuntimeId, AcceptanceSnapshots.RateLimited(), now));
        controller.ApplyGlobalGate(limit, harness.GlobalGate, now);
        var queued = new List<string> { "dispatch-3", "dispatch-1" };

        var worker3 = await harness.BindTaskAsync(new AcceptanceTask("task-3", 3, "three", "scope-3"));
        harness.Adapter.SetSnapshot(worker3.RuntimeId, AcceptanceSnapshots.Healthy());
        harness.Adapter.SetSubmission(worker3.RuntimeId, new(true, true, false, "SUBMISSION_PROVEN", ["worker3:submitted"]));
        var paused = await provider.SendAsync(worker3.RuntimeId, harness.Request(worker3, "task-3", "dispatch-3", "three"));

        Assert.Equal(BrowserDispatchOutcome.NotSent, paused.Outcome);
        Assert.Equal("GLOBAL_SEND_PAUSED", paused.Reason);
        Assert.Equal(2, queued.Count);
        Assert.Equal(1, harness.Adapter.SubmitCounts[worker2.RuntimeId]);
        Assert.False(harness.Adapter.SubmitCounts.ContainsKey(worker3.RuntimeId));

        var recovering = controller.Evaluate(RuntimeResilienceState.RateLimited,
            new RuntimeResilienceObservation(worker2.RuntimeId, AcceptanceSnapshots.Healthy(), now.AddMinutes(1)));
        var ready = controller.Evaluate(RuntimeResilienceState.Recovering,
            new RuntimeResilienceObservation(worker2.RuntimeId, AcceptanceSnapshots.Healthy(), now.AddMinutes(1).AddSeconds(1)));
        var recovery = new GlobalRateLimitRecoveryCoordinator(harness.GlobalGate);
        var resumeAt = harness.GlobalGate.Snapshot.ResumeNotBefore!.Value.AddMilliseconds(1);
        var resumed = recovery.Reevaluate(resumeAt, [ready], new AdaptivePacingState(TimeSpan.FromSeconds(40)));

        Assert.Equal(RuntimeResilienceState.Recovering, recovering.Current);
        Assert.Equal(RuntimeResilienceState.Ready, ready.Current);
        Assert.True(resumed.MayResumeNewSends);
        Assert.True(resumed.GateResumed);
        Assert.True(resumed.SuggestedInterval >= TimeSpan.FromSeconds(10));

        var after = await provider.SendAsync(worker3.RuntimeId, harness.Request(worker3, "task-3", "dispatch-3", "three"));
        Assert.Equal(BrowserDispatchOutcome.Submitted, after.Outcome);
        Assert.Equal(1, harness.Adapter.SubmitCounts[worker3.RuntimeId]);
    }

    [Fact]
    public void Per_session_temp_error_does_not_pause_healthy_workers()
    {
        var controller = new ChatGptResilienceController();
        var decision = controller.Evaluate(RuntimeResilienceState.Ready,
            new RuntimeResilienceObservation("worker-2", AcceptanceSnapshots.TempError(), DateTimeOffset.UtcNow));
        var healthy = controller.Evaluate(RuntimeResilienceState.Ready,
            new RuntimeResilienceObservation("worker-5", AcceptanceSnapshots.Healthy(), DateTimeOffset.UtcNow));

        Assert.Equal(RuntimeResilienceState.TempError, decision.Current);
        Assert.Equal(FaultScope.PerSession, decision.Scope);
        Assert.False(decision.PauseUnsafeNewSends);
        Assert.Equal(RuntimeResilienceState.Ready, healthy.Current);
    }

    [Fact]
    public async Task Partial_response_is_captured_and_never_done()
    {
        var capture = new PartialCapturePort();
        var coordinator = new PartialResponseRecoveryCoordinator(capture);
        var request = new BrowserDispatchRequest("dispatch-partial", "run", "worker-2-agent", "task-partial", "W2-C01", "https://chatgpt.com/c/W2-C01", "prompt");
        var result = await coordinator.CaptureAsync(request, AcceptanceSnapshots.Partial("visible partial answer"));

        Assert.True(result.Captured);
        Assert.False(result.MayReportDone);
        Assert.Equal("visible partial answer", Assert.Single(capture.Captures).CapturedText);
        Assert.Contains("DISPATCH_ID=dispatch-partial", result.ContinuationInstruction);
    }

    [Fact]
    public async Task Login_expiry_preserves_state_raises_attention_and_does_not_fail_running_worker()
    {
        var controller = new ChatGptResilienceController();
        var now = DateTimeOffset.UtcNow;
        var decision = controller.Evaluate(RuntimeResilienceState.Generating,
            new RuntimeResilienceObservation("manager-runtime", AcceptanceSnapshots.LoginRequired(), now));
        var preservation = new PreservationPort();
        var auth = new AuthenticationRecoveryCoordinator(preservation);
        var envelope = new RuntimePreservationEnvelope("run", "manager-agent", "manager-task", "M-C01",
            ["dispatch-manager", "dispatch-worker-1"], "captured-safe-summary", "LOGIN_REQUIRED", now);
        var attention = await auth.PauseSafelyAsync("manager-runtime", envelope, decision.Current);
        var workerDone = controller.Evaluate(RuntimeResilienceState.Generating,
            new RuntimeResilienceObservation("worker-1-runtime",
                AcceptanceSnapshots.Healthy(generation: GenerationState.Complete, completeness: ResponseCompleteness.Complete, response: "done"),
                now.AddSeconds(2)));

        Assert.Equal(RuntimeResilienceState.LoginRequired, decision.Current);
        Assert.True(decision.PauseUnsafeNewSends);
        Assert.True(decision.RequiresHumanAction);
        Assert.True(attention.Required);
        Assert.Single(preservation.Envelopes);
        Assert.Equal(RuntimeResilienceState.Done, workerDone.Current);
    }

    [Fact]
    public void Offline_recovery_preserves_work_and_returns_to_ready_without_worker_failure()
    {
        var controller = new ChatGptResilienceController();
        var gate = new GlobalBrowserSendGate();
        var now = DateTimeOffset.UtcNow;
        var queued = new[] { "dispatch-1", "dispatch-2" };

        var offline = controller.Evaluate(RuntimeResilienceState.Ready,
            new RuntimeResilienceObservation("manager-runtime", AcceptanceSnapshots.Offline(), now));
        controller.ApplyGlobalGate(offline, gate, now);
        var recovering = controller.Evaluate(RuntimeResilienceState.Offline,
            new RuntimeResilienceObservation("manager-runtime", AcceptanceSnapshots.Healthy(), now.AddMinutes(1)));
        var ready = controller.Evaluate(RuntimeResilienceState.Recovering,
            new RuntimeResilienceObservation("manager-runtime", AcceptanceSnapshots.Healthy(), now.AddMinutes(1).AddSeconds(1)));
        var resumeAt = gate.Snapshot.ResumeNotBefore!.Value.AddMilliseconds(1);
        var resumed = new GlobalRateLimitRecoveryCoordinator(gate).Reevaluate(resumeAt, [ready], new AdaptivePacingState(TimeSpan.FromSeconds(20)));

        Assert.Equal(RuntimeResilienceState.Offline, offline.Current);
        Assert.True(gate.Snapshot.IsPaused || resumed.GateResumed);
        Assert.Equal(2, queued.Length);
        Assert.Equal(RuntimeResilienceState.Recovering, recovering.Current);
        Assert.Equal(RuntimeResilienceState.Ready, ready.Current);
        Assert.True(resumed.MayResumeNewSends);
        Assert.NotEqual(RuntimeResilienceState.Failed, ready.Current);
    }

    [Fact]
    public async Task Manager_rollover_keeps_logical_manager_identity()
    {
        var active = AcceptanceTestFactory.Conversation("M-C01", "manager-agent");
        var store = new LifecycleStore();
        var journal = new RolloverJournal();
        var coordinator = new ConversationRolloverCoordinator(new CheckpointPort(), new ConversationCreator(),
            new ContinuationSender(), new ContinuationProofPort(), store, journal);
        var result = await coordinator.RolloverAsync(AcceptanceTestFactory.RolloverRequest(active, "manager-growth"));

        Assert.True(result.Succeeded);
        Assert.Equal("manager-agent", result.ActiveConversation.LogicalAgentId);
        Assert.Equal(2, result.ActiveConversation.Sequence);
        Assert.Equal("M-C01", result.ActiveConversation.PredecessorConversationId);
        Assert.Equal(ConversationLifecycleState.Archived, result.RetiredConversation!.State);
        Assert.True(store.Committed);
    }

    [Fact]
    public async Task Worker_rollover_keeps_worker_slot_logical_identity()
    {
        var active = AcceptanceTestFactory.Conversation("W3-C01", "worker-3-agent");
        var store = new LifecycleStore();
        var coordinator = new ConversationRolloverCoordinator(new CheckpointPort(), new ConversationCreator(),
            new ContinuationSender(), new ContinuationProofPort(), store, new RolloverJournal());
        var result = await coordinator.RolloverAsync(AcceptanceTestFactory.RolloverRequest(active, "worker-growth"));

        Assert.True(result.Succeeded);
        Assert.Equal("worker-3-agent", result.ActiveConversation.LogicalAgentId);
        Assert.Equal(2, result.ActiveConversation.Sequence);
        Assert.Equal("W3-C01", result.ActiveConversation.PredecessorConversationId);
    }

    [Fact]
    public async Task Failed_rollover_never_promotes_candidate_or_loses_old_conversation()
    {
        var active = AcceptanceTestFactory.Conversation("W3-C01", "worker-3-agent");
        var store = new LifecycleStore();
        var coordinator = new ConversationRolloverCoordinator(new CheckpointPort(), new ConversationCreator(),
            new ContinuationSender(), new ContinuationProofPort(false), store, new RolloverJournal());
        var result = await coordinator.RolloverAsync(AcceptanceTestFactory.RolloverRequest(active, "validation-failure"));

        Assert.False(result.Succeeded);
        Assert.Equal("W3-C01", result.ActiveConversation.ConversationId);
        Assert.Equal(ConversationLifecycleState.Active, result.ActiveConversation.State);
        Assert.False(store.Committed);
        Assert.NotNull(result.FailedCandidate);
    }

    [Fact]
    public async Task Controlled_browser_crash_recovers_owned_runtime_and_leaves_unrelated_process_untouched()
    {
        var root = Path.Combine(Path.GetTempPath(), "pcc-e2e-crash", Guid.NewGuid().ToString("N"));
        var processes = new FakeProcesses();
        var markers = new FakeMarkers();
        var registry = new InMemoryBrowserRuntimeRegistry();
        var host = new FakeRuntimeHost(root, processes);
        var runtime = ControlledBrowserAcceptanceHarness.CreateRuntime("owned-crashed", "worker-3-agent", 3, "task-3", "W3-C01", true, 41001) with { ProfilePath = Path.Combine(root, "owned-crashed") };
        processes.Set(runtime.ProcessId!.Value, runtime.ProcessStartIdentity!, false);
        var unrelatedPid = 51001;
        processes.Set(unrelatedPid, $"pid:{unrelatedPid}:personal", true);
        markers.Set(AcceptanceTestFactory.Marker(runtime));
        await registry.UpsertAsync(runtime);
        var controller = new BrowserSessionController(registry, host, new OwnershipProofService(root, markers, processes), markers, processes);

        var recovered = await controller.RecoverOrphanAsync(runtime.RuntimeId);

        Assert.True(recovered.Succeeded);
        Assert.Equal("DEAD_ORPHAN_REPLACED_WITH_NEW_PCC_RUNTIME", recovered.Reason);
        Assert.Equal(runtime.LogicalAgentId, recovered.Runtime!.LogicalAgentId);
        Assert.Empty(host.KilledRuntimeIds);
        Assert.True(processes.IsAlive(unrelatedPid));
    }

    [Fact]
    public async Task Restart_during_active_wave_and_generating_worker_reconstructs_browser_expectations()
    {
        var harness = new ControlledBrowserAcceptanceHarness();
        await harness.CreateTopologyAsync(3);
        var worker = await harness.BindTaskAsync(new AcceptanceTask("task-generating", 2, "work", "scope-2"));
        await harness.Ledger.ReserveAsync("dispatch-generating", "hash-generating");
        await harness.Ledger.UpdateAsync("dispatch-generating", DispatchState.Generating);
        var conversation = AcceptanceTestFactory.Conversation("W2-C01", worker.LogicalAgentId);
        var envelope = await harness.CaptureRestartAsync(["dispatch-generating"], [conversation], "active-wave-generating");

        ControlledBrowserAcceptanceHarness.ValidateRestartEnvelope(envelope);

        Assert.Contains(envelope.Runtimes, x => x.RuntimeId == worker.RuntimeId && x.LogicalAgentId == worker.LogicalAgentId && x.ConversationIdentity == "W2-C01");
        Assert.Equal(DispatchState.Generating, Assert.Single(envelope.Dispatches).State);
        Assert.Equal("W2-C01", Assert.Single(envelope.Conversations).ConversationId);
    }

    [Fact]
    public async Task Restart_during_submitted_unknown_preserves_uncertainty_and_idempotency_identity()
    {
        var harness = new ControlledBrowserAcceptanceHarness();
        await harness.CreateTopologyAsync(1);
        var worker = await harness.BindTaskAsync(new AcceptanceTask("task-unknown-restart", 1, "work", "scope"));
        harness.Adapter.SetSnapshot(worker.RuntimeId, AcceptanceSnapshots.Healthy());
        harness.Adapter.SetSubmission(worker.RuntimeId, new(true, false, true, "SUBMITTED_UNKNOWN", ["delivery-unproven"]));
        var request = harness.Request(worker, "task-unknown-restart", "dispatch-unknown-restart", "work");
        var provider = new BrowserChatProvider(harness.Registry, harness.Adapter, harness.Ledger, new WrongChatGuard(), harness.GlobalGate);
        await provider.SendAsync(worker.RuntimeId, request);

        var envelope = await harness.CaptureRestartAsync([request.DispatchId], phase: "submitted-unknown");
        ControlledBrowserAcceptanceHarness.ValidateRestartEnvelope(envelope);

        var dispatch = Assert.Single(envelope.Dispatches);
        Assert.Equal("dispatch-unknown-restart", dispatch.DispatchId);
        Assert.Equal(DispatchState.SubmittedUnknown, dispatch.State);
        Assert.False(string.IsNullOrWhiteSpace(dispatch.ContentHash));
    }

    [Fact]
    public async Task Restart_during_rollover_preserves_predecessor_and_uncommitted_candidate()
    {
        var harness = new ControlledBrowserAcceptanceHarness();
        await harness.CreateTopologyAsync(1);
        var active = AcceptanceTestFactory.Conversation("M-C01", harness.Manager.LogicalAgentId);
        var store = new LifecycleStore();
        var sender = new ContinuationSender(false);
        var coordinator = new ConversationRolloverCoordinator(new CheckpointPort(), new ConversationCreator(),
            sender, new ContinuationProofPort(), store, new RolloverJournal());

        var result = await coordinator.RolloverAsync(AcceptanceTestFactory.RolloverRequest(active, "restart-during-rollover"));
        var conversations = new List<ConversationRecord> { active };
        if (store.SavedCandidate is not null) conversations.Add(store.SavedCandidate);
        var envelope = await harness.CaptureRestartAsync([], conversations, "rollover-incomplete");
        ControlledBrowserAcceptanceHarness.ValidateRestartEnvelope(envelope);

        Assert.False(result.Succeeded);
        Assert.Equal(ConversationLifecycleState.Active, active.State);
        Assert.NotNull(store.SavedCandidate);
        Assert.False(store.Committed);
        Assert.Contains(envelope.Conversations, x => x.ConversationId == active.ConversationId);
    }

    [Fact]
    public async Task Archived_runtime_retirement_bounds_process_count_without_losing_lineage()
    {
        var root = Path.Combine(Path.GetTempPath(), "pcc-e2e-archive", Guid.NewGuid().ToString("N"));
        var processes = new FakeProcesses();
        var markers = new FakeMarkers();
        var registry = new InMemoryBrowserRuntimeRegistry();
        var host = new FakeRuntimeHost(root, processes);
        var evidence = new ArchiveEvidencePort();
        var lineage = new List<ConversationRecord>();

        for (var i = 1; i <= 8; i++)
        {
            var runtime = ControlledBrowserAcceptanceHarness.CreateRuntime($"archived-{i}", "worker-3-agent", 3, "task-3", $"W3-C{i:00}", true, 42000 + i) with
            {
                ProfilePath = Path.Combine(root, $"archived-{i}"),
                IsArchived = true,
                State = BrowserSessionState.Archived
            };
            processes.Set(runtime.ProcessId!.Value, runtime.ProcessStartIdentity!, true);
            markers.Set(AcceptanceTestFactory.Marker(runtime));
            await registry.UpsertAsync(runtime);
            evidence.Prove(runtime.LogicalAgentId, runtime.ConversationIdentity!);
            lineage.Add(AcceptanceTestFactory.Conversation(runtime.ConversationIdentity!, runtime.LogicalAgentId, i, ConversationLifecycleState.Archived));
        }

        var controller = new BrowserSessionController(registry, host, new OwnershipProofService(root, markers, processes), markers, processes);
        var result = await new ArchivedConversationRuntimeRetirementService(registry, controller, evidence).RetireArchivedAsync();

        Assert.Equal(8, result.RetiredRuntimeIds.Count);
        Assert.Equal(8, lineage.Count);
        Assert.Equal(8, host.KilledRuntimeIds.Count);
        Assert.All(lineage, conversation => Assert.Equal("worker-3-agent", conversation.LogicalAgentId));
    }

    [Fact]
    public async Task Personal_chrome_invariant_survives_restart_kill_killall_orphan_and_retirement_paths()
    {
        var root = Path.Combine(Path.GetTempPath(), "pcc-e2e-personal", Guid.NewGuid().ToString("N"));
        var processes = new FakeProcesses();
        var markers = new FakeMarkers();
        var registry = new InMemoryBrowserRuntimeRegistry();
        var host = new FakeRuntimeHost(root, processes);
        var personal = ControlledBrowserAcceptanceHarness.CreateRuntime("personal-chrome", "unknown", null, "none", "personal", false, 53001) with
        {
            ProfilePath = Path.Combine(root, "personal-chrome"),
            LastHeartbeatAt = DateTimeOffset.UtcNow.AddHours(-2)
        };
        processes.Set(personal.ProcessId!.Value, personal.ProcessStartIdentity!, true);
        markers.Set(AcceptanceTestFactory.Marker(personal));
        await registry.UpsertAsync(personal);
        var controller = new BrowserSessionController(registry, host, new OwnershipProofService(root, markers, processes), markers, processes);

        var kill = await controller.KillAsync(personal.RuntimeId);
        var restart = await controller.RestartAsync(personal.RuntimeId);
        var killAll = await controller.KillAllPccSessionsAsync();
        var orphans = await controller.DetectOrphansAsync(TimeSpan.FromMinutes(10));
        var orphanRecovery = await controller.RecoverOrphanAsync(personal.RuntimeId);
        var archived = personal with { IsArchived = true, State = BrowserSessionState.Archived };
        await registry.UpsertAsync(archived);
        var archiveEvidence = new ArchiveEvidencePort();
        archiveEvidence.Prove(archived.LogicalAgentId, archived.ConversationIdentity!);
        var retirement = await new ArchivedConversationRuntimeRetirementService(registry, controller, archiveEvidence).RetireArchivedAsync();

        Assert.False(kill.Succeeded);
        Assert.False(restart.Succeeded);
        Assert.Contains(personal.RuntimeId, killAll.SkippedRuntimeReasons.Keys);
        Assert.Contains(orphans, x => x.RuntimeId == personal.RuntimeId);
        Assert.False(orphanRecovery.Succeeded);
        Assert.Empty(retirement.RetiredRuntimeIds);
        Assert.True(processes.IsAlive(personal.ProcessId.Value));
        Assert.Empty(host.KilledRuntimeIds);
    }

    [Fact]
    public async Task Acceptance_artifact_is_structured_and_privacy_safe()
    {
        var harness = new ControlledBrowserAcceptanceHarness();
        await harness.RunIndependentWaveAsync(1);
        var report = harness.Report("one-worker");
        var json = AcceptanceArtifactWriter.Serialize(report with { EvidenceSummary = report.EvidenceSummary.Concat(["cookie:must-not-emit"]).ToArray() });

        Assert.Contains("\"Scenario\": \"one-worker\"", json);
        Assert.Contains("\"RuntimeIds\"", json);
        Assert.Contains("\"LogicalAgentIds\"", json);
        Assert.False(json.Contains("cookie:must-not-emit", StringComparison.OrdinalIgnoreCase));
        Assert.False(json.Contains("authorization", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Deterministic_fixture_catalog_covers_controlled_browser_failure_shapes()
    {
        Assert.Equal(16, AcceptanceHtmlFixtures.All.Count);
        var names = AcceptanceHtmlFixtures.All.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("wrong-conversation", names);
        Assert.Contains("uncertain-submission", names);
        Assert.Contains("context-limit", names);
        Assert.Contains("offline", names);
        Assert.Contains("challenge", names);
    }
}
