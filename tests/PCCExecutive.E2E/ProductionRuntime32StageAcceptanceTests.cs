using System.Security.Cryptography;
using System.Text;
using PCCExecutive.Application;
using PCCExecutive.Browser;
using PCCExecutive.Domain;
using PCCExecutive.Infrastructure;
using Xunit;

namespace PCCExecutive.E2E;

public sealed class ProductionRuntime32StageAcceptanceTests
{
    [Fact]
    public async Task Production_runtime_host_proves_the_ordered_32_stage_path()
    {
        await using var h = await ProductionRuntimeAcceptanceHarness.CreateAsync();
        var stages = new List<int>();

        Assert.True(File.Exists(h.DatabasePath));
        Assert.Equal(1, await h.Store.GetSchemaVersionAsync());
        Stage(1);

        Assert.IsType<PCCExecutive.App.Presentation.PccExecutiveRuntimeHost>(h.Host);
        Assert.True(h.Host.Snapshot.GatewayBound);
        Stage(2);

        await h.SelectProjectAsync();
        Assert.True(h.Pcc.ResolveCalls >= 1);
        Assert.Equal(ProductionRuntimeAcceptanceHarness.ProjectControlId, h.Host.Snapshot.Projects.Single().Id);
        Stage(3);

        var runId = h.Run.Id;
        Assert.Equal(ProjectRunState.ManagerPlanning, h.Run.State);
        Assert.Equal(runId, (await h.Store.LoadProjectRunAsync(runId))!.Id);
        Stage(4);

        using (var secondLock = ProjectRunLock.TryAcquire(h.Route.RoutingIdentity))
            Assert.False(secondLock.IsOwned);
        Stage(5);

        var managerBefore = await h.Store.LoadLogicalAgentAsync(h.ManagerAgentId);
        Assert.NotNull(managerBefore);
        Assert.Equal(AgentRole.Manager, managerBefore!.Role);
        Stage(6);

        var workerIds = h.WorkerAgentIds;
        Assert.Equal(5, workerIds.Length);
        var workerSessionsBefore = await Task.WhenAll(workerIds.Select(id => h.Store.LoadLogicalAgentAsync(id)));
        Assert.All(workerSessionsBefore, session => Assert.NotNull(session));
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, workerSessionsBefore.Select(x => x!.WorkerSlotId!.Value.Value).OrderBy(x => x).ToArray());
        Stage(7);

        await h.ConnectManagerAsync();
        var autopilotCancellation = ProductionRuntimeAcceptanceHarness.GetField<CancellationTokenSource>(h.Host, "_autopilotCancellation");
        autopilotCancellation.Cancel();
        await h.StartManagerAsync();
        var autopilotTask = ProductionRuntimeAcceptanceHarness.GetField<Task?>(h.Host, "_autopilotTask");
        if (autopilotTask is not null) await autopilotTask;
        var managerRuntime = await h.RuntimeForAsync(h.ManagerAgentId);
        var managerPrompt = h.Adapter.SubmittedPrompts.Last(x => x.RuntimeId == managerRuntime.RuntimeId).Prompt;
        Assert.Contains("PCC_SOURCE_SHA:", managerPrompt, StringComparison.Ordinal);
        Assert.Contains($"DEFAULT_HEAD: {ProductionRuntimeAcceptanceHarness.ExactHead}", managerPrompt, StringComparison.Ordinal);
        Assert.Contains($"PROJECT_RUN: {runId}", managerPrompt, StringComparison.Ordinal);
        Assert.True(h.Baseline.Calls >= 1);
        Stage(8);

        var managerDispatches = await new AutonomousDispatchJournal(h.Store).ListAsync(runId);
        var managerDispatch = Assert.Single(managerDispatches, x => x.LogicalAgentId == h.ManagerAgentId);
        var managerLogical = await h.Store.LoadLogicalAgentAsync(h.ManagerAgentId);
        var managerConversationRecord = await h.ActiveBrowserConversationAsync(h.ManagerAgentId);
        Assert.NotNull(managerLogical?.CurrentConversationId);
        Assert.True(StringComparer.Ordinal.Equals(managerRuntime.ConversationIdentity, managerLogical!.CurrentConversationId!.Value.ToString()));
        Assert.True(StringComparer.Ordinal.Equals(managerRuntime.ConversationIdentity, managerConversationRecord.ConversationId));
        Assert.True(StringComparer.Ordinal.Equals(managerRuntime.ConversationIdentity, managerDispatch.ConversationId.ToString()));
        Assert.Equal(1, h.Adapter.EnterCount(managerRuntime.RuntimeId));
        Stage(9);

