using PCCExecutive.Application;
using PCCExecutive.Browser;
using PCCExecutive.Domain;
using PCCExecutive.Infrastructure;
using Xunit;

namespace PCCExecutive.E2E;

public sealed class ProductionRuntimeSecurityNegativeTests
{
    [Fact]
    public async Task Browser_send_boundary_rejects_every_identity_and_ownership_mismatch_with_zero_enter()
    {
        await using var h = await ProductionRuntimeAcceptanceHarness.CreateAsync();
        await h.SelectProjectAsync();

        var worker = h.WorkerAgentIds[0];
        var task = TaskId.New();
        var conversation = ConversationId.New();
        var runtime = await h.Sessions.CreateAsync(new BrowserSessionRequest(
            h.Run.Id.ToString(), worker.ToString(), "1", task.ToString(), conversation.ToString(), "provider-bound", BrowserVisibility.Hidden));
        await h.Store.SaveLogicalAgentAsync(new LogicalAgentSession(worker, h.Run.Id, AgentRole.Worker, new WorkerSlotId(1), task, conversation, LogicalSessionState.Active));

        var provider = ProductionRuntimeAcceptanceHarness.GetField<BrowserChatProvider>(h.AgentProvider, "_provider");
        var initialEnter = h.Adapter.PhysicalEnterCount;

        await AssertZero("wrong-project", new BrowserDispatchRequest(Guid.NewGuid().ToString("N"), ProjectRunId.New().ToString(), worker.ToString(), task.ToString(), conversation.ToString(), "provider-bound", "payload", "hash-a", "1"));
        await AssertZero("wrong-agent", new BrowserDispatchRequest(Guid.NewGuid().ToString("N"), h.Run.Id.ToString(), LogicalAgentId.New().ToString(), task.ToString(), conversation.ToString(), "provider-bound", "payload", "hash-b", "1"));
        await AssertZero("wrong-slot", new BrowserDispatchRequest(Guid.NewGuid().ToString("N"), h.Run.Id.ToString(), worker.ToString(), task.ToString(), conversation.ToString(), "provider-bound", "payload", "hash-c", "2"));
        await AssertZero("wrong-task", new BrowserDispatchRequest(Guid.NewGuid().ToString("N"), h.Run.Id.ToString(), worker.ToString(), TaskId.New().ToString(), conversation.ToString(), "provider-bound", "payload", "hash-d", "1"));
        await AssertZero("wrong-logical-conversation", new BrowserDispatchRequest(Guid.NewGuid().ToString("N"), h.Run.Id.ToString(), worker.ToString(), task.ToString(), ConversationId.New().ToString(), "provider-bound", "payload", "hash-e", "1"));
        await AssertZero("wrong-provider-conversation", new BrowserDispatchRequest(Guid.NewGuid().ToString("N"), h.Run.Id.ToString(), worker.ToString(), task.ToString(), conversation.ToString(), "provider-other", "payload", "hash-f", "1"));

        h.Ownership.Allow = false;
        await AssertZero("ownership-failure", new BrowserDispatchRequest(Guid.NewGuid().ToString("N"), h.Run.Id.ToString(), worker.ToString(), task.ToString(), conversation.ToString(), "provider-bound", "payload", "hash-g", "1"));
        h.Ownership.Allow = true;

        h.Adapter.BeforeFinalAuthorization(_ => { h.Ownership.Allow = false; return Task.CompletedTask; });
        var tamper = await provider.SendAsync(runtime.RuntimeId, new BrowserDispatchRequest(Guid.NewGuid().ToString("N"), h.Run.Id.ToString(), worker.ToString(), task.ToString(), conversation.ToString(), "provider-bound", "payload", "hash-h", "1"));
        Assert.Equal(BrowserDispatchOutcome.NotSent, tamper.Outcome);
        Assert.Equal("PRE_ENTER_AUTHORIZATION_DENIED", tamper.Reason);
        Assert.Equal(initialEnter, h.Adapter.PhysicalEnterCount);
        h.Ownership.Allow = true;

        async Task AssertZero(string scenario, BrowserDispatchRequest request)
        {
            var result = await provider.SendAsync(runtime.RuntimeId, request);
            Assert.Equal(BrowserDispatchOutcome.NotSent, result.Outcome);
            Assert.Equal(initialEnter, h.Adapter.PhysicalEnterCount);
            Assert.False(string.IsNullOrWhiteSpace(result.Reason), scenario);
        }
    }

