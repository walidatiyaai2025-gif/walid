using PCCExecutive.Browser;
using PCCExecutive.Domain;
using PCCExecutive.Infrastructure;
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
    private readonly PreventiveRolloverPolicy _policy = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Task _loop;
    private readonly string _profileRoot;
    private int _disposed;

    private AutonomousConversationRolloverRuntime(PccExecutiveRuntimeHost host)
    {
        _host = host;
        _store = PccHostRecoveryAccess.Store(_host);
        _profileRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCC Executive", "browser-profiles");
        RecoverInterruptedRolloversAsync().GetAwaiter().GetResult();
        RecoverDurableAttentionAsync().GetAwaiter().GetResult();
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
                    await RecoverInterruptedRolloversAsync(cancellationToken).ConfigureAwait(false);
                    await RecoverDurableAttentionAsync(cancellationToken).ConfigureAwait(false);
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
        var age = DateTimeOffset.UtcNow - active.CreatedAt;
        if (string.IsNullOrWhiteSpace(runtime.TaskId) || string.IsNullOrWhiteSpace(runtime.ConversationIdentity) || string.IsNullOrWhiteSpace(runtime.ProviderConversationIdentity))
            return new ConversationGrowthObservation(0, 0, 0, age, runtime.State is BrowserSessionState.Degraded or BrowserSessionState.Recovering ? 1 : 0, false, false);

        var expected = new BrowserDispatchExpectation(runtime.ProjectRunId, runtime.LogicalAgentId, runtime.TaskId!, runtime.ConversationIdentity!, runtime.ProviderConversationIdentity!, runtime.WorkerSlotId);
        var semantic = await PccHostConversationAccess.BrowserAdapter(_host).InspectAsync(runtime, expected, cancellationToken).ConfigureAwait(false);
        var evidence = semantic.Input.Evidence
            .Concat(semantic.Generation.Evidence)
            .Concat(semantic.Auth.Evidence)
            .Concat(semantic.Conversation.Evidence)
            .Concat(semantic.Health.Evidence)
            .ToArray();
        var contextLimit = evidence.Any(x => x.Contains("CONTEXT_LIMIT", StringComparison.OrdinalIgnoreCase) || x.Contains("context limit", StringComparison.OrdinalIgnoreCase));
        var longComposerFailure = evidence.Any(x => x.Contains("LONG_CONVERSATION_COMPOSER", StringComparison.OrdinalIgnoreCase) || x.Contains("conversation too long", StringComparison.OrdinalIgnoreCase));
        var slowOrStuck = semantic.Health.State is PageHealth.Slow or PageHealth.TempError || semantic.Generation.State == GenerationState.Unknown ? 1 : 0;
        var capturedCharacters = semantic.CapturedResponseText?.Length ?? 0;
        return new ConversationGrowthObservation(semantic.AssistantMessageCount, capturedCharacters, 0, age, slowOrStuck, contextLimit, longComposerFailure);
    }

    private async Task GovernedRolloverAsync(BrowserRuntimeRecord runtime, ConversationRecord predecessor, string reason, CancellationToken cancellationToken)
    {
        if (predecessor.State != ConversationLifecycleState.Active) return;
        var checkpointId = $"rollover:{predecessor.LogicalAgentId}:{predecessor.ConversationId}:{DateTimeOffset.UtcNow.UtcTicks}";
        var checkpoint = new DurableCheckpoint(
            checkpointId,
            predecessor.ProjectRunId,
            "conversation-rollover-v1",
            JsonSerializer.Serialize(new { predecessor.ConversationId, predecessor.LogicalAgentId, predecessor.ProjectRunId, reason, runtime.RuntimeId, Stage = "CHECKPOINTED" }),
            DateTimeOffset.UtcNow);
        await _store.SaveCheckpointAsync(checkpoint, cancellationToken).ConfigureAwait(false);

        var candidateConversation = ConversationId.New();
        var candidateConversationId = candidateConversation.ToString();
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
        var logicalConversation = candidateConversation;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(packet))).ToLowerInvariant();
        var projectRunId = new ProjectRunId(Guid.Parse(predecessor.ProjectRunId));
        var logicalAgentId = new LogicalAgentId(Guid.Parse(predecessor.LogicalAgentId));
        WorkerSlotId? workerSlotId = candidateRuntime.WorkerSlotId is null ? null : new WorkerSlotId(int.Parse(candidateRuntime.WorkerSlotId));
        TaskId taskId;
        WaveId waveId;
        if (workerSlotId is not null)
        {
            if (!Guid.TryParse(candidateRuntime.TaskId, out var currentTaskGuid))
            {
                await RollbackCandidateAsync(predecessor, candidate, candidateRuntime, checkpointId, "ROLLOVER_WORKER_TASK_IDENTITY_INVALID", cancellationToken).ConfigureAwait(false);
                return;
            }
            var currentWave = PccHostRecoveryAccess.CurrentWave(_host);
            if (currentWave is null)
            {
                await RollbackCandidateAsync(predecessor, candidate, candidateRuntime, checkpointId, "ROLLOVER_WORKER_WAVE_IDENTITY_MISSING", cancellationToken).ConfigureAwait(false);
                return;
            }
            taskId = new TaskId(currentTaskGuid);
            waveId = currentWave.Id;
        }
        else
        {
            var taskKey = candidateRuntime.TaskId ?? $"rollover:{predecessor.LogicalAgentId}";
            taskId = PCCExecutive.Application.CanonicalDispatchIdentity.StableTask(projectRunId, taskKey);
            waveId = PCCExecutive.Application.CanonicalDispatchIdentity.StableWave(projectRunId, taskKey);
        }
        var correlation = new PCCExecutive.Application.DurableDispatchCorrelation(projectRunId, logicalAgentId, workerSlotId, taskId, waveId, logicalConversation, providerIdentity, hash);
        var dispatch = await new CanonicalDispatchReservationService(_store).ReserveOrRecoverAsync(correlation, cancellationToken).ConfigureAwait(false);
        var request = new PCCExecutive.Application.AgentRequest(correlation.ProjectRunId, correlation.LogicalAgentId, logicalConversation, dispatch.Id, packet, hash, workerSlotId, workerSlotId is null ? null : taskId, workerSlotId is null ? null : waveId, providerIdentity);
        var result = await PccHostConversationAccess.AgentProvider(_host).SendAsync(request, cancellationToken).ConfigureAwait(false);
        await PccHostRecoveryAccess.NewSendPause(_host).PauseNewSendsAsync($"Conversation rollover for logical agent {predecessor.LogicalAgentId}.", cancellationToken).ConfigureAwait(false);
        if (!result.Accepted)
        {
            await SaveJournalAsync(predecessor, candidate, checkpointId, result.IsUncertain ? "CONTINUATION_SUBMITTED_UNKNOWN" : "CONTINUATION_SEND_FAILED", result.ErrorCode ?? reason, cancellationToken).ConfigureAwait(false);
            if (!result.IsUncertain) await RollbackCandidateAsync(predecessor, candidate, candidateRuntime, checkpointId, result.ErrorCode ?? "CONTINUATION_SEND_FAILED", cancellationToken).ConfigureAwait(false);
            return;
        }

        candidateRuntime = await PccHostConversationAccess.RuntimeRegistry(_host).GetAsync(candidateRuntime.RuntimeId, cancellationToken).ConfigureAwait(false) ?? candidateRuntime;
        if (string.IsNullOrWhiteSpace(candidateRuntime.TaskId) || string.IsNullOrWhiteSpace(candidateRuntime.ProviderConversationIdentity))
        {
            await SaveJournalAsync(predecessor, candidate, checkpointId, "CONTINUATION_NOT_YET_PROVEN", "Successor runtime binding is incomplete.", cancellationToken).ConfigureAwait(false);
            return;
        }
        var expected = new BrowserDispatchExpectation(predecessor.ProjectRunId, predecessor.LogicalAgentId, candidateRuntime.TaskId, candidate.ConversationId, candidateRuntime.ProviderConversationIdentity, candidateRuntime.WorkerSlotId);
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
        await TryResolveStartupRecoveryFenceAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task TryResolveStartupRecoveryFenceAsync(CancellationToken cancellationToken)
    {
        var run = PccHostRecoveryAccess.Run(_host);
        if (run is null) return;

        var sessions = (await _store.ListLogicalAgentsAsync(cancellationToken).ConfigureAwait(false))
            .Where(x => x.ProjectRunId == run.Id && x.CurrentConversationId is not null)
            .ToArray();
        if (sessions.Length == 0) return;

        var runtimes = (await PccHostConversationAccess.RuntimeRegistry(_host).ListAsync(cancellationToken).ConfigureAwait(false))
            .Where(x => !x.IsArchived &&
                        x.State is not BrowserSessionState.Killed and not BrowserSessionState.Archived &&
                        StringComparer.Ordinal.Equals(x.ProjectRunId, run.Id.ToString()))
            .ToArray();
        var reconciler = new BrowserSessionReconciliationService();
        foreach (var session in sessions)
        {
            var agentRuntimes = runtimes
                .Where(x => StringComparer.Ordinal.Equals(x.LogicalAgentId, session.Id.ToString()))
                .ToArray();
            if (agentRuntimes.Length != 1) return;

            var runtime = agentRuntimes[0];
            if (reconciler.Reconcile(session, runtime).Outcome != BrowserReconciliationKind.MATCHED) return;
            var ownership = await PccHostConversationAccess.Ownership(_host).ProveAsync(runtime, cancellationToken).ConfigureAwait(false);
            if (!ownership.IsProven) return;
        }

        await PccHostRecoveryAccess.NewSendPause(_host).ResumeNewSendsAsync(
            "STARTUP_BROWSER_RECONCILIATION_COMPLETE:all durable logical sessions match exactly one PCC-owned active runtime.",
            cancellationToken).ConfigureAwait(false);
        if (!PccHostRecoveryAccess.SendGate(_host).Snapshot.IsPaused &&
            StringComparer.Ordinal.Equals(PccHostRecoveryAccess.Autopilot(_host), "RECOVERY_REQUIRED"))
            PccHostRecoveryAccess.Autopilot(_host) = "RUNNING";
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
        var expected = new BrowserDispatchExpectation(predecessor.ProjectRunId, predecessor.LogicalAgentId, runtime.TaskId, candidate.ConversationId, runtime.ProviderConversationIdentity, runtime.WorkerSlotId);
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
        var runtimes = (await PccHostConversationAccess.RuntimeRegistry(_host).ListAsync(cancellationToken).ConfigureAwait(false))
            .Where(x => StringComparer.Ordinal.Equals(x.ProjectRunId, run.Id.ToString()))
            .ToArray();

        foreach (var group in records.GroupBy(x => x.LogicalAgentId, StringComparer.Ordinal))
        {
            LogicalAgentSession? logical = null;
            if (Guid.TryParse(group.Key, out var logicalGuid))
                logical = await _store.LoadLogicalAgentAsync(new LogicalAgentId(logicalGuid), cancellationToken).ConfigureAwait(false);

            var agentRuntimes = runtimes.Where(x => StringComparer.Ordinal.Equals(x.LogicalAgentId, group.Key)).ToArray();
            var plan = ConversationRecoveryInvariantPlanner.Build(group.ToArray(), logical?.CurrentConversationId?.ToString(), agentRuntimes);
            if (plan.ActiveConversationId is null) continue;

            var selected = group.First(x => StringComparer.Ordinal.Equals(x.ConversationId, plan.ActiveConversationId));
            if (plan.PromoteSelectedConversation && selected.State != ConversationLifecycleState.Active)
                await _store.SaveBrowserConversationAsync(selected with { State = ConversationLifecycleState.Active, RetiredAt = null, RolloverReason = selected.RolloverReason ?? "RECOVERY_RESTORED_ACTIVE" }, cancellationToken).ConfigureAwait(false);

            foreach (var conversationId in plan.ArchiveConversationIds)
            {
                var loser = group.First(x => StringComparer.Ordinal.Equals(x.ConversationId, conversationId));
                await _store.SaveBrowserConversationAsync(loser with
                {
                    State = ConversationLifecycleState.Archived,
                    RetiredAt = loser.RetiredAt ?? DateTimeOffset.UtcNow,
                    SuccessorConversationId = plan.ActiveConversationId,
                    RolloverReason = loser.RolloverReason ?? "RECOVERY_EXACTLY_ONE_ACTIVE"
                }, cancellationToken).ConfigureAwait(false);
            }

            if (logical is not null && plan.UpdateLogicalSession && Guid.TryParse(plan.ActiveConversationId, out var activeGuid))
                await _store.SaveLogicalAgentAsync(logical with { CurrentConversationId = new ConversationId(activeGuid), State = LogicalSessionState.Active }, cancellationToken).ConfigureAwait(false);

            foreach (var runtimeId in plan.RetireRuntimeIds)
            {
                var retired = await PccHostConversationAccess.Sessions(_host).KillAsync(runtimeId, cancellationToken).ConfigureAwait(false);
                if (!retired.Succeeded)
                {
                    await PccHostRecoveryAccess.NewSendPause(_host).PauseNewSendsAsync($"RETIRED_CONVERSATION_RUNTIME_NOT_CLOSED:{runtimeId}:{retired.Reason}", cancellationToken).ConfigureAwait(false);
                    await RecordRuntimeEventAsync("RETIRED_CONVERSATION_RUNTIME_NOT_CLOSED", $"{runtimeId}:{retired.Reason}", cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task RecoverDurableAttentionAsync(CancellationToken cancellationToken = default)
    {
        var run = PccHostRecoveryAccess.Run(_host);
        if (run is null) return;
        var attention = PccHostRecoveryAccess.Attention(_host);
        var checkpoint = await _store.LoadCheckpointAsync($"runtime-health:{run.Id}", cancellationToken).ConfigureAwait(false);
        DurableRuntimeHealthProjection? health = null;
        if (checkpoint is not null)
        {
            try { health = JsonSerializer.Deserialize<DurableRuntimeHealthProjection>(checkpoint.Payload); }
            catch (JsonException) { }
        }

        var attentionCode = health is null ? null : DurableProviderAttentionPolicy.Classify(health.Active, health.State, health.Reason);
        if (attentionCode is null)
        {
            foreach (var key in attention.Keys.Where(x => x.StartsWith("browser-attention:", StringComparison.Ordinal)).ToArray())
                attention.Remove(key);
            return;
        }

        var runtimeId = health!.RuntimeId ?? string.Empty;
        var runtime = string.IsNullOrWhiteSpace(runtimeId)
            ? null
            : await PccHostConversationAccess.RuntimeRegistry(_host).GetAsync(runtimeId, cancellationToken).ConfigureAwait(false);
        var target = runtime?.WorkerSlotId is { Length: > 0 } slot ? $"Worker {slot} ChatGPT session" : "Manager ChatGPT session";
        var id = $"browser-attention:{(string.IsNullOrWhiteSpace(runtimeId) ? "durable-global-health" : runtimeId)}";
        var reason = attentionCode == "CHALLENGE"
            ? "ChatGPT presented a challenge/CAPTCHA that automation must not bypass. Durable global sends remain blocked until fresh semantic recovery proof."
            : "ChatGPT authentication is required in the isolated PCC-owned profile. Durable global sends remain blocked until fresh semantic recovery proof.";
        attention[id] = (new AttentionSummary(id, attentionCode, reason, "Open PCC Browser", target, "P0"), runtimeId);
        PccHostRecoveryAccess.Autopilot(_host) = "ATTENTION_REQUIRED";
    }

    private async Task CommitSuccessorAsync(ConversationRecord predecessor, ConversationRecord candidate, BrowserRuntimeRecord candidateRuntime, string checkpointId, string reason, CancellationToken cancellationToken)
    {
        var provenCandidate = candidate with
        {
            UrlOrProviderIdentity = candidateRuntime.ProviderConversationIdentity ?? candidate.UrlOrProviderIdentity
        };
        var lifecycleResult = await RecoveryRolloverLifecycleBridge.CommitWithExistingConversationLifecycleManagerAsync(
            predecessor,
            provenCandidate,
            checkpointId,
            reason,
            (archived, successor, committedCheckpointId, ct) => _store.CommitRolloverAsync(archived, successor, committedCheckpointId, ct),
            cancellationToken).ConfigureAwait(false);

        if (!lifecycleResult.Succeeded)
        {
            await SaveJournalAsync(predecessor, candidate, checkpointId, "LIFECYCLE_FINALIZATION_FAILED", lifecycleResult.Reason, cancellationToken).ConfigureAwait(false);
            await PccHostRecoveryAccess.NewSendPause(_host).PauseNewSendsAsync($"ROLLOVER_LIFECYCLE_FINALIZATION_FAILED:{lifecycleResult.Reason}", cancellationToken).ConfigureAwait(false);
            await RecordRuntimeEventAsync("ROLLOVER_LIFECYCLE_FINALIZATION_FAILED", lifecycleResult.Reason, cancellationToken).ConfigureAwait(false);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var archived = predecessor with { State = ConversationLifecycleState.Archived, RetiredAt = now, SuccessorConversationId = candidate.ConversationId, RolloverReason = reason };
        var successor = provenCandidate with { State = ConversationLifecycleState.Active };
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
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
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

    private sealed record DurableRuntimeHealthProjection(
        bool Active,
        string State,
        string Reason,
        DateTimeOffset? ResumeNotBefore,
        bool RequiresHumanAction,
        string? RuntimeId);
}

internal static class PccHostRecoveryAccess
{
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_store")]
    internal static extern ref SqliteStateStore Store(PccExecutiveRuntimeHost host);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_run")]
    internal static extern ref ProjectRun? Run(PccExecutiveRuntimeHost host);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_currentWave")]
    internal static extern ref Wave? CurrentWave(PccExecutiveRuntimeHost host);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_runtimeHealthFault")]
    internal static extern ref string? RuntimeHealthFault(PccExecutiveRuntimeHost host);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_sendGate")]
    internal static extern ref GlobalBrowserSendGate SendGate(PccExecutiveRuntimeHost host);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_newSendPause")]
    internal static extern ref INewSendPausePort NewSendPause(PccExecutiveRuntimeHost host);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_autopilot")]
    internal static extern ref string Autopilot(PccExecutiveRuntimeHost host);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_attention")]
    internal static extern ref Dictionary<string, (AttentionSummary Summary, string RuntimeId)> Attention(PccExecutiveRuntimeHost host);
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