        var tasks = Enumerable.Range(1, 5)
            .Select(i => ProductionRuntimeAcceptanceHarness.CreatePlannedTask($"Runtime worker task {i}", $"tests/runtime-final/task-{i}", i))
            .ToArray();
        var firstPlanJson = ProductionRuntimeAcceptanceHarness.PlanJson(h.Route, 70m, "CONTINUE", tasks);
        h.Adapter.SetSemantic(managerRuntime.RuntimeId,
            ProductionRuntimeAcceptanceHarness.ScriptedBrowserAdapter.SemanticReady(firstPlanJson, complete: true));
        var managerSemantic = await h.Adapter.InspectAsync(managerRuntime,
            new BrowserDispatchExpectation(runId.ToString(), h.ManagerAgentId.ToString(), managerRuntime.TaskId!, managerRuntime.ConversationIdentity!, managerRuntime.ProviderConversationIdentity!));
        Assert.Equal(ResponseCompleteness.Complete, managerSemantic.ResponseCompleteness);
        Stage(10);

        await h.ReconcileAsync();
        var planCheckpoint = await h.Store.LoadCheckpointAsync($"manager-plan:{runId}");
        Assert.NotNull(planCheckpoint);
        var parsed = new StructuredManagerPlanParser().Parse(planCheckpoint!.Payload);
        Assert.True(parsed.IsValid);
        Assert.Equal(5, parsed.Plan!.Tasks.Count);
        Stage(11);

        Assert.NotNull(h.CurrentWave);
        Assert.Equal(1, h.CurrentWave!.Sequence);
        Assert.Equal(WaveState.Ready, h.CurrentWave.State);
        Stage(12);

        var dependencyIssues = new DependencyValidator().Validate(h.RuntimeTasks, EmptyCompletedTaskIndex.Instance);
        Assert.DoesNotContain(dependencyIssues, x => x.Code.StartsWith("DEPENDENCY", StringComparison.Ordinal));
        Stage(13);

        var overlap = new ScopeOverlapDetector();
        for (var i = 0; i < h.RuntimeTasks.Count; i++)
            for (var j = i + 1; j < h.RuntimeTasks.Count; j++)
                Assert.False(overlap.Overlaps(h.RuntimeTasks[i].Scope, h.RuntimeTasks[j].Scope));
        Stage(14);