    [Fact]
    public async Task Ambiguous_submission_content_conflict_and_retired_conversation_never_blindly_resend()
    {
        await using var h = await ProductionRuntimeAcceptanceHarness.CreateAsync();
        await h.SelectProjectAsync();
        var worker = h.WorkerAgentIds[0];
        var task = TaskId.New();
        var conversation = ConversationId.New();
        var runtime = await h.Sessions.CreateAsync(new BrowserSessionRequest(h.Run.Id.ToString(), worker.ToString(), "1", task.ToString(), conversation.ToString(), "provider-bound", BrowserVisibility.Hidden));
        var provider = ProductionRuntimeAcceptanceHarness.GetField<BrowserChatProvider>(h.AgentProvider, "_provider");

        var uncertainId = Guid.NewGuid().ToString("N");
        var uncertainRequest = new BrowserDispatchRequest(uncertainId, h.Run.Id.ToString(), worker.ToString(), task.ToString(), conversation.ToString(), "provider-bound", "uncertain", "uncertain-hash", "1");
        h.Adapter.QueueSubmission(false, true, "SUBMITTED_UNKNOWN");
        var first = await provider.SendAsync(runtime.RuntimeId, uncertainRequest);
        Assert.Equal(BrowserDispatchOutcome.SubmittedUnknown, first.Outcome);
        var afterFirst = h.Adapter.PhysicalEnterCount;
        var replay = await provider.SendAsync(runtime.RuntimeId, uncertainRequest);
        Assert.Equal(BrowserDispatchOutcome.DuplicateBlocked, replay.Outcome);
        Assert.Equal(afterFirst, h.Adapter.PhysicalEnterCount);

        var conflictId = Guid.NewGuid().ToString("N");
        var conflictA = new BrowserDispatchRequest(conflictId, h.Run.Id.ToString(), worker.ToString(), task.ToString(), conversation.ToString(), "provider-bound", "first-content", "content-hash-a", "1");
        var accepted = await provider.SendAsync(runtime.RuntimeId, conflictA);
        Assert.Equal(BrowserDispatchOutcome.Submitted, accepted.Outcome);
        var afterAccepted = h.Adapter.PhysicalEnterCount;
        var conflictB = conflictA with { Prompt = "replacement-content", ContentHash = "content-hash-b" };
        var conflict = await provider.SendAsync(runtime.RuntimeId, conflictB);
        Assert.Equal(BrowserDispatchOutcome.DuplicateBlocked, conflict.Outcome);
        Assert.Equal("DISPATCH_ID_CONTENT_HASH_CONFLICT", conflict.Reason);
        Assert.Equal(afterAccepted, h.Adapter.PhysicalEnterCount);

        await ((IBrowserRuntimeRegistry)h.Store).UpsertAsync(runtime with { IsArchived = true, State = BrowserSessionState.Archived });
        var retired = await provider.SendAsync(runtime.RuntimeId, conflictA with { DispatchId = Guid.NewGuid().ToString("N"), ContentHash = "retired-hash" });
        Assert.Equal(BrowserDispatchOutcome.NotSent, retired.Outcome);
        Assert.Equal(afterAccepted, h.Adapter.PhysicalEnterCount);
    }

