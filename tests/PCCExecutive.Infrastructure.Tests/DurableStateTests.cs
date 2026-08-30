using PCCExecutive.Browser;
using PCCExecutive.Domain;
using PCCExecutive.Infrastructure;
using Xunit;

namespace PCCExecutive.Infrastructure.Tests;

public sealed class DurableStateTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pcc-executive-tests", Guid.NewGuid().ToString("N"));

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Migration_and_core_state_survive_reopen()
    {
        var path = Path.Combine(_root, "state.db");
        var run = new ProjectRun(ProjectRunId.New(), ProjectId.New(), ProjectRunState.ManagerPlanning, DateTimeOffset.UtcNow, new ManagerEstimate(25), new VerifiedCompletion(10), ProjectCompletionMode.Active);
        var wave = new Wave(WaveId.New(), run.Id, 1, WaveState.Planned, Array.Empty<TaskId>(), DateTimeOffset.UtcNow);
        var task = new WorkerTask(TaskId.New(), "Persist me", TaskScope.Create("owner/repo", paths: ["src"]), new HashSet<TaskId>(), ["stored"], TaskState.Ready, "fp-1");
        var agent = new LogicalAgentSession(LogicalAgentId.New(), run.Id, AgentRole.Manager, null, task.Id, null, LogicalSessionState.Ready);
        var conversation = new PCCExecutive.Domain.Conversation(ConversationId.New(), agent.Id, 1, AgentProviderKind.BrowserChat, "provider-1", "https://chatgpt.com/c/test", ConversationState.Active, DateTimeOffset.UtcNow, null, null, null, 1, 100, null, null);
        var dispatch = new PCCExecutive.Domain.Dispatch(PCCExecutive.Domain.DispatchId.New(), run.Id, wave.Id, task.Id, agent.Id, conversation.Id, "hash", DateTimeOffset.UtcNow, PCCExecutive.Domain.DispatchState.SUBMITTED_UNKNOWN, DateTimeOffset.UtcNow, null, null, null, "uncertain");
        var evidence = new EvidenceRecord(EvidenceId.New(), run.Id, task.Id, "HEAD", "github", "fingerprint", "abc", DateTimeOffset.UtcNow);
        var attention = new AttentionRequest(AttentionRequestId.New(), run.Id, AttentionState.Open, "login", "Login required", "Sign in", "browser", false, DateTimeOffset.UtcNow);

        await using (var store = new SqliteStateStore(path))
        {
            await store.InitializeAsync();
            Assert.Equal(1, await store.GetSchemaVersionAsync());
            await store.SaveProjectRunAsync(run);
            await store.SaveWaveAsync(wave);
            await store.SaveTaskAsync(task, run.Id);
            await store.SaveLogicalAgentAsync(agent);
            await store.SaveConversationAsync(conversation, run.Id);
            await store.SaveDispatchAsync(dispatch);
            await store.SaveEvidenceAsync(evidence);
            await store.SaveAttentionAsync(attention);
            await store.SaveSettingsAsync(new PccExecutiveSettings());
        }

        await using (var reopened = new SqliteStateStore(path))
        {
            await reopened.InitializeAsync();
            Assert.Equal(run, await reopened.LoadProjectRunAsync(run.Id));
            var loadedWave = await reopened.LoadWaveAsync(wave.Id);
            Assert.NotNull(loadedWave);
            Assert.Equal(wave.Id, loadedWave!.Id);
            Assert.Equal(wave.ProjectRunId, loadedWave.ProjectRunId);
            Assert.Equal(wave.Sequence, loadedWave.Sequence);
            Assert.Equal(wave.State, loadedWave.State);
            Assert.Equal(wave.TaskIds.ToArray(), loadedWave.TaskIds.ToArray());
            Assert.Equal(wave.CreatedAt, loadedWave.CreatedAt);
            Assert.Equal(task.Id, (await reopened.LoadTaskAsync(task.Id))?.Id);
            Assert.Equal(agent, await reopened.LoadLogicalAgentAsync(agent.Id));
            Assert.Equal(conversation, await reopened.LoadConversationAsync(conversation.Id));
            Assert.Equal(PCCExecutive.Domain.DispatchState.SUBMITTED_UNKNOWN, (await reopened.LoadDispatchAsync(dispatch.Id))?.State);
            Assert.Equal(evidence, await reopened.LoadEvidenceAsync(evidence.Id));
            Assert.Equal(attention, await reopened.LoadAttentionAsync(attention.Id));
            Assert.Equal("BrowserChat", (await reopened.LoadSettingsAsync()).Provider);
        }
    }

    [Fact]
    public async Task Browser_registry_dispatch_and_rollover_are_durable()
    {
        var path = Path.Combine(_root, "browser.db");
        var now = DateTimeOffset.UtcNow;
        var runtime = new BrowserRuntimeRecord
        {
            RuntimeId = "runtime-1",
            ProjectRunId = "run-1",
            LogicalAgentId = "agent-1",
            WorkerSlotId = "worker-1",
            TaskId = "task-1",
            ProfilePath = Path.Combine(_root, "profile"),
            CreatedByPcc = true,
            AdoptedExplicitly = false,
            Visibility = BrowserVisibility.Hidden,
            State = BrowserSessionState.Ready,
            LastHeartbeatAt = now,
            LastActivityAt = now,
            OwnershipNonce = "nonce"
        };
        var predecessor = new ConversationRecord
        {
            ConversationId = "conversation-1",
            LogicalAgentId = "agent-1",
            ProjectRunId = "run-1",
            Sequence = 1,
            UrlOrProviderIdentity = "provider-1",
            CreatedAt = now,
            State = ConversationLifecycleState.Active
        };
        var successor = predecessor with
        {
            ConversationId = "conversation-2",
            Sequence = 2,
            PredecessorConversationId = predecessor.ConversationId,
            UrlOrProviderIdentity = "provider-2"
        };

        await using (var store = new SqliteStateStore(path))
        {
            await store.InitializeAsync();
            var registry = (IBrowserRuntimeRegistry)store;
            await registry.UpsertAsync(runtime);
            var reservation = await store.ReserveAsync("dispatch-1", "hash-1");
            Assert.Equal(DispatchReservationStatus.New, reservation.Status);
            await store.UpdateAsync("dispatch-1", PCCExecutive.Browser.DispatchState.SubmittedUnknown, "unknown-send");
            var checkpoint = await store.CreateCheckpointAsync(predecessor);
            await store.SaveCandidateAsync(successor, checkpoint);
            await store.CommitRolloverAsync(predecessor with { State = ConversationLifecycleState.Archived, SuccessorConversationId = successor.ConversationId }, successor, checkpoint);
            Assert.NotNull(await store.LoadCheckpointAsync(checkpoint));
        }

        await using (var reopened = new SqliteStateStore(path))
        {
            await reopened.InitializeAsync();
            Assert.Equal(runtime, await reopened.GetBrowserRuntimeAsync(runtime.RuntimeId));
            var dispatch = await reopened.GetDispatchLedgerAsync("dispatch-1");
            Assert.NotNull(dispatch);
            Assert.Equal(PCCExecutive.Browser.DispatchState.SubmittedUnknown, dispatch!.State);
            var duplicate = await reopened.ReserveAsync("dispatch-1", "hash-1");
            Assert.Equal(DispatchReservationStatus.DuplicateBlocked, duplicate.Status);
        }
    }

    [Fact]
    public void Project_lock_excludes_second_controller()
    {
        var isolatedProject = $"PCCEXECUTIVE-LOCK-TEST-{Guid.NewGuid():N}";
        using var first = ProjectRunLock.TryAcquire(isolatedProject);
        using var second = ProjectRunLock.TryAcquire(isolatedProject);
        Assert.True(first.IsOwned);
        Assert.False(second.IsOwned);
    }

    [Fact]
    public async Task Project_lock_can_be_released_on_a_different_thread_and_reacquired()
    {
        var isolatedProject = $"PCCEXECUTIVE-LOCK-ASYNC-{Guid.NewGuid():N}";
        var first = ProjectRunLock.TryAcquire(isolatedProject);
        Assert.True(first.IsOwned);
        var acquireThread = Environment.CurrentManagedThreadId;

        var releaseThread = await Task.Factory.StartNew(
            () =>
            {
                var thread = Environment.CurrentManagedThreadId;
                first.Dispose();
                first.Dispose();
                return thread;
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        Assert.NotEqual(acquireThread, releaseThread);
        Assert.False(first.IsOwned);
        using var reacquired = ProjectRunLock.TryAcquire(isolatedProject);
        Assert.True(reacquired.IsOwned);
    }

    [Fact]
    public async Task Browser_conversation_history_is_durable_and_project_scoped()
    {
        var path = Path.Combine(_root, "conversation-history.db");
        await using var store = new SqliteStateStore(path);
        await store.InitializeAsync();
        var record = new ConversationRecord
        {
            ConversationId = "conversation-1",
            LogicalAgentId = "agent-1",
            ProjectRunId = "run-1",
            Sequence = 1,
            UrlOrProviderIdentity = "provider-conversation-1",
            CreatedAt = DateTimeOffset.UtcNow,
            State = ConversationLifecycleState.Active
        };

        await store.SaveBrowserConversationAsync(record);
        var history = await store.ListBrowserConversationsAsync();

        Assert.Single(history);
        Assert.Equal(record, history[0]);
    }
}
