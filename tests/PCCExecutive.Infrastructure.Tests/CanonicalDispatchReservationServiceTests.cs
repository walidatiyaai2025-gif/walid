using PCCExecutive.Application;
using PCCExecutive.Browser;
using PCCExecutive.Domain;
using PCCExecutive.Infrastructure;
using Xunit;

namespace PCCExecutive.Infrastructure.Tests;

public sealed class CanonicalDispatchReservationServiceTests
{
    [Fact]
    public async Task Restart_submitted_unknown_recovers_same_dispatch_id_without_replacement()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pcc-dispatch-{Guid.NewGuid():N}.db");
        try
        {
            var run = ProjectRunId.New(); var agent = LogicalAgentId.New(); var task = TaskId.New(); var wave = WaveId.New(); var conversation = ConversationId.New();
            var correlation = new DurableDispatchCorrelation(run, agent, new WorkerSlotId(1), task, wave, conversation, "NEW", "hash");
            DispatchId id;
            await using (var store = new SqliteStateStore(path))
            {
                await store.InitializeAsync();
                var first = await new CanonicalDispatchReservationService(store).ReserveOrRecoverAsync(correlation);
                id = first.Id;
                await store.ReserveAsync(id.ToString(), first.ContentHash);
                await store.UpdateAsync(id.ToString(), PCCExecutive.Browser.DispatchState.Submitting, "crash-after-enter");
                await store.SaveDispatchAsync(first with { State = PCCExecutive.Domain.DispatchState.SUBMITTED_UNKNOWN });
            }
            await using (var reopened = new SqliteStateStore(path))
            {
                await reopened.InitializeAsync();
                var recovered = await new CanonicalDispatchReservationService(reopened).ReserveOrRecoverAsync(correlation with { ProviderConversationId = "provider-established" });
                Assert.Equal(id, recovered.Id);
                Assert.Equal(PCCExecutive.Domain.DispatchState.SUBMITTED_UNKNOWN, recovered.State);
                Assert.Single(await new AutonomousDispatchJournal(reopened).ListAsync(run), x => x.ContentHash == "hash");
            }
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public async Task Submitted_unknown_restart_never_calls_browser_submit_and_returns_original_dispatch_id()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pcc-dispatch-submit-{Guid.NewGuid():N}.db");
        try
        {
            var run = ProjectRunId.New();
            var agent = LogicalAgentId.New();
            var task = TaskId.New();
            var wave = WaveId.New();
            var conversation = ConversationId.New();
            var slot = new WorkerSlotId(1);
            var correlation = new DurableDispatchCorrelation(run, agent, slot, task, wave, conversation, "NEW", "hash");

            await using var store = new SqliteStateStore(path);
            await store.InitializeAsync();
            await ((IBrowserRuntimeRegistry)store).UpsertAsync(new BrowserRuntimeRecord
            {
                RuntimeId = "runtime-1",
                ProjectRunId = run.ToString(),
                LogicalAgentId = agent.ToString(),
                WorkerSlotId = slot.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                TaskId = task.ToString(),
                ConversationIdentity = conversation.ToString(),
                ProviderConversationIdentity = "provider-established",
                ProfilePath = "profile",
                CreatedByPcc = true,
                OwnershipNonce = "nonce",
                State = BrowserSessionState.Ready,
                LastHeartbeatAt = DateTimeOffset.UtcNow,
                LastActivityAt = DateTimeOffset.UtcNow
            });

            var first = await new CanonicalDispatchReservationService(store).ReserveOrRecoverAsync(correlation);
            await store.ReserveAsync(first.Id.ToString(), first.ContentHash);
            await store.UpdateAsync(first.Id.ToString(), PCCExecutive.Browser.DispatchState.Submitting, "crash-after-enter");
            await store.SaveDispatchAsync(first with { State = PCCExecutive.Domain.DispatchState.SUBMITTED_UNKNOWN });

            var adapter = new CountingAdapter();
            var ownership = new ProvenOwnership();
            var browser = new PCCExecutive.Browser.BrowserChatProvider(store, adapter, store, new WrongChatGuard(), new GlobalBrowserSendGate(), ownership);
            var provider = new BrowserAgentProviderAdapter(store, browser, ownership);
            var request = new AgentRequest(run, agent, conversation, DispatchId.New(), "do work", "hash", slot, task, wave, "provider-established");

            var result = await provider.SendAsync(request);

            Assert.True(result.IsUncertain);
            Assert.Equal("SUBMITTED_UNKNOWN", result.ErrorCode);
            Assert.Equal(first.Id, result.DispatchId);
            Assert.Equal(0, adapter.SubmitCalls);
            Assert.Single(await new AutonomousDispatchJournal(store).ListAsync(run), x => x.ContentHash == "hash");
        }
        finally { try { File.Delete(path); } catch { } }
    }

    private sealed class ProvenOwnership : IOwnershipProofService
    {
        public Task<OwnershipProof> ProveAsync(BrowserRuntimeRecord runtime, CancellationToken cancellationToken = default) =>
            Task.FromResult(OwnershipProof.Proven(runtime.RuntimeId));
    }

    private sealed class CountingAdapter : IChatGptBrowserAdapter
    {
        public int SubmitCalls { get; private set; }
        public string AdapterVersion => "canonical-dispatch-test";

        public Task<ChatGptSemanticSnapshot> InspectAsync(BrowserRuntimeRecord runtime, BrowserDispatchExpectation expectation, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatGptSemanticSnapshot(
                SemanticDetection<InputState>.Create(InputState.Ready, 1, AdapterVersion, "ready"),
                SemanticDetection<GenerationState>.Create(GenerationState.Idle, 1, AdapterVersion, "idle"),
                SemanticDetection<AuthState>.Create(AuthState.Authenticated, 1, AdapterVersion, "auth"),
                SemanticDetection<ConversationMatch>.Create(ConversationMatch.Match, 1, AdapterVersion, "match"),
                SemanticDetection<PageHealth>.Create(PageHealth.Healthy, 1, AdapterVersion, "healthy"),
                ResponseCompleteness.None,
                0,
                null,
                DateTimeOffset.UtcNow,
                AdapterVersion));

        public Task<AdapterSubmissionResult> SubmitAsync(BrowserRuntimeRecord runtime, BrowserDispatchExpectation expectation, string prompt, CancellationToken cancellationToken = default)
        {
            SubmitCalls++;
            return Task.FromResult(new AdapterSubmissionResult(true, true, false, "SUBMITTED", new[] { "submitted" }));
        }
    }
}