    [Theory]
    [InlineData("LOGIN_REQUIRED")]
    [InlineData("CHALLENGE")]
    public async Task Login_or_challenge_in_production_reconciliation_globally_blocks_new_sends(string fault)
    {
        await using var h = await ProductionRuntimeAcceptanceHarness.CreateAsync();
        await h.SelectProjectAsync();
        await h.ConnectManagerAsync();
        await h.StartManagerAsync();
        var runtime = await h.RuntimeForAsync(h.ManagerAgentId);
        var auth = fault == "CHALLENGE" ? AuthState.Challenge : AuthState.LoginRequired;
        h.Adapter.SetSemantic(runtime.RuntimeId, ProductionRuntimeAcceptanceHarness.ScriptedBrowserAdapter.SemanticReady(auth: auth));
        await h.ReconcileAsync();
        Assert.True(h.SendGate.Snapshot.IsPaused);
        Assert.Contains(fault == "CHALLENGE" ? "CHALLENGE" : "LOGIN", h.Host.Snapshot.AttentionItems.Single().WhatHappened, StringComparison.OrdinalIgnoreCase);
        var enters = h.Adapter.PhysicalEnterCount;
        var blocked = await h.AgentProvider.SendAsync(new AgentRequest(h.Run.Id, h.ManagerAgentId, new ConversationId(Guid.Parse(runtime.ConversationIdentity!)), DispatchId.New(), "blocked", "blocked-hash", null, null, null, runtime.ProviderConversationIdentity));
        Assert.False(blocked.Accepted);
        Assert.Equal(enters, h.Adapter.PhysicalEnterCount);
    }

