using PCCExecutive.Browser;
using PCCExecutive.Domain;
using PCCExecutive.Infrastructure;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PCCExecutive.App.Presentation;

public sealed class AutonomousConversationRolloverRuntime : IAsyncDisposable
{
    private static readonly TimeSpan MonitorInterval = TimeSpan.FromSeconds(5);
    private readonly PccExecutiveRuntimeHost _host;
    private readonly SqliteStateStore _store;
    private readonly ConversationLifecycleManager _lifecycle;
    private readonly PreventiveRolloverPolicy _policy = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Task _loop;
    private readonly string _profileRoot;

    private AutonomousConversationRolloverRuntime(PccExecutiveRuntimeHost host)
    {
        _host = host;
        _store = PccHostRecoveryAccess.Store(_host);
        _lifecycle = new ConversationLifecycleManager(_store);
        _profileRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCC Executive", "browser-profiles");
        RecoverInterruptedRolloversAsync().GetAwaiter().GetResult();
        _loop = MonitorAsync(_shutdown.Token);
    }

    public static AutonomousConversationRolloverRuntime Attach(PccExecutiveRuntimeHost host) => new(host);

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await RepairInterruptedRolloversAsync(cancellationToken).ConfigureAwait(false);
                    var run = PccHostRecoveryAccess.Run(_host);
                    if (run is not null && PccHostRecoveryAccess.RuntimeHealthFault(_host) is null)
                    {
                        var runtimes = (await PccHostConversationAccess.RuntimeRegistry(_host).ListAsync(cancellationToken).ConfigureAwait(false))
                            .Where(x => !x.IsArchived && x.State is not BrowserSessionState.Killed and not BrowserSessionState.Archived && StringComparer.Ordinal.Equals(x.ProjectRunId, run.Id.ToString()))
                            .ToArray();
                        foreach (var runtime in runtimes)
                        {
                            if (string.IsNullOrWhiteSpace(runtime.ConversationIdentity)) continue;
                            var active = await FindActiveRecordAsync(runtime, cancellationToken).ConfigureAwait(false);
                            if (active is null) continue;
                            var observation = await ObserveAsync(runtime, active, cancellationToken).ConfigureAwait(false);
                            var decision = _policy.Evaluate(observation);
                            if (decision.State == ConversationHealthState.Rotate)
                                await GovernedRolloverAsync(runtime, active, decision.Reason, cancellationToken).ConfigureAwait(false);
                        }
                    }
                }
                finally { _gate.Release(); }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                await RecordRuntimeEventAsync("ROLLOVER_MONITOR_ERROR", ex.GetType().Name + ":" + ex.Message, cancellationToken).ConfigureAwait(false);
            }

            try { await Task.Delay(MonitorInterval, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
        }
    }

    private async Task<ConversationGrowthObservation> ObserveAsync(BrowserRuntimeRecord runtime, ConversationRecord active, CancellationToken cancellationToken)
    {
        var messages = 0;
        long characters = 0;
        var waveCount = 0;
        var slowOrStuck = 0;
        var contextLimit = false;
        var longComposerFailure = false;

        var checkpoints = await _store.ListCheckpointsAsync(active.ProjectRunId, cancellationToken).ConfigureAwait(false);
        foreach (var checkpoint in checkpoints.Where(x => StringComparer.Ordinal.Equals(x.ProjectRunId, active.ProjectRunId)))
        {
            if (checkpoint.Kind.Contains("manager", StringComparison.OrdinalIgnoreCase) || checkpoint.Kind.Contains("worker", StringComparison.OrdinalIgnoreCase))
            {
                messages++;
                characters += checkpoint.Payload?.Length ?? 0;
            }
            if (checkpoint.Kind.Contains("wave", StringComparison.OrdinalIgnoreCase)) waveCount++;
        }

        var runtimeCheckpoints = await _store.ListCheckpointsAsync(active.ProjectRunId, cancellationToken).ConfigureAwait(false);
        foreach (var checkpoint in runtimeCheckpoints.Where(x => x.Payload?.Contains(runtime.RuntimeId, StringComparison.Ordinal) == true))
        {
            if (checkpoint.Payload!.Contains("SLOW", StringComparison.OrdinalIgnoreCase) || checkpoint.Payload.Contains("STUCK", StringComparison.OrdinalIgnoreCase)) slowOrStuck++;
            if (checkpoint.Payload.Contains("CONTEXT_LIMIT", StringComparison.OrdinalIgnoreCase)) contextLimit = true;
            if (checkpoint.Payload.Contains("LONG_CONVERSATION_COMPOSER", StringComparison.OrdinalIgnoreCase)) longComposerFailure = true;
        }

        return new ConversationGrowthObservation(messages, characters, waveCount, DateTimeOffset.UtcNow - active.CreatedAt, slowOrStuck, contextLimit, longComposerFailure);
    }

    private async Task GovernedRolloverAsync(BrowserRuntimeRecord runtime, ConversationRecord predecessor, string reason, CancellationToken cancellationToken)
    {
        if (predecessor.State != ConversationLifecycleState.Active) return;
        await PccHostRecoveryAccess.NewSendPause(_host).PauseNewSendsAsync($"Conversation rollover for logical agent {predecessor.LogicalAgentId}.", cancellationToken).ConfigureAwait(false);
        var checkpointId = $"rollover:{predecessor.LogicalAgentId}:{predecessor.ConversationId}:{DateTimeOffset.UtcNow.UtcTicks}";
        var checkpoint = new DurableCheckpoint(
            checkpointId,
            predecessor.ProjectRunId,
            "conversation-rollover-v1",
            JsonSerializer.Serialize(new { predecessor.ConversationId, predecessor.LogicalAgentId, predecessor.ProjectRunId, reason, runtime.RuntimeId, Stage = "CHECKPOINTED" }),
            DateTimeOffset.UtcNow);
        await _store.SaveCheckpointAsync(checkpoint, cancellationToken).ConfigureAwait(false);

        var candidateConversationId = Guid.NewGuid().ToString();
        var candidateRuntime = await PccHostConversationAccess.Sessions(_host).CreateAsync(new BrowserSessionRequest(
            predecessor.ProjectRunId,
            predecessor.LogicalAgentId,
            runtime.WorkerSlotId,
            runtime.TaskId,
            candidateConversationId,
            "NEW",
            runtime.Visibility), cancellationToken).ConfigureAwait(false);

        var candidate = new ConversationRecord
        {
            ConversationId = candidateConversationId,
            LogicalAgentId = predecessor.LogicalAgentId,
            ProjectRunId = predecessor.ProjectRunId,
            Sequence = checked(predecessor.Sequence + 1),
            UrlOrProviderIdentity = candidateRuntime.ProviderConversationIdentity ?? "NEW",
            CreatedAt = DateTimeOffset.UtcNow,
            State = ConversationLifecycleState.Candidate,
            PredecessorConversationId = predecessor.ConversationId,
            RolloverReason = reason
        };
        await _store.SaveBrowserConversationAsync(candidate, cancellationToken).ConfigureAwait(false);
        await SaveJournalAsync(predecessor, candidate, checkpointId, "CANDIDATE_CREATED", reason, cancellationToken).ConfigureAwait(false);

        var packet = BuildContinuationPacket(predecessor, candidate, checkpointId, runtime);
        var candidateOwnership = await PccHostConversationAccess.Ownership(_host).ProveAsync(candidateRuntime, cancellationToken).ConfigureAwait(false);
        if (!candidateOwnership.IsProven)
        {
            await RollbackCandidateAsync(predecessor, candidate, candidateRuntime, checkpointId, "CANDIDATE_OWNERSHIP_NOT_PROVEN", cancellationToken).ConfigureAwait(false);
            return;
        }

        var providerIdentity = candidateRuntime.ProviderConversationIdentity ?? "NEW";
        var logicalConversation = new ConversationId(Guid.Parse(candidate.ConversationId));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(packet))).ToLowerInvariant();
        var taskKey = candidateRuntime.TaskId ?? $"rollover:{predecessor.LogicalAgentId}";
        var taskId = PCCExecutive.Application.CanonicalDispatchIdentity.StableTask(new ProjectRunId(Guid.Parse(predecessor.ProjectRunId)), taskKey);
        var waveId = PCCExecutive.Application.CanonicalDispatchIdentity.StableWave(new ProjectRunId(Guid.Parse(predecessor.ProjectRunId)), taskKey);
        var correlation = new PCCExecutive.Application.DurableDispatchCorrelation(new ProjectRunId(Guid.Parse(predecessor.ProjectRunId)), new LogicalAgentId(Guid.Parse(predecessor.LogicalAgentId)), candidateRuntime.WorkerSlotId is null ? null : new WorkerSlotId(int.Parse(candidateRuntime.WorkerSlotId)), taskId, waveId, logicalConversation, providerIdentity, hash);
        var dispatch = await new CanonicalDispatchReservationService(_store).ReserveOrRecoverAsync(correlation, cancellationToken).ConfigureAwait(false);
        var request = new PCCExecutive.Application.AgentRequest(correlation.ProjectRunId, correlation.LogicalAgentId, logicalConversation, dispatch.Id, packet, hash, correlation.WorkerSlotId, candidateRuntime.WorkerSlotId is null ? null : taskId, candidateRuntime.WorkerSlotId is null ? null : waveId, providerIdentity);
        var result = await PccHostConversationAccess.AgentProvider(_host).SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!result.Accepted)
        {
            await SaveJournalAsync(predecessor, candidate, checkpointId, result.IsUncertain ? "CONTINUATION_SUBMITTED_UNKNOWN" : "CONTINUATION_SEND_FAILED", result.ErrorCode ?? reason, cancellationToken).ConfigureAwait(false);
            if (!result.IsUncertain) await RollbackCandidateAsync(predecessor, candidate, candidateRuntime, checkpointId, result.ErrorCode ?? "CONTINUATION_SEND_FAILED", cancellationToken).ConfigureAwait(false);
            return;
        }

        candidateRuntime = await PccHostConversationAccess.RuntimeRegistry(_host).GetAsync(candidateRuntime.RuntimeId, cancellationToken).ConfigureAwait(false) ?? candidateRuntime;
        var expected = new BrowserDispatchExpectation(predecessor.ProjectRunId, predecessor.LogicalAgentId, candidateRuntime.TaskId!, candidate.ConversationId, candidateRuntime.ProviderConversationIdentity!, candidateRuntime.WorkerSlotId);
        var semantic = await PccHostConversationAccess.BrowserAdapter(_host).InspectAsync(candidateRuntime, expected, cancellationToken).ConfigureAwait(false);
        if (semantic.Auth.State != AuthState.Authenticated || semantic.Health.State != PageHealth.Healthy || semantic.Generation.State == GenerationState.Generating)
        {
            await SaveJournalAsync(predecessor, candidate, checkpointId, "CONTINUATION_NOT_YET_PROVEN", "Semantic successor validation incomplete.", cancellationToken).ConfigureAwait(false);
            return;
        }

        await CommitSuccessorAsync(predecessor, candidate, candidateRuntime, checkpointId, reason, cancellationToken).ConfigureAwait(false);
    }

    private async Task RecoverInterruptedRolloversAsync(CancellationToken cancellationToken = default)
    {
        await RepairInterruptedRolloversAsync(cancellationToken).ConfigureAwait(false);
        await NormalizeActiveConversationTruthAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RepairInterruptedRolloversAsync(CancellationToken cancellationToken)
    {
        var run = PccHostRecoveryAccess.Run(_host);
        if (run is null) return;
        var records = (await _store.ListBrowserConversationsAsync(cancellationToken).ConfigureAwait(false))
            .Where(x => StringComparer.Ordinal.Equals(x.ProjectRunId, run.Id.ToString()))
            .ToArray();
        foreach (var group in records.GroupBy(x => x.LogicalAgentId, StringComparer.Ordinal))
        {
            var candidates = group.Where(x => x.State == ConversationLifecycleState.Candidate).OrderByDescending(x => x.Sequence).ToArray();
            foreach (var candidate in candidates)
            {
                var journal = await LoadJournalAsync(candidate, cancellationToken).ConfigureAwait(false);
                if (journal is null) continue;
                var predecessor = group.FirstOrDefault(x => StringComparer.Ordinal.Equals(x.ConversationId, journal.PredecessorConversationId));
                if (predecessor is null) continue;
                var runtime = (await PccHostConversationAccess.RuntimeRegistry(_host).ListAsync(cancellationToken).ConfigureAwait(false))
                    .FirstOrDefault(x => !x.IsArchived && x.State is not BrowserSessionState.Killed and not BrowserSessionState.Archived && StringComparer.Ordinal.Equals(x.LogicalAgentId, candidate.LogicalAgentId) && StringComparer.Ordinal.Equals(x.ConversationIdentity, candidate.ConversationId));
                if (runtime is null)
                {
                    await _store.SaveBrowserConversationAsync(candidate with { State = ConversationLifecycleState.FailedCandidate, RolloverReason = "CANDIDATE_RUNTIME_MISSING_AFTER_CRASH" }, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                if (journal.Status is "CONTINUATION_SUBMITTED_UNKNOWN" or "CONTINUATION_NOT_YET_PROVEN" or "CANDIDATE_CREATED")
                    await FinishBrowserCommitAfterCrashAsync(predecessor, candidate, runtime, journal, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task FinishBrowserCommitAfterCrashAsync(ConversationRecord predecessor, ConversationRecord candidate, BrowserRuntimeRecord runtime, RolloverJournalEntry journal, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(runtime.ProviderConversationIdentity) || string.IsNullOrWhiteSpace(runtime.TaskId)) return;
        var expected = new BrowserDispatchExpectation(predecessor.ProjectRunId, predecessor.LogicalAgentId, runtime.TaskId!, candidate.ConversationId, runtime.ProviderConversationIdentity!, runtime.WorkerSlotId);
        var semantic = await PccHostConversationAccess.BrowserAdapter(_host).InspectAsync(runtime, expected, cancellationToken).ConfigureAwait(false);
        if (semantic.Auth.State == AuthState.Authenticated && semantic.Health.State == PageHealth.Healthy && semantic.Generation.State != GenerationState.Generating)
            await CommitSuccessorAsync(predecessor, candidate, runtime, journal.LifecycleCheckpointId, journal.Reason, cancellationToken).ConfigureAwait(false);
    }

    private async Task NormalizeActiveConversationTruthAsync(CancellationToken cancellationToken)
    {
        var run = PccHostRecoveryAccess.Run(_host);
        if (run is null) return;
        var records = (await _store.ListBrowserConversationsAsync(cancellationToken).ConfigureAwait(false))
            .Where(x => StringComparer.Ordinal.Equals(x.ProjectRunId, run.Id.ToString()))
            .ToArray();
        foreach (var group in records.GroupBy(x => x.LogicalAgentId, StringComparer.Ordinal))
        {
            var active = group.Where(x => x.State == ConversationLifecycleState.Active).OrderByDescending(x => x.Sequence).ThenByDescending(x => x.CreatedAt).ToArray();
            if (active.Length <= 1) continue;
            var winner = active[0];
            foreach (var loser in active.Skip(1))
            {
                var archived = loser with { State = ConversationLifecycleState.Archived, RetiredAt = DateTimeOffset.UtcNow, SuccessorConversationId = winner.ConversationId, RolloverReason = "RECOVERY_EXACTLY_ONE_ACTIVE" };
                await _store.SaveBrowserConversationAsync(archived, cancellationToken).ConfigureAwait(false);
                var runtime = (await PccHostConversationAccess.RuntimeRegistry(_host).ListAsync(cancellationToken).ConfigureAwait(false))
                    .FirstOrDefault(x => StringComparer.Ordinal.Equals(x.LogicalAgentId, loser.LogicalAgentId) && StringComparer.Ordinal.Equals(x.ConversationIdentity, loser.ConversationId));
                if (runtime is not null && !runtime.IsArchived) await PccHostConversationAccess.Sessions(_host).KillAsync(runtime.RuntimeId, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task CommitSuccessorAsync(ConversationRecord predecessor, ConversationRecord candidate, BrowserRuntimeRecord candidateRuntime, string checkpointId, string reason, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var archived = predecessor with { State = ConversationLifecycleState.Archived, RetiredAt = now, SuccessorConversationId = candidate.ConversationId, RolloverReason = reason };
        var successor = candidate with { State = ConversationLifecycleState.Active, UrlOrProviderIdentity = candidateRuntime.ProviderConversationIdentity ?? candidate.UrlOrProviderIdentity };
        await _lifecycle.CommitRolloverAsync(archived, successor, checkpointId, cancellationToken).ConfigureAwait(false);
        await SaveJournalAsync(archived, successor, checkpointId, "COMMITTED", reason, cancellationToken).ConfigureAwait(false);

        var oldRuntime = (await PccHostConversationAccess.RuntimeRegistry(_host).ListAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(x => StringComparer.Ordinal.Equals(x.LogicalAgentId, predecessor.LogicalAgentId) && StringComparer.Ordinal.Equals(x.ConversationIdentity, predecessor.ConversationId));
        if (oldRuntime is not null && !oldRuntime.IsArchived) await PccHostConversationAccess.Sessions(_host).KillAsync(oldRuntime.RuntimeId, cancellationToken).ConfigureAwait(false);

        var logicalId = new LogicalAgentId(Guid.Parse(successor.LogicalAgentId));
        var logical = await _store.LoadLogicalAgentAsync(logicalId, cancellationToken).ConfigureAwait(false);
        if (logical is not null)
            await _store.SaveLogicalAgentAsync(logical with { CurrentConversationId = new ConversationId(Guid.Parse(successor.ConversationId)), State = LogicalSessionState.Active }, cancellationToken).ConfigureAwait(false);

        await PccHostRecoveryAccess.NewSendPause(_host).ResumeNewSendsAsync("Conversation rollover committed and successor is proven active.", cancellationToken).ConfigureAwait(false);
    }

    private async Task RollbackCandidateAsync(ConversationRecord predecessor, ConversationRecord candidate, BrowserRuntimeRecord candidateRuntime, string checkpointId, string reason, CancellationToken cancellationToken)
    {
        await _store.SaveBrowserConversationAsync(candidate with { State = ConversationLifecycleState.FailedCandidate, RolloverReason = reason }, cancellationToken).ConfigureAwait(false);
        if (!candidateRuntime.IsArchived) await PccHostConversationAccess.Sessions(_host).KillAsync(candidateRuntime.RuntimeId, cancellationToken).ConfigureAwait(false);
        await SaveJournalAsync(predecessor, candidate, checkpointId, "ROLLED_BACK", reason, cancellationToken).ConfigureAwait(false);
        await PccHostRecoveryAccess.NewSendPause(_host).ResumeNewSendsAsync("Conversation rollover candidate rolled back; predecessor remains active.", cancellationToken).ConfigureAwait(false);
    }

    private string BuildContinuationPacket(ConversationRecord predecessor, ConversationRecord candidate, string checkpointId, BrowserRuntimeRecord runtime)
    {
        var run = PccHostRecoveryAccess.Run(_host);
        return string.Join('\n', new[]
        {
            $"PROJECT_RUN_ID: {predecessor.ProjectRunId}",
            $"LOGICAL_AGENT_ID: {predecessor.LogicalAgentId}",
            $"WORKER_SLOT_ID: {runtime.WorkerSlotId ?? "MANAGER"}",
            $"CURRENT_TASK_ID: {runtime.TaskId ?? "MANAGER"}",
            $"PREVIOUS_CONVERSATION_ID: {predecessor.ConversationId}",
            $"SUCCESSOR_CONVERSATION_ID: {candidate.ConversationId}",
            $"ROLLOVER_CHECKPOINT_ID: {checkpointId}",
            $"VERIFIED_COMPLETION: {run?.VerifiedCompletion.Percent ?? 0m}",
            "CONTINUE THE SAME LOGICAL AGENT AND CURRENT TASK. FETCH LIVE STATE BEFORE MAKING NEW CONCLUSIONS."
        });
    }

    private async Task<ConversationRecord?> FindActiveRecordAsync(BrowserRuntimeRecord runtime, CancellationToken cancellationToken)
    {
        var records = await _store.ListBrowserConversationsAsync(cancellationToken).ConfigureAwait(false);
        return records.Where(x => x.State == ConversationLifecycleState.Active && StringComparer.Ordinal.Equals(x.LogicalAgentId, runtime.LogicalAgentId) && StringComparer.Ordinal.Equals(x.ConversationId, runtime.ConversationIdentity)).OrderByDescending(x => x.Sequence).FirstOrDefault();
    }

    private Task SaveJournalAsync(ConversationRecord predecessor, ConversationRecord successor, string checkpointId, string status, string reason, CancellationToken cancellationToken) =>
        _store.SaveCheckpointAsync(new DurableCheckpoint(
            $"rollover-journal:{predecessor.LogicalAgentId}:{successor.ConversationId}",
            predecessor.ProjectRunId,
            "rollover-journal-v1",
            JsonSerializer.Serialize(new RolloverJournalEntry(predecessor.ConversationId, successor.ConversationId, checkpointId, reason, status, DateTimeOffset.UtcNow)),
            DateTimeOffset.UtcNow), cancellationToken);

    private async Task<RolloverJournalEntry?> LoadJournalAsync(ConversationRecord candidate, CancellationToken cancellationToken)
    {
        var checkpoint = await _store.LoadCheckpointAsync($"rollover-journal:{candidate.LogicalAgentId}:{candidate.ConversationId}", cancellationToken).ConfigureAwait(false);
        return checkpoint is null ? null : JsonSerializer.Deserialize<RolloverJournalEntry>(checkpoint.Payload);
    }

    private Task RecordRuntimeEventAsync(string kind, string detail, CancellationToken cancellationToken)
    {
        var run = PccHostRecoveryAccess.Run(_host);
        return run is null ? Task.CompletedTask : _store.SaveCheckpointAsync(new DurableCheckpoint($"{kind}:{run.Id}:{DateTimeOffset.UtcNow.UtcTicks}", run.Id.ToString(), kind, detail, DateTimeOffset.UtcNow), cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        try { await _loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        _shutdown.Dispose();
        _gate.Dispose();
    }

    private sealed record RolloverJournalEntry(
        string PredecessorConversationId,
        string SuccessorConversationId,
        string LifecycleCheckpointId,
        string Reason,
        string Status,
        DateTimeOffset UpdatedAt);
}


internal static class PccHostRecoveryAccess
{
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_store")]
    internal static extern ref SqliteStateStore Store(PccExecutiveRuntimeHost host);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_run")]
    internal static extern ref ProjectRun? Run(PccExecutiveRuntimeHost host);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_runtimeHealthFault")]
    internal static extern ref string? RuntimeHealthFault(PccExecutiveRuntimeHost host);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_sendGate")]
    internal static extern ref GlobalBrowserSendGate SendGate(PccExecutiveRuntimeHost host);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_newSendPause")]
    internal static extern ref INewSendPausePort NewSendPause(PccExecutiveRuntimeHost host);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_autopilot")]
    internal static extern ref string Autopilot(PccExecutiveRuntimeHost host);
}
internal static class PccHostConversationAccess
{
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_runtimeRegistry")]
    internal static extern ref IBrowserRuntimeRegistry RuntimeRegistry(PccExecutiveRuntimeHost host);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_sessions")]
    internal static extern ref BrowserSessionController Sessions(PccExecutiveRuntimeHost host);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_ownership")]
    internal static extern ref IOwnershipProofService Ownership(PccExecutiveRuntimeHost host);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_agentProvider")]
    internal static extern ref PCCExecutive.Application.IAgentProvider AgentProvider(PccExecutiveRuntimeHost host);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_browserAdapter")]
    internal static extern ref IChatGptBrowserAdapter BrowserAdapter(PccExecutiveRuntimeHost host);
}