        Assert.Equal(5, h.Assignments.Count);
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, h.Assignments.Values.Select(x => x.Value).OrderBy(x => x).ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorkerSlotId(6));
        Stage(15);

        await h.StartDispatchAsync();
        Assert.Equal(WaveState.Running, h.CurrentWave!.State);
        Assert.Equal(ProjectRunState.WaveRunning, h.Run.State);
        Stage(16);

        var scheduler = new BrowserDispatchScheduler();
        var pacing = new DispatchSchedulerOptions(PCCExecutive.Browser.DispatchMode.AutomaticStaged, TimeSpan.FromSeconds(10), true, 5);
        var first = scheduler.Evaluate(ProductionRuntimeAcceptanceHarness.Now, null, 0, pacing, new GlobalBrowserSendGate().Snapshot);
        var slowed = scheduler.Evaluate(ProductionRuntimeAcceptanceHarness.Now.AddSeconds(19), ProductionRuntimeAcceptanceHarness.Now, 1, pacing, new GlobalBrowserSendGate().Snapshot, ChatGptResilienceState.Slow);
        var ready = scheduler.Evaluate(ProductionRuntimeAcceptanceHarness.Now.AddSeconds(20), ProductionRuntimeAcceptanceHarness.Now, 1, pacing, new GlobalBrowserSendGate().Snapshot, ChatGptResilienceState.Slow);
        Assert.True(first.MayDispatch);
        Assert.False(slowed.MayDispatch);
        Assert.True(ready.MayDispatch);
        Stage(17);

        var workerRuntimes = new List<BrowserRuntimeRecord>();
        for (var slotNumber = 1; slotNumber <= 5; slotNumber++)
        {
            var runtime = await h.RuntimeForAsync(workerIds[slotNumber - 1]);
            workerRuntimes.Add(runtime);
            var expectedTask = h.Assignments.Single(x => x.Value.Value == slotNumber).Key;
            Assert.Equal(slotNumber.ToString(), runtime.WorkerSlotId);
            Assert.Equal(expectedTask.ToString(), runtime.TaskId);
            var workerLogical = await h.Store.LoadLogicalAgentAsync(workerIds[slotNumber - 1]);
            var workerConversationRecord = await h.ActiveBrowserConversationAsync(workerIds[slotNumber - 1]);
            Assert.NotNull(workerLogical?.CurrentConversationId);
            Assert.True(StringComparer.Ordinal.Equals(runtime.ConversationIdentity, workerLogical!.CurrentConversationId!.Value.ToString()));
            Assert.True(StringComparer.Ordinal.Equals(runtime.ConversationIdentity, workerConversationRecord.ConversationId));
            Assert.Equal(1, h.Adapter.EnterCount(runtime.RuntimeId));
        }
        Stage(18);

        var physicalBeforeWrongChat = h.Adapter.PhysicalEnterCount;
        var wrong = new WrongChatGuard().Evaluate(workerRuntimes[0],
            new BrowserDispatchExpectation(runId.ToString(), workerIds[0].ToString(), workerRuntimes[0].TaskId!, workerRuntimes[0].ConversationIdentity!, "provider-wrong", "1"),
            ProductionRuntimeAcceptanceHarness.ScriptedBrowserAdapter.SemanticReady());
        Assert.False(wrong.MaySend);
        Assert.Equal(physicalBeforeWrongChat, h.Adapter.PhysicalEnterCount);
        Stage(19);

        for (var slotNumber = 1; slotNumber <= 5; slotNumber++)
        {
            var taskId = h.Assignments.Single(x => x.Value.Value == slotNumber).Key;
            var task = h.RuntimeTasks.Single(x => x.Id == taskId);
            h.Adapter.SetSemantic(workerRuntimes[slotNumber - 1].RuntimeId,
                ProductionRuntimeAcceptanceHarness.ScriptedBrowserAdapter.SemanticReady(
                    ProductionRuntimeAcceptanceHarness.Handoff(taskId, new WorkerSlotId(slotNumber), task.Scope.Paths.Single()), complete: true));
        }
        Stage(20);

        for (var slotNumber = 1; slotNumber <= 5; slotNumber++)
        {
            var logical = await h.Store.LoadLogicalAgentAsync(workerIds[slotNumber - 1]);
            Assert.NotNull(logical);
            Assert.Equal(slotNumber, logical!.WorkerSlotId!.Value.Value);
            Assert.Equal(workerRuntimes[slotNumber - 1].ConversationIdentity, logical.CurrentConversationId!.Value.ToString());
            Assert.Equal(workerRuntimes[slotNumber - 1].TaskId, logical.CurrentTaskId!.Value.ToString());
        }
        Stage(21);

        await h.ReconcileAsync();
        Assert.All(h.RuntimeTasks, x => Assert.Equal(TaskState.Completed, x.State));
        Stage(22);

        Assert.True(h.Baseline.Calls >= 2);
        Assert.Equal(ProductionRuntimeAcceptanceHarness.ExactHead, h.Baseline.Current.DefaultHeadSha);
        Stage(23);

        Assert.True(h.Run.VerifiedCompletion.Percent > 0m);
        Assert.True(h.Run.VerifiedCompletion.Percent < 100m);
        Assert.NotEqual(ProjectCompletionMode.VerifiedComplete, h.Run.CompletionMode);
        Stage(24);

        Assert.Equal(WaveState.Completed, h.CurrentWave!.State);
        Assert.Contains("reconciled against live evidence", h.Host.Snapshot.LatestManagerHandoff, StringComparison.OrdinalIgnoreCase);
        Stage(25);

        var orchestration = ProductionRuntimeAcceptanceHarness.GetField<CrashConsistentOrchestrationStore>(h.Host, "_orchestrationStore");
        var reviewState = await orchestration.LoadAsync(runId);
        Assert.NotNull(reviewState?.ManagerReview);
        Assert.Equal(5, reviewState!.ManagerReview!.TaskResults.Count);
        Stage(26);

        managerRuntime = await h.RuntimeForAsync(h.ManagerAgentId);
        var secondTask = ProductionRuntimeAcceptanceHarness.CreatePlannedTask("Second wave verification repair", "tests/runtime-final/second-wave");
        h.Adapter.SetSemantic(managerRuntime.RuntimeId,
            ProductionRuntimeAcceptanceHarness.ScriptedBrowserAdapter.SemanticReady(
                ProductionRuntimeAcceptanceHarness.PlanJson(h.Route, 85m, "CONTINUE", secondTask), complete: true));
        await h.ReconcileAsync();
        Assert.Equal(2, h.CurrentWave!.Sequence);
        Assert.Equal(WaveState.Ready, h.CurrentWave.State);
        Assert.Single(h.Assignments);
        Stage(27);

        Assert.Equal(1, h.Assignments.Single().Value.Value);
        var workerOneAgent = workerIds[0];
        h.Adapter.QueueSubmission(false, true, "SUBMITTED_UNKNOWN");
        await h.StartDispatchAsync();
        var secondWorkerRuntime = await h.RuntimeForAsync(workerOneAgent);
        Assert.Equal("1", secondWorkerRuntime.WorkerSlotId);
        Assert.Equal(secondTask.Id.ToString(), secondWorkerRuntime.TaskId);
        Assert.Equal(workerOneAgent, h.WorkerAgentIds[0]);
        Stage(28);

        var preRestartRun = h.Run.Id;
        var preRestartWave = h.CurrentWave!.Id;
        var preRestartAssignment = h.Assignments.Single();
        var preRestartAgents = new[] { h.ManagerAgentId }.Concat(h.WorkerAgentIds).ToArray();
        var preRestartIdentity = new Dictionary<LogicalAgentId, (string? Slot, string? Task, string Conversation, string? Provider)>();
        foreach (var agent in preRestartAgents)
        {
            var runtime = await h.RuntimeForAsync(agent);
            var logical = await h.Store.LoadLogicalAgentAsync(agent);
            Assert.NotNull(logical?.CurrentConversationId);
            Assert.True(StringComparer.Ordinal.Equals(runtime.ConversationIdentity, logical!.CurrentConversationId!.Value.ToString()));
            preRestartIdentity[agent] = (runtime.WorkerSlotId, runtime.TaskId, runtime.ConversationIdentity!, runtime.ProviderConversationIdentity);
        }
        await h.PauseAsync();
        Assert.True(h.SendGate.Snapshot.IsPaused);
        await h.ForceInterruptedRestartAsync();
        Assert.Equal(preRestartRun, h.Run.Id);
        Assert.Equal(preRestartWave, h.CurrentWave!.Id);
        Assert.Equal(preRestartAssignment, h.Assignments.Single());
        Assert.Equal("PAUSED", h.Autopilot);
        Assert.True(h.SendGate.Snapshot.IsPaused);
        foreach (var agent in preRestartAgents)
        {
            var logical = await h.Store.LoadLogicalAgentAsync(agent);
            var runtime = await h.RuntimeForAsync(agent);
            Assert.NotNull(logical);
            var reconciliation = new BrowserSessionReconciliationService().Reconcile(logical!, runtime);
            Assert.Equal(BrowserReconciliationKind.MATCHED, reconciliation.Outcome);
            var before = preRestartIdentity[agent];
            Assert.True(StringComparer.Ordinal.Equals(before.Slot, runtime.WorkerSlotId));
            Assert.True(StringComparer.Ordinal.Equals(before.Task, runtime.TaskId));
            Assert.True(StringComparer.Ordinal.Equals(before.Conversation, runtime.ConversationIdentity));
            Assert.True(StringComparer.Ordinal.Equals(before.Provider, runtime.ProviderConversationIdentity));
        }
        await h.ResumeAsync();
        Assert.False(h.SendGate.Snapshot.IsPaused);
        Stage(29);

        var journal = new AutonomousDispatchJournal(h.Store);
        var durableDispatches = await journal.ListAsync(h.Run.Id);
        var uncertain = Assert.Single(durableDispatches, x => x.TaskId == secondTask.Id);
        Assert.Equal(PCCExecutive.Domain.DispatchState.SUBMITTED_UNKNOWN, uncertain.State);
        var submitted = h.Adapter.SubmittedPrompts.Last(x => x.Expectation.TaskId == secondTask.Id.ToString());
        var retryRequest = new AgentRequest(
            uncertain.ProjectRunId, uncertain.LogicalAgentId, uncertain.ConversationId, uncertain.Id,
            submitted.Prompt, uncertain.ContentHash, uncertain.WorkerSlotId, uncertain.TaskId,
            uncertain.WaveId, uncertain.ProviderConversationId);
        var enterBeforeBlockedRetry = h.Adapter.PhysicalEnterCount;
        var blockedRetry = await h.AgentProvider.SendAsync(retryRequest);
        Assert.True(blockedRetry.IsUncertain);
        Assert.Equal(enterBeforeBlockedRetry, h.Adapter.PhysicalEnterCount);

        await h.Store.UpdateAsync(uncertain.Id.ToString(), PCCExecutive.Browser.DispatchState.SafeRetry,
            "PROVEN_ABSENCE_AFTER_SEMANTIC_RECONCILIATION");
        var safeRetry = await h.AgentProvider.SendAsync(retryRequest);
        Assert.True(safeRetry.Accepted);
        Assert.Equal(enterBeforeBlockedRetry + 1, h.Adapter.PhysicalEnterCount);

        secondWorkerRuntime = await h.RuntimeForAsync(workerOneAgent);
        h.Adapter.SetSemantic(secondWorkerRuntime.RuntimeId,
            ProductionRuntimeAcceptanceHarness.ScriptedBrowserAdapter.SemanticReady(health: PageHealth.RateLimited));
        await h.ReconcileAsync();
        Assert.True(h.SendGate.Snapshot.IsPaused);
        await h.ForceInterruptedRestartAsync();
        Assert.True(h.SendGate.Snapshot.IsPaused);
        Assert.NotNull(await h.Store.LoadCheckpointAsync($"runtime-health:{h.Run.Id}"));

        ProductionRuntimeAcceptanceHarness.SetField(h.SendGate, "_snapshot",
            new GlobalSendGateSnapshot(true, "RATE_LIMITED", DateTimeOffset.UtcNow.AddMinutes(-2), DateTimeOffset.UtcNow.AddSeconds(-1)));
        secondWorkerRuntime = await h.RuntimeForAsync(workerOneAgent);
        h.Adapter.SetSemantic(secondWorkerRuntime.RuntimeId,
            ProductionRuntimeAcceptanceHarness.ScriptedBrowserAdapter.SemanticReady(health: PageHealth.RateLimited));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.ResumeAsync());
        Assert.True(h.SendGate.Snapshot.IsPaused);
        h.Adapter.SetSemantic(secondWorkerRuntime.RuntimeId, ProductionRuntimeAcceptanceHarness.ScriptedBrowserAdapter.SemanticReady());
        await h.ResumeAsync();
        Assert.False(h.SendGate.Snapshot.IsPaused);
        Stage(30);

        h.MakeCanonicalTaskTerminal();
        secondWorkerRuntime = await h.RuntimeForAsync(workerOneAgent);
        h.Adapter.SetSemantic(secondWorkerRuntime.RuntimeId,
            ProductionRuntimeAcceptanceHarness.ScriptedBrowserAdapter.SemanticReady(
                ProductionRuntimeAcceptanceHarness.Handoff(secondTask.Id, new WorkerSlotId(1), secondTask.Path), complete: true));
        await h.ReconcileAsync();
        Assert.Equal(ProjectCompletionMode.ClosureMode, h.Run.CompletionMode);
        Assert.True(h.Run.VerifiedCompletion.Percent <= 99m);

        managerRuntime = await h.RuntimeForAsync(h.ManagerAgentId);
        var managerPredecessor = await h.ActiveBrowserConversationAsync(h.ManagerAgentId);
        await h.InvokeGovernedRolloverAsync(managerRuntime, managerPredecessor, "MANAGER_CONTEXT_PRESSURE_ACCEPTANCE");
        var managerLineage = await h.BrowserConversationsAsync(h.ManagerAgentId);
        var managerSuccessor = Assert.Single(managerLineage, x => x.State == ConversationLifecycleState.Active);
        Assert.True(StringComparer.Ordinal.Equals(managerSuccessor.ConversationId, new ConversationId(Guid.Parse(managerSuccessor.ConversationId)).ToString()));
        Assert.Contains(managerLineage, x => x.ConversationId == managerPredecessor.ConversationId && x.State == ConversationLifecycleState.Archived);

        var workerPredecessor = await h.ActiveBrowserConversationAsync(workerOneAgent);
        secondWorkerRuntime = await h.RuntimeForAsync(workerOneAgent);
        await h.InvokeGovernedRolloverAsync(secondWorkerRuntime, workerPredecessor, "WORKER_CONTEXT_PRESSURE_ACCEPTANCE");
        var workerLineage = await h.BrowserConversationsAsync(workerOneAgent);
        var workerSuccessor = Assert.Single(workerLineage, x => x.State == ConversationLifecycleState.Active);
        Assert.True(StringComparer.Ordinal.Equals(workerSuccessor.ConversationId, new ConversationId(Guid.Parse(workerSuccessor.ConversationId)).ToString()));
        Assert.Contains(workerLineage, x => x.ConversationId == workerPredecessor.ConversationId && x.State == ConversationLifecycleState.Archived);

        var crashAgent = h.WorkerAgentIds[1];
        var crashRuntime = await h.RuntimeForAsync(crashAgent);
        var crashPredecessor = await h.ActiveBrowserConversationAsync(crashAgent);
        h.Adapter.QueueSubmission(false, true, "SUBMITTED_UNKNOWN");
        await h.InvokeGovernedRolloverAsync(crashRuntime, crashPredecessor, "CRASH_DURING_ROLLOVER_ACCEPTANCE");
        var beforeCrashLineage = await h.BrowserConversationsAsync(crashAgent);
        Assert.Contains(beforeCrashLineage, x => x.State == ConversationLifecycleState.Candidate);
        await h.ForceInterruptedRestartAsync();
        var recoveredCrashLineage = await h.BrowserConversationsAsync(crashAgent);
        Assert.Single(recoveredCrashLineage, x => x.State == ConversationLifecycleState.Active);
        Assert.Contains(recoveredCrashLineage, x => x.ConversationId == crashPredecessor.ConversationId && x.State == ConversationLifecycleState.Archived);

        var activeManager = await h.ActiveBrowserConversationAsync(h.ManagerAgentId);
        managerRuntime = await h.RuntimeForAsync(h.ManagerAgentId);
        var futurePrompt = "POST_ROLLOVER_FUTURE_MANAGER_SEND";
        var futureHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(futurePrompt))).ToLowerInvariant();
        var enterBeforeFuture = h.Adapter.PhysicalEnterCount;
        var futureResult = await h.AgentProvider.SendAsync(new AgentRequest(
            h.Run.Id, h.ManagerAgentId, new ConversationId(Guid.Parse(activeManager.ConversationId)), DispatchId.New(),
            futurePrompt, futureHash, ProviderConversationId: managerRuntime.ProviderConversationIdentity));
        Assert.True(futureResult.Accepted);
        Assert.Equal(enterBeforeFuture + 1, h.Adapter.PhysicalEnterCount);

        var retiredResult = await h.AgentProvider.SendAsync(new AgentRequest(
            h.Run.Id, h.ManagerAgentId, new ConversationId(Guid.Parse(managerPredecessor.ConversationId)), DispatchId.New(),
            "retired", "retired-hash", ProviderConversationId: managerRuntime.ProviderConversationIdentity));
        Assert.False(retiredResult.Accepted);
        Assert.Equal(enterBeforeFuture + 1, h.Adapter.PhysicalEnterCount);
        Stage(31);

        managerRuntime = await h.RuntimeForAsync(h.ManagerAgentId);
        h.Adapter.SetSemantic(managerRuntime.RuntimeId,
            ProductionRuntimeAcceptanceHarness.ScriptedBrowserAdapter.SemanticReady(
                ProductionRuntimeAcceptanceHarness.PlanJson(h.Route, 100m, "CLOSE"), complete: true));
        await h.ReconcileAsync();
        Assert.Equal(ProjectCompletionMode.ClosureMode, h.Run.CompletionMode);
        Assert.True(h.Run.VerifiedCompletion.Percent <= 99m);
        Assert.NotNull(await h.Store.LoadCheckpointAsync($"final-verification-request:{h.Run.Id}"));
        h.SetBaseline(ProductionRuntimeAcceptanceHarness.BuildBaseline(h.Route));
        Assert.True(await h.RunIndependentFinalVerificationAsync());
        Assert.Equal(100m, h.Run.VerifiedCompletion.Percent);
        Assert.Equal(ProjectCompletionMode.VerifiedComplete, h.Run.CompletionMode);
        Assert.Equal(ProjectRunState.VerifiedComplete, h.Run.State);
        Stage(32);

        Assert.Equal(Enumerable.Range(1, 32), stages);

        void Stage(int stage)
        {
            Assert.Equal(stages.Count + 1, stage);
            stages.Add(stage);
        }
    }

    private sealed class EmptyCompletedTaskIndex : ICompletedTaskIndex
    {
        internal static EmptyCompletedTaskIndex Instance { get; } = new();
        public bool IsCompleted(TaskId taskId) => false;
        public bool ContainsFingerprint(string fingerprint) => false;
    }
}