    [Fact]
    public async Task Resume_ai_cannot_bypass_startup_recovery_fence_and_only_opens_after_governed_reconciliation()
    {
        await using var h = await ProductionRuntimeAcceptanceHarness.CreateAsync();
        await h.SelectProjectAsync();
        await h.ConnectManagerAsync();
        await h.StartManagerAsync();

        var managerRuntime = await h.RuntimeForAsync(h.ManagerAgentId);
        var workerTask = ProductionRuntimeAcceptanceHarness.CreatePlannedTask("Recovery fence worker proof", "tests/runtime-final/recovery-fence-worker");
        h.Adapter.SetSemantic(managerRuntime.RuntimeId,
            ProductionRuntimeAcceptanceHarness.ScriptedBrowserAdapter.SemanticReady(
                ProductionRuntimeAcceptanceHarness.PlanJson(h.Route, 80m, "CONTINUE", workerTask), complete: true));
        await h.ReconcileAsync();
        await h.StartDispatchAsync();

        var workerId = h.WorkerAgentIds[0];
        var workerRuntime = await h.RuntimeForAsync(workerId);
        var durableManager = await h.Store.LoadLogicalAgentAsync(h.ManagerAgentId);
        var durableWorker = await h.Store.LoadLogicalAgentAsync(workerId);
        Assert.NotNull(durableManager?.CurrentConversationId);
        Assert.NotNull(durableWorker?.CurrentConversationId);
        Assert.NotNull(durableWorker?.CurrentTaskId);
        var correctManagerConversation = durableManager!.CurrentConversationId!.Value;

        await h.PauseAsync();
        var runId = h.Run.Id;
        Assert.Contains("\"paused\":true", (await h.Store.LoadCheckpointAsync($"autopilot-pause:{runId}"))!.Payload, StringComparison.OrdinalIgnoreCase);

        var nonCanonicalManagerConversation = correctManagerConversation.Value.ToString("D");
        Assert.False(StringComparer.Ordinal.Equals(correctManagerConversation.ToString(), nonCanonicalManagerConversation));
        await ((IBrowserRuntimeRegistry)h.Store).UpsertAsync(managerRuntime with { ConversationIdentity = nonCanonicalManagerConversation });
        await h.ForceInterruptedRestartAsync();

        var mismatchedRuntime = await h.RuntimeForAsync(h.ManagerAgentId);
        var storedManagerAfterRestart = await h.Store.LoadLogicalAgentAsync(h.ManagerAgentId);
        Assert.NotNull(storedManagerAfterRestart);
        var reconciliation = new BrowserSessionReconciliationService().Reconcile(storedManagerAfterRestart!, mismatchedRuntime);
        Assert.Equal(BrowserReconciliationKind.IDENTITY_MISMATCH, reconciliation.Outcome);
        Assert.Equal("RECOVERY_REQUIRED", h.Autopilot);
        Assert.True(h.SendGate.Snapshot.IsPaused);
        Assert.Contains("STARTUP_BROWSER_RECONCILIATION", h.SendGate.Snapshot.Reason, StringComparison.OrdinalIgnoreCase);

        var entersBeforeResume = h.Adapter.PhysicalEnterCount;
        var submitsBeforeResume = h.Adapter.SubmittedPrompts.Count;
        var resumeFailure = await Assert.ThrowsAsync<InvalidOperationException>(() => h.ResumeAsync());
        Assert.Contains("STARTUP_RECOVERY_REQUIRED", resumeFailure.Message, StringComparison.Ordinal);
        Assert.Equal("RECOVERY_REQUIRED", h.Autopilot);
        Assert.True(h.SendGate.Snapshot.IsPaused);
        Assert.Contains("\"paused\":true", (await h.Store.LoadCheckpointAsync($"autopilot-pause:{runId}"))!.Payload, StringComparison.OrdinalIgnoreCase);

        var managerBlocked = await h.AgentProvider.SendAsync(new AgentRequest(
            runId,
            h.ManagerAgentId,
            correctManagerConversation,
            DispatchId.New(),
            "manager must remain blocked by startup recovery",
            "recovery-fence-manager-hash",
            null,
            null,
            null,
            mismatchedRuntime.ProviderConversationIdentity));
        Assert.False(managerBlocked.Accepted);

        workerRuntime = await h.RuntimeForAsync(workerId);
        durableWorker = await h.Store.LoadLogicalAgentAsync(workerId);
        Assert.NotNull(durableWorker?.CurrentConversationId);
        Assert.NotNull(durableWorker?.CurrentTaskId);
        var workerBlocked = await h.AgentProvider.SendAsync(new AgentRequest(
            runId,
            workerId,
            durableWorker!.CurrentConversationId!.Value,
            DispatchId.New(),
            "worker must remain blocked by startup recovery",
            "recovery-fence-worker-hash",
            durableWorker.WorkerSlotId,
            durableWorker.CurrentTaskId,
            h.CurrentWave?.Id,
            workerRuntime.ProviderConversationIdentity));
        Assert.False(workerBlocked.Accepted);
        Assert.Equal(entersBeforeResume, h.Adapter.PhysicalEnterCount);
        Assert.Equal(submitsBeforeResume, h.Adapter.SubmittedPrompts.Count);

        await ((IBrowserRuntimeRegistry)h.Store).UpsertAsync(mismatchedRuntime with { ConversationIdentity = correctManagerConversation.ToString() });
        var repaired = await h.RuntimeForAsync(h.ManagerAgentId);
        Assert.Equal(BrowserReconciliationKind.MATCHED, new BrowserSessionReconciliationService().Reconcile(storedManagerAfterRestart!, repaired).Outcome);

        await h.ForceInterruptedRestartAsync();
        Assert.Equal(runId, h.Run.Id);
        Assert.Equal("PAUSED", h.Autopilot);
        Assert.True(h.SendGate.Snapshot.IsPaused);
        Assert.Contains("\"paused\":true", (await h.Store.LoadCheckpointAsync($"autopilot-pause:{runId}"))!.Payload, StringComparison.OrdinalIgnoreCase);

        var managerAfterRecovery = await h.RuntimeForAsync(h.ManagerAgentId);
        var managerLogicalAfterRecovery = await h.Store.LoadLogicalAgentAsync(h.ManagerAgentId);
        Assert.NotNull(managerLogicalAfterRecovery);
        Assert.Equal(BrowserReconciliationKind.MATCHED, new BrowserSessionReconciliationService().Reconcile(managerLogicalAfterRecovery!, managerAfterRecovery).Outcome);

        await h.ResumeAsync();
        Assert.False(h.SendGate.Snapshot.IsPaused);
        Assert.NotEqual("RECOVERY_REQUIRED", h.Autopilot);
        Assert.Contains("\"paused\":false", (await h.Store.LoadCheckpointAsync($"autopilot-pause:{runId}"))!.Payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Offline_global_pause_survives_a_real_production_host_restart()
    {
        await using var h = await ProductionRuntimeAcceptanceHarness.CreateAsync();
        await h.SelectProjectAsync();
        await h.ConnectManagerAsync();
        await h.StartManagerAsync();
        var runId = h.Run.Id;
        var runtime = await h.RuntimeForAsync(h.ManagerAgentId);
        h.Adapter.SetSemantic(runtime.RuntimeId, ProductionRuntimeAcceptanceHarness.ScriptedBrowserAdapter.SemanticReady(health: PageHealth.Offline));
        await h.ReconcileAsync();
        Assert.True(h.SendGate.Snapshot.IsPaused);
        await h.ForceInterruptedRestartAsync();
        Assert.Equal(runId, h.Run.Id);
        Assert.True(h.SendGate.Snapshot.IsPaused);
        Assert.Contains("OFFLINE", (await h.Store.LoadCheckpointAsync($"runtime-health:{runId}"))!.Payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Durable_stagnation_counter_survives_restart_and_reaches_finite_auto_stop()
    {
        await using var h = await ProductionRuntimeAcceptanceHarness.CreateAsync();
        await h.SelectProjectAsync();
        var runId = h.Run.Id;
        await h.RecordRuntimeLoopErrorAsync("same governed failure");
        await h.RecordRuntimeLoopErrorAsync("same governed failure");
        Assert.NotEqual(ProjectRunState.StalledAutoStopped, h.Run.State);
        await h.ForceInterruptedRestartAsync();
        Assert.Equal(runId, h.Run.Id);
        await h.RecordRuntimeLoopErrorAsync("same governed failure");
        Assert.Equal(ProjectRunState.StalledAutoStopped, h.Run.State);
        Assert.NotNull(await h.Store.LoadCheckpointAsync($"loop-guard:{runId}"));
    }

    [Theory]
    [InlineData("NO_FRESH_EVIDENCE")]
    [InlineData("STALE_CI")]
    [InlineData("FAILING_CI")]
    [InlineData("MISSING_TEST_FAMILY")]
    [InlineData("STALE_GITHUB_HEAD")]
    public async Task Closure_never_reaches_100_without_fresh_authoritative_exact_head_evidence(string scenario)
    {
        await using var h = await ProductionRuntimeAcceptanceHarness.CreateAsync();
        await h.SelectProjectAsync();
        h.MakeCanonicalTaskTerminal();
        var closure = h.Run with
        {
            State = ProjectRunState.ClosureMode,
            ManagerEstimate = new ManagerEstimate(100m),
            VerifiedCompletion = new VerifiedCompletion(99m),
            CompletionMode = ProjectCompletionMode.ClosureMode
        };
        ProductionRuntimeAcceptanceHarness.SetField(h.Host, "_run", closure);
        await h.Store.SaveProjectRunAsync(closure);
        await h.Store.SaveCheckpointAsync(new DurableCheckpoint($"final-verification-request:{closure.Id}", closure.Id.ToString(), "final-verification-request-v1", "{}", DateTimeOffset.UtcNow));

        var good = ProductionRuntimeAcceptanceHarness.BuildBaseline(h.Route);
        switch (scenario)
        {
            case "NO_FRESH_EVIDENCE":
                h.Baseline.Status = ExternalReadStatus.TemporaryFailure;
                break;
            case "STALE_CI":
                h.SetBaseline(good with { Freshness = EvidenceFreshness.Stale, CapturedAt = DateTimeOffset.UtcNow.AddHours(-2) });
                break;
            case "FAILING_CI":
                h.SetBaseline(good with { Checks = new GitHubCheckSummary(good.Repository, good.DefaultHeadSha, "failure", [new GitHubCheckSnapshot("runtime-e2e", "completed", "failure", null)]) });
                break;
            case "MISSING_TEST_FAMILY":
                h.SetBaseline(good with { CanonicalTasks = Array.Empty<CanonicalTaskSnapshot>() });
                break;
            case "STALE_GITHUB_HEAD":
                h.SetBaseline(good with
                {
                    DefaultHeadSha = "2222222222222222222222222222222222222222",
                    Checks = new GitHubCheckSummary(good.Repository, ProductionRuntimeAcceptanceHarness.ExactHead, "success", [new GitHubCheckSnapshot("runtime-e2e", "completed", "success", null)])
                });
                break;
        }

        var verified = await h.RunIndependentFinalVerificationAsync();
        Assert.False(verified);
        Assert.True(h.Run.VerifiedCompletion.Percent <= 99m);
        Assert.NotEqual(ProjectCompletionMode.VerifiedComplete, h.Run.CompletionMode);
    }
}
