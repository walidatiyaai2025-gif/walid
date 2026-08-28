using System.Runtime.CompilerServices;
using System.Text.Json;
using PCCExecutive.Browser;
using PCCExecutive.Domain;
using PCCExecutive.Infrastructure;

namespace PCCExecutive.App.Presentation;

/// <summary>
/// Production composition for the existing ConversationLifecycleManager.
/// The logical runtime keeps its deterministic active-tip id so existing Manager/Worker
/// dispatch contracts remain compatible; before rotation the current tip is moved to an
/// immutable historical predecessor id, then the stable tip id becomes the validated successor.
/// This preserves lineage without allowing future work to target an archived provider conversation.
/// </summary>
public sealed class AutonomousConversationRolloverRuntime : IAsyncDisposable
{
    private readonly RecoveryCompletionPresentationGateway _gateway;
    private readonly PccExecutiveRuntimeHost _host;
    private readonly SqliteStateStore _store;
    private readonly IBrowserRuntimeRegistry _registry;
    private readonly BrowserSessionController _sessions;
    private readonly IOwnershipProofService _ownership;
    private readonly IChatGptBrowserAdapter _adapter;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly PreventiveRolloverPolicy _policy = new();
    private readonly Task _monitor;

    private AutonomousConversationRolloverRuntime(RecoveryCompletionPresentationGateway gateway)
    {
        _gateway = gateway;
        _host = RecoveryGatewayRolloverAccess.Inner(gateway);
        _store = PccHostRecoveryAccess.Store(_host);
        _registry = PccHostConversationAccess.RuntimeRegistry(_host);
        _sessions = PccHostConversationAccess.Sessions(_host);
        _ownership = PccHostConversationAccess.Ownership(_host);
        _adapter = PccHostConversationAccess.BrowserAdapter(_host);
        RepairInterruptedRolloversAsync(CancellationToken.None).GetAwaiter().GetResult();
        NormalizeActiveConversationTruthAsync(CancellationToken.None).GetAwaiter().GetResult();
        TryResumeRecoveredAutopilotAsync(CancellationToken.None).GetAwaiter().GetResult();
        _monitor = Task.Run(() => MonitorAsync(_lifetime.Token));
    }

    public static AutonomousConversationRolloverRuntime Attach(RecoveryCompletionPresentationGateway gateway) => new(gateway);

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await EvaluateAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch { /* fail closed; next deterministic pass retries observation, never the continuation send */ }
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task EvaluateAsync(CancellationToken cancellationToken)
    {
        if (!await _operation.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return;
        try
        {
            var run = PccHostRecoveryAccess.Run(_host);
            if (run is null || run.State is ProjectRunState.StalledAutoStopped or ProjectRunState.VerifiedComplete or ProjectRunState.StoppedByOperator) return;
            if (!string.IsNullOrWhiteSpace(PccHostRecoveryAccess.RuntimeHealthFault(_host))) return;
            if (PccHostRecoveryAccess.SendGate(_host).Snapshot.IsPaused) return;

            var runtimes = (await _registry.ListAsync(cancellationToken).ConfigureAwait(false))
                .Where(x => !x.IsArchived && x.State is not BrowserSessionState.Killed &&
                            StringComparer.Ordinal.Equals(x.ProjectRunId, run.Id.ToString()) &&
                            !string.IsNullOrWhiteSpace(x.ConversationIdentity) &&
                            !string.IsNullOrWhiteSpace(x.ProviderConversationIdentity) &&
                            !string.Equals(x.ProviderConversationIdentity, "NEW", StringComparison.OrdinalIgnoreCase) &&
                            !string.IsNullOrWhiteSpace(x.TaskId))
                .ToArray();

            foreach (var runtime in runtimes)
            {
                if (PccHostRecoveryAccess.SendGate(_host).Snapshot.IsPaused) break;
                var expected = new BrowserDispatchExpectation(runtime.ProjectRunId, runtime.LogicalAgentId, runtime.TaskId!, runtime.ConversationIdentity!, runtime.ProviderConversationIdentity!, runtime.WorkerSlotId);
                var semantic = await _adapter.InspectAsync(runtime, expected, cancellationToken).ConfigureAwait(false);
                var resilience = new ChatGptResilienceClassifier().Classify(semantic, DateTimeOffset.UtcNow - runtime.LastActivityAt);
                if (semantic.Auth.State is AuthState.LoginRequired or AuthState.Challenge ||
                    (resilience.Scope == FaultScope.Global && resilience.PauseUnsafeNewSends))
                {
                    await PccHostConversationAccess.PersistGlobalHealthPauseAsync(_host, resilience, runtime.RuntimeId, cancellationToken).ConfigureAwait(false);
                    break;
                }
                if (semantic.Auth.State != AuthState.Authenticated || semantic.Health.State != PageHealth.Healthy || semantic.Generation.State == GenerationState.Generating) continue;

                var active = await FindOrCreateActiveBrowserConversationAsync(runtime, cancellationToken).ConfigureAwait(false);
                if (active is null) continue;
                var contextLimit = semantic.Health.Evidence.Any(x => x.Contains("context-limit", StringComparison.OrdinalIgnoreCase)) ||
                                   (semantic.CapturedResponseText?.Contains("maximum context", StringComparison.OrdinalIgnoreCase) ?? false);
                var decision = _policy.Evaluate(new ConversationGrowthObservation(
                    semantic.AssistantMessageCount * 2,
                    semantic.CapturedResponseText?.Length ?? 0,
                    0,
                    DateTimeOffset.UtcNow - active.CreatedAt,
                    semantic.Health.State == PageHealth.Slow ? 1 : 0,
                    contextLimit,
                    false));
                if (!decision.RequestCheckpoint) continue;

                await RolloverAsync(runtime, active, decision.Reason, cancellationToken).ConfigureAwait(false);
            }
        }
        finally { _operation.Release(); }
    }

    private async Task RolloverAsync(BrowserRuntimeRecord runtime, ConversationRecord activeTip, string reason, CancellationToken cancellationToken)
    {
        var gateWasPaused = PccHostRecoveryAccess.SendGate(_host).Snapshot.IsPaused;
        if (gateWasPaused) return;
        await PccHostRecoveryAccess.NewSendPause(_host).PauseNewSendsAsync($"CONVERSATION_ROLLOVER:{runtime.LogicalAgentId}", cancellationToken).ConfigureAwait(false);

        RolloverTransactionContext? context = null;
        try
        {
            context = await PrepareHistoricalPredecessorAsync(runtime, activeTip, reason, cancellationToken).ConfigureAwait(false);
            var lifecycleStore = new StableTipConversationLifecycleStore(_store, _registry, _sessions, _adapter, context);
            var manager = new ConversationLifecycleManager(
                _store,
                new StableTipConversationCreator(context),
                new RuntimeContinuationSender(_host, _registry, _sessions, _ownership, _adapter, context),
                new RuntimeContinuationValidator(_registry, _adapter, context),
                lifecycleStore);

            var packet = BuildContinuationPacket(runtime, context);
            var result = await manager.RolloverAsync(context.HistoricalPredecessor, reason, packet, cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                await lifecycleStore.EnsureRollbackAsync(result.Reason, cancellationToken).ConfigureAwait(false);
                return;
            }

            await NormalizeActiveConversationTruthAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (!gateWasPaused && string.IsNullOrWhiteSpace(PccHostRecoveryAccess.RuntimeHealthFault(_host)) &&
                PccHostRecoveryAccess.Run(_host)?.State != ProjectRunState.StalledAutoStopped)
                await PccHostRecoveryAccess.NewSendPause(_host).ResumeNewSendsAsync("Conversation rollover transaction finished with deterministic active-conversation truth.", cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<RolloverTransactionContext> PrepareHistoricalPredecessorAsync(BrowserRuntimeRecord runtime, ConversationRecord activeTip, string reason, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(activeTip.ConversationId, out var stableGuid) || !Guid.TryParse(runtime.LogicalAgentId, out var agentGuid) || !Guid.TryParse(runtime.ProjectRunId, out var runGuid))
            throw new InvalidOperationException("Rollover requires GUID-backed durable runtime identities.");

        var stableId = new ConversationId(stableGuid);
        var agentId = new LogicalAgentId(agentGuid);
        var runId = new ProjectRunId(runGuid);
        var session = await _store.LoadLogicalAgentAsync(agentId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Durable logical agent session is unavailable for rollover.");
        if (session.CurrentConversationId is not null && session.CurrentConversationId.Value != stableId)
            throw new InvalidOperationException("Logical agent active conversation contradicts Browser active-tip identity.");

        var historicalGuid = Guid.NewGuid();
        var historicalId = new ConversationId(historicalGuid);
        var historical = activeTip with
        {
            ConversationId = historicalGuid.ToString(),
            SuccessorConversationId = null,
            RetiredAt = null,
            RolloverReason = reason,
            State = ConversationLifecycleState.Active
        };
        var domainTip = await _store.LoadConversationAsync(stableId, cancellationToken).ConfigureAwait(false) ?? new PCCExecutive.Domain.Conversation(
            stableId, agentId, activeTip.Sequence, AgentProviderKind.BrowserChat, activeTip.UrlOrProviderIdentity, ProviderUrl(activeTip.UrlOrProviderIdentity),
            ConversationState.Active, activeTip.CreatedAt, null,
            ParseConversation(activeTip.PredecessorConversationId), ParseConversation(activeTip.SuccessorConversationId), 1d, 0, null, activeTip.RolloverReason);
        var previousPredecessorId = domainTip.PredecessorId;
        var historicalDomain = domainTip with
        {
            Id = historicalId,
            State = ConversationState.Active,
            RetiredAt = null,
            SuccessorId = null,
            RolloverReason = reason
        };

        var state = new RolloverRecoveryState(
            runtime.ProjectRunId, runtime.LogicalAgentId, runtime.RuntimeId, activeTip.ConversationId,
            historical.ConversationId, activeTip.Sequence, activeTip.UrlOrProviderIdentity,
            activeTip.PredecessorConversationId, null, null, reason, "PREPARING", DateTimeOffset.UtcNow);
        await SaveRecoveryStateAsync(state, cancellationToken).ConfigureAwait(false);

        var browserConversations = await _store.ListBrowserConversationsAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(activeTip.PredecessorConversationId))
        {
            var previousBrowser = browserConversations.FirstOrDefault(x => StringComparer.Ordinal.Equals(x.ConversationId, activeTip.PredecessorConversationId));
            if (previousBrowser is not null)
                await _store.SaveBrowserConversationAsync(previousBrowser with { SuccessorConversationId = historical.ConversationId, State = ConversationLifecycleState.Archived }, cancellationToken).ConfigureAwait(false);
        }
        if (previousPredecessorId is not null)
        {
            var previousDomain = await _store.LoadConversationAsync(previousPredecessorId.Value, cancellationToken).ConfigureAwait(false);
            if (previousDomain is not null)
                await _store.SaveConversationAsync(previousDomain with { SuccessorId = historicalId, State = ConversationState.Archived }, runId, cancellationToken).ConfigureAwait(false);
        }

        await _store.SaveBrowserConversationAsync(historical, cancellationToken).ConfigureAwait(false);
        await _store.SaveBrowserConversationAsync(activeTip with { State = ConversationLifecycleState.RolloverPending, RolloverReason = reason }, cancellationToken).ConfigureAwait(false);
        await _store.SaveConversationAsync(historicalDomain, runId, cancellationToken).ConfigureAwait(false);
        await _store.SaveConversationAsync(domainTip with { State = ConversationState.Rotating, RolloverReason = reason }, runId, cancellationToken).ConfigureAwait(false);
        await _store.SaveLogicalAgentAsync(session with { CurrentConversationId = historicalId, State = LogicalSessionState.Recovering }, cancellationToken).ConfigureAwait(false);

        state = state with { Status = "PREPARED", UpdatedAt = DateTimeOffset.UtcNow };
        await SaveRecoveryStateAsync(state, cancellationToken).ConfigureAwait(false);
        return new(runtime, activeTip, domainTip, historical, historicalDomain, session, state);
    }

    private async Task<ConversationRecord?> FindOrCreateActiveBrowserConversationAsync(BrowserRuntimeRecord runtime, CancellationToken cancellationToken)
    {
        var conversations = (await _store.ListBrowserConversationsAsync(cancellationToken).ConfigureAwait(false))
            .Where(x => StringComparer.Ordinal.Equals(x.ProjectRunId, runtime.ProjectRunId) && StringComparer.Ordinal.Equals(x.LogicalAgentId, runtime.LogicalAgentId))
            .ToArray();
        var matching = conversations.FirstOrDefault(x => x.State == ConversationLifecycleState.Active && StringComparer.Ordinal.Equals(x.ConversationId, runtime.ConversationIdentity));
        if (matching is not null) return matching;
        if (string.IsNullOrWhiteSpace(runtime.ConversationIdentity) || string.IsNullOrWhiteSpace(runtime.ProviderConversationIdentity)) return null;
        var sequence = conversations.Count == 0 ? 1 : Math.Max(1, conversations.Max(x => x.Sequence));
        var created = new ConversationRecord
        {
            ConversationId = runtime.ConversationIdentity,
            LogicalAgentId = runtime.LogicalAgentId,
            ProjectRunId = runtime.ProjectRunId,
            Sequence = sequence,
            UrlOrProviderIdentity = runtime.ProviderConversationIdentity,
            CreatedAt = DateTimeOffset.UtcNow,
            State = ConversationLifecycleState.Active
        };
        await _store.SaveBrowserConversationAsync(created, cancellationToken).ConfigureAwait(false);
        return created;
    }

    private async Task RepairInterruptedRolloversAsync(CancellationToken cancellationToken)
    {
        var run = PccHostRecoveryAccess.Run(_host);
        if (run is null) return;
        var runtimes = (await _registry.ListAsync(cancellationToken).ConfigureAwait(false))
            .Where(x => StringComparer.Ordinal.Equals(x.ProjectRunId, run.Id.ToString()))
            .ToArray();
        foreach (var runtime in runtimes)
        {
            var checkpoint = await _store.LoadCheckpointAsync(RecoveryKey(runtime.ProjectRunId, runtime.LogicalAgentId), cancellationToken).ConfigureAwait(false);
            if (checkpoint is null) continue;
            RolloverRecoveryState? state;
            try { state = JsonSerializer.Deserialize<RolloverRecoveryState>(checkpoint.Payload); }
            catch (JsonException) { continue; }
            if (state is null || state.Status is "COMMITTED" or "FAILED_RECOVERED") continue;

            var session = Guid.TryParse(state.LogicalAgentId, out var agentGuid)
                ? await _store.LoadLogicalAgentAsync(new LogicalAgentId(agentGuid), cancellationToken).ConfigureAwait(false)
                : null;
            var candidateCommittedInDomain = session?.CurrentConversationId?.ToString() == state.StableConversationId;
            if (candidateCommittedInDomain && !string.IsNullOrWhiteSpace(state.CandidateProviderIdentity) && !string.IsNullOrWhiteSpace(state.LifecycleCheckpointId))
            {
                await FinishBrowserCommitAfterCrashAsync(state, cancellationToken).ConfigureAwait(false);
                await SaveRecoveryStateAsync(state with { Status = "COMMITTED", UpdatedAt = DateTimeOffset.UtcNow }, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await RollbackFromRecoveryStateAsync(state, "INTERRUPTED_ROLLOVER_RECOVERED_TO_PREDECESSOR", cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task FinishBrowserCommitAfterCrashAsync(RolloverRecoveryState state, CancellationToken cancellationToken)
    {
        var conversations = await _store.ListBrowserConversationsAsync(cancellationToken).ConfigureAwait(false);
        var historical = conversations.FirstOrDefault(x => StringComparer.Ordinal.Equals(x.ConversationId, state.HistoricalPredecessorId));
        if (historical is null) return;
        var archived = historical with { State = ConversationLifecycleState.Archived, RetiredAt = DateTimeOffset.UtcNow, SuccessorConversationId = state.StableConversationId };
        var successor = new ConversationRecord
        {
            ConversationId = state.StableConversationId,
            LogicalAgentId = state.LogicalAgentId,
            ProjectRunId = state.ProjectRunId,
            Sequence = checked(state.PredecessorSequence + 1),
            UrlOrProviderIdentity = state.CandidateProviderIdentity!,
            CreatedAt = state.UpdatedAt,
            PredecessorConversationId = state.HistoricalPredecessorId,
            RolloverReason = state.Reason,
            State = ConversationLifecycleState.Active
        };
        await ((IConversationLifecycleStore)_store).CommitRolloverAsync(archived, successor, state.LifecycleCheckpointId!, cancellationToken).ConfigureAwait(false);
        var runtime = await _registry.GetAsync(state.RuntimeId, cancellationToken).ConfigureAwait(false);
        if (runtime is not null)
            await _sessions.BindDispatchTargetAsync(runtime.RuntimeId, runtime.TaskId ?? $"rollover:{state.LogicalAgentId}", state.StableConversationId, state.CandidateProviderIdentity!, cancellationToken).ConfigureAwait(false);
    }

    private async Task RollbackFromRecoveryStateAsync(RolloverRecoveryState state, string reason, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(state.ProjectRunId, out var runGuid) || !Guid.TryParse(state.LogicalAgentId, out var agentGuid) ||
            !Guid.TryParse(state.StableConversationId, out var stableGuid) || !Guid.TryParse(state.HistoricalPredecessorId, out var historicalGuid)) return;
        var runId = new ProjectRunId(runGuid);
        var agentId = new LogicalAgentId(agentGuid);
        var stableId = new ConversationId(stableGuid);
        var historicalId = new ConversationId(historicalGuid);
        var runtime = await _registry.GetAsync(state.RuntimeId, cancellationToken).ConfigureAwait(false);
        if (runtime is not null)
        {
            await NavigateAsync(_sessions, runtime.RuntimeId, ProviderUrl(state.PredecessorProviderIdentity), cancellationToken).ConfigureAwait(false);
            await _sessions.BindDispatchTargetAsync(runtime.RuntimeId, runtime.TaskId ?? $"rollover:{state.LogicalAgentId}", state.StableConversationId, state.PredecessorProviderIdentity, cancellationToken).ConfigureAwait(false);
        }

        var browser = await _store.ListBrowserConversationsAsync(cancellationToken).ConfigureAwait(false);
        var stable = browser.FirstOrDefault(x => StringComparer.Ordinal.Equals(x.ConversationId, state.StableConversationId));
        if (stable is not null)
            await _store.SaveBrowserConversationAsync(stable with { Sequence = state.PredecessorSequence, UrlOrProviderIdentity = state.PredecessorProviderIdentity, PredecessorConversationId = state.PreviousArchivedPredecessorId, SuccessorConversationId = null, RetiredAt = null, State = ConversationLifecycleState.Active }, cancellationToken).ConfigureAwait(false);
        var historical = browser.FirstOrDefault(x => StringComparer.Ordinal.Equals(x.ConversationId, state.HistoricalPredecessorId));
        if (historical is not null)
            await _store.SaveBrowserConversationAsync(historical with { State = ConversationLifecycleState.FailedCandidate, RetiredAt = DateTimeOffset.UtcNow }, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(state.PreviousArchivedPredecessorId))
        {
            var previous = browser.FirstOrDefault(x => StringComparer.Ordinal.Equals(x.ConversationId, state.PreviousArchivedPredecessorId));
            if (previous is not null)
                await _store.SaveBrowserConversationAsync(previous with { SuccessorConversationId = state.StableConversationId, State = ConversationLifecycleState.Archived }, cancellationToken).ConfigureAwait(false);
        }

        var stableDomain = await _store.LoadConversationAsync(stableId, cancellationToken).ConfigureAwait(false);
        if (stableDomain is not null)
            await _store.SaveConversationAsync(stableDomain with { Sequence = state.PredecessorSequence, ProviderIdentity = state.PredecessorProviderIdentity, Url = ProviderUrl(state.PredecessorProviderIdentity), State = ConversationState.Active, RetiredAt = null, PredecessorId = ParseConversation(state.PreviousArchivedPredecessorId), SuccessorId = null, RolloverReason = null }, runId, cancellationToken).ConfigureAwait(false);
        var historicalDomain = await _store.LoadConversationAsync(historicalId, cancellationToken).ConfigureAwait(false);
        if (historicalDomain is not null)
            await _store.SaveConversationAsync(historicalDomain with { State = ConversationState.Failed, RetiredAt = DateTimeOffset.UtcNow }, runId, cancellationToken).ConfigureAwait(false);
        var previousDomainId = ParseConversation(state.PreviousArchivedPredecessorId);
        if (previousDomainId is not null)
        {
            var previousDomain = await _store.LoadConversationAsync(previousDomainId.Value, cancellationToken).ConfigureAwait(false);
            if (previousDomain is not null)
                await _store.SaveConversationAsync(previousDomain with { SuccessorId = stableId, State = ConversationState.Archived }, runId, cancellationToken).ConfigureAwait(false);
        }
        var session = await _store.LoadLogicalAgentAsync(agentId, cancellationToken).ConfigureAwait(false);
        if (session is not null)
            await _store.SaveLogicalAgentAsync(session with { CurrentConversationId = stableId, State = LogicalSessionState.Active }, cancellationToken).ConfigureAwait(false);
        await SaveRecoveryStateAsync(state with { Status = "FAILED_RECOVERED", Reason = reason, UpdatedAt = DateTimeOffset.UtcNow }, cancellationToken).ConfigureAwait(false);
    }

    private async Task NormalizeActiveConversationTruthAsync(CancellationToken cancellationToken)
    {
        var run = PccHostRecoveryAccess.Run(_host);
        if (run is null) return;
        var conversations = (await _store.ListBrowserConversationsAsync(cancellationToken).ConfigureAwait(false))
            .Where(x => StringComparer.Ordinal.Equals(x.ProjectRunId, run.Id.ToString()))
            .ToArray();
        var runtimes = (await _registry.ListAsync(cancellationToken).ConfigureAwait(false))
            .Where(x => !x.IsArchived && x.State is not BrowserSessionState.Killed && StringComparer.Ordinal.Equals(x.ProjectRunId, run.Id.ToString()))
            .ToArray();
        foreach (var group in conversations.GroupBy(x => x.LogicalAgentId, StringComparer.Ordinal))
        {
            if (!Guid.TryParse(group.Key, out var agentGuid)) continue;
            var session = await _store.LoadLogicalAgentAsync(new LogicalAgentId(agentGuid), cancellationToken).ConfigureAwait(false);
            var current = session?.CurrentConversationId?.ToString() ?? runtimes.FirstOrDefault(x => StringComparer.Ordinal.Equals(x.LogicalAgentId, group.Key))?.ConversationIdentity;
            if (string.IsNullOrWhiteSpace(current)) continue;
            foreach (var conversation in group.Where(x => x.State == ConversationLifecycleState.Active && !StringComparer.Ordinal.Equals(x.ConversationId, current)))
                await _store.SaveBrowserConversationAsync(conversation with { State = ConversationLifecycleState.Archived, RetiredAt = conversation.RetiredAt ?? DateTimeOffset.UtcNow }, cancellationToken).ConfigureAwait(false);
            var selected = group.FirstOrDefault(x => StringComparer.Ordinal.Equals(x.ConversationId, current));
            if (selected is not null && selected.State != ConversationLifecycleState.Active)
                await _store.SaveBrowserConversationAsync(selected with { State = ConversationLifecycleState.Active, RetiredAt = null }, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task TryResumeRecoveredAutopilotAsync(CancellationToken cancellationToken)
    {
        if (!string.Equals(PccHostRecoveryAccess.Autopilot(_host), "RECOVERY_REQUIRED", StringComparison.Ordinal)) return;
        var run = PccHostRecoveryAccess.Run(_host);
        if (run is null || !string.IsNullOrWhiteSpace(PccHostRecoveryAccess.RuntimeHealthFault(_host))) return;
        var operatorPause = await _store.LoadCheckpointAsync($"autopilot-pause:{run.Id}", cancellationToken).ConfigureAwait(false);
        if (operatorPause?.Payload.Contains("\"paused\":true", StringComparison.Ordinal) == true) return;

        var runtimes = await _registry.ListAsync(cancellationToken).ConfigureAwait(false);
        var reconciler = new BrowserSessionReconciliationService();
        foreach (var runtime in runtimes.Where(x => !x.IsArchived && x.State is not BrowserSessionState.Killed && StringComparer.Ordinal.Equals(x.ProjectRunId, run.Id.ToString())))
        {
            if (!Guid.TryParse(runtime.LogicalAgentId, out var agentGuid)) return;
            var session = await _store.LoadLogicalAgentAsync(new LogicalAgentId(agentGuid), cancellationToken).ConfigureAwait(false);
            if (session is null || reconciler.Reconcile(session, runtime).Outcome != BrowserReconciliationKind.MATCHED) return;
        }
        await PccHostRecoveryAccess.NewSendPause(_host).ResumeNewSendsAsync("Interrupted conversation rollover repaired before AutoResume.", cancellationToken).ConfigureAwait(false);
        PccHostRecoveryAccess.Autopilot(_host) = "READY";
        PccHostConversationAccess.EnsureAutopilotLoop(_host);
    }

    private Task SaveRecoveryStateAsync(RolloverRecoveryState state, CancellationToken cancellationToken) =>
        _store.SaveCheckpointAsync(new DurableCheckpoint(RecoveryKey(state.ProjectRunId, state.LogicalAgentId), state.ProjectRunId, "conversation-rollover-runtime-v1", JsonSerializer.Serialize(state), DateTimeOffset.UtcNow), cancellationToken);

    private static string BuildContinuationPacket(BrowserRuntimeRecord runtime, RolloverTransactionContext context) =>
        string.Join('\n',
            "PCC_EXECUTIVE_CONTINUATION_PACKET",
            $"PROJECT_RUN: {runtime.ProjectRunId}",
            $"LOGICAL_AGENT: {runtime.LogicalAgentId}",
            $"WORKER_SLOT: {runtime.WorkerSlotId ?? "MANAGER"}",
            $"CURRENT_TASK: {runtime.TaskId ?? "NONE"}",
            $"PREVIOUS_PROVIDER_CONVERSATION: {context.OriginalActiveTip.UrlOrProviderIdentity}",
            $"ROLLOVER_REASON: {context.Recovery.Reason}",
            "Continue the same logical agent, task, constraints, and durable project state. Do not replay completed sends.");

    private static string RecoveryKey(string runId, string logicalAgentId) => $"conversation-rollover:{runId}:{logicalAgentId}";
    private static ConversationId? ParseConversation(string? value) => Guid.TryParse(value, out var guid) ? new ConversationId(guid) : null;
    private static string ProviderUrl(string providerIdentity) => Uri.TryCreate(providerIdentity, UriKind.Absolute, out var uri) ? uri.ToString() : string.Equals(providerIdentity, "NEW", StringComparison.OrdinalIgnoreCase) ? "https://chatgpt.com/" : $"https://chatgpt.com/c/{providerIdentity}";

    private static async Task<bool> NavigateAsync(BrowserSessionController sessions, string runtimeId, string url, CancellationToken cancellationToken)
    {
        var host = BrowserSessionRolloverAccess.RuntimeHost(sessions);
        if (host is not IPlaywrightPageProvider pages) return false;
        var page = await pages.GetPageAsync(runtimeId, cancellationToken).ConfigureAwait(false);
        if (page is null) return false;
        try
        {
            await page.GotoAsync(url, new() { WaitUntil = Microsoft.Playwright.WaitUntilState.DOMContentLoaded, Timeout = 30_000 }).ConfigureAwait(false);
            return true;
        }
        catch (Microsoft.Playwright.PlaywrightException) { return false; }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        try { await _monitor.ConfigureAwait(false); } catch (OperationCanceledException) { }
        _operation.Dispose();
        _lifetime.Dispose();
    }

    private sealed class StableTipConversationCreator : IConversationCreator
    {
        private readonly RolloverTransactionContext _context;
        public StableTipConversationCreator(RolloverTransactionContext context) => _context = context;
        public Task<ConversationCreationResult> CreateAsync(ConversationRecord predecessor, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!StringComparer.Ordinal.Equals(predecessor.ConversationId, _context.HistoricalPredecessor.ConversationId))
                throw new InvalidOperationException("Rollover predecessor changed after checkpoint preparation.");
            return Task.FromResult(new ConversationCreationResult(_context.OriginalActiveTip.ConversationId, "NEW"));
        }
    }

    private sealed class RuntimeContinuationSender : IContinuationSender
    {
        private readonly PccExecutiveRuntimeHost _host;
        private readonly IBrowserRuntimeRegistry _registry;
        private readonly BrowserSessionController _sessions;
        private readonly IOwnershipProofService _ownership;
        private readonly IChatGptBrowserAdapter _adapter;
        private readonly RolloverTransactionContext _context;
        public RuntimeContinuationSender(PccExecutiveRuntimeHost host, IBrowserRuntimeRegistry registry, BrowserSessionController sessions, IOwnershipProofService ownership, IChatGptBrowserAdapter adapter, RolloverTransactionContext context)
        { _host = host; _registry = registry; _sessions = sessions; _ownership = ownership; _adapter = adapter; _context = context; }

        public async Task<bool> SendContinuationAsync(ConversationRecord candidate, string checkpointId, string continuationPacket, CancellationToken cancellationToken = default)
        {
            _context.Recovery = _context.Recovery with { LifecycleCheckpointId = checkpointId, Status = "CANDIDATE_SAVED", UpdatedAt = DateTimeOffset.UtcNow };
            await _context.SaveAsync(cancellationToken).ConfigureAwait(false);
            var runtime = await _registry.GetAsync(_context.Runtime.RuntimeId, cancellationToken).ConfigureAwait(false);
            if (runtime is null) return false;
            var proof = await _ownership.ProveAsync(runtime, cancellationToken).ConfigureAwait(false);
            if (!proof.IsProven) return false;
            if (!await NavigateAsync(_sessions, runtime.RuntimeId, "https://chatgpt.com/", cancellationToken).ConfigureAwait(false)) return false;
            var taskId = runtime.TaskId ?? $"rollover:{runtime.LogicalAgentId}";
            var bound = await _sessions.BindDispatchTargetAsync(runtime.RuntimeId, taskId, candidate.ConversationId, "NEW", cancellationToken).ConfigureAwait(false);
            if (!bound.Succeeded || bound.Runtime is null) return false;
            runtime = bound.Runtime;
            var expected = new BrowserDispatchExpectation(runtime.ProjectRunId, runtime.LogicalAgentId, taskId, candidate.ConversationId, "NEW", runtime.WorkerSlotId);
            var semantic = await _adapter.InspectAsync(runtime, expected, cancellationToken).ConfigureAwait(false);
            if (semantic.Auth.State is AuthState.LoginRequired or AuthState.Challenge || semantic.Health.State is PageHealth.RateLimited or PageHealth.Offline)
            {
                var resilience = new ChatGptResilienceClassifier().Classify(semantic, DateTimeOffset.UtcNow - runtime.LastActivityAt);
                await PccHostConversationAccess.PersistGlobalHealthPauseAsync(_host, resilience, runtime.RuntimeId, cancellationToken).ConfigureAwait(false);
                return false;
            }
            if (semantic.Auth.State != AuthState.Authenticated || semantic.Health.State != PageHealth.Healthy || semantic.Input.State != InputState.Ready || semantic.Conversation.State != ConversationMatch.Match) return false;

            _context.ExpectedAcknowledgement = $"CONTINUATION_ACK {_context.Runtime.ProjectRunId} {_context.Runtime.LogicalAgentId}";
            var prompt = $"{continuationPacket}\nCHECKPOINT_ID: {checkpointId}\nReply exactly with: {_context.ExpectedAcknowledgement}";
            var submission = await _adapter.SubmitAsync(runtime, expected, prompt, cancellationToken).ConfigureAwait(false);
            if (!submission.ProvenSubmitted) return false;

            string? providerIdentity = null;
            for (var attempt = 0; attempt < 40 && string.IsNullOrWhiteSpace(providerIdentity); attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
                providerIdentity = await _adapter.GetCurrentConversationIdentityAsync(runtime, cancellationToken).ConfigureAwait(false);
            }
            if (string.IsNullOrWhiteSpace(providerIdentity)) return false;
            var rebound = await _sessions.BindDispatchTargetAsync(runtime.RuntimeId, taskId, candidate.ConversationId, providerIdentity, cancellationToken).ConfigureAwait(false);
            if (!rebound.Succeeded) return false;
            _context.Recovery = _context.Recovery with { CandidateProviderIdentity = providerIdentity, Status = "CONTINUATION_SUBMITTED", UpdatedAt = DateTimeOffset.UtcNow };
            await _context.SaveAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
    }

    private sealed class RuntimeContinuationValidator : IContinuationValidator
    {
        private readonly IBrowserRuntimeRegistry _registry;
        private readonly IChatGptBrowserAdapter _adapter;
        private readonly RolloverTransactionContext _context;
        public RuntimeContinuationValidator(IBrowserRuntimeRegistry registry, IChatGptBrowserAdapter adapter, RolloverTransactionContext context)
        { _registry = registry; _adapter = adapter; _context = context; }
        public async Task<ContinuationValidationResult> ValidateAsync(ConversationRecord candidate, CancellationToken cancellationToken = default)
        {
            for (var attempt = 0; attempt < 120; attempt++)
            {
                var runtime = await _registry.GetAsync(_context.Runtime.RuntimeId, cancellationToken).ConfigureAwait(false);
                if (runtime is null || string.IsNullOrWhiteSpace(runtime.ProviderConversationIdentity) || string.Equals(runtime.ProviderConversationIdentity, "NEW", StringComparison.OrdinalIgnoreCase))
                    return new(false, "SUCCESSOR_PROVIDER_IDENTITY_NOT_PROVEN");
                var expectation = new BrowserDispatchExpectation(runtime.ProjectRunId, runtime.LogicalAgentId, runtime.TaskId ?? $"rollover:{runtime.LogicalAgentId}", candidate.ConversationId, runtime.ProviderConversationIdentity, runtime.WorkerSlotId);
                var semantic = await _adapter.InspectAsync(runtime, expectation, cancellationToken).ConfigureAwait(false);
                if (semantic.Auth.State is AuthState.LoginRequired or AuthState.Challenge) return new(false, semantic.Auth.State.ToString().ToUpperInvariant());
                if (semantic.Health.State is PageHealth.RateLimited or PageHealth.Offline or PageHealth.TempError) return new(false, semantic.Health.State.ToString().ToUpperInvariant());
                if (semantic.Auth.State == AuthState.Authenticated && semantic.Health.State == PageHealth.Healthy && semantic.Conversation.State == ConversationMatch.Match &&
                    semantic.Generation.State == GenerationState.Complete && semantic.ResponseCompleteness == ResponseCompleteness.Complete &&
                    !string.IsNullOrWhiteSpace(_context.ExpectedAcknowledgement) &&
                    (semantic.CapturedResponseText?.Contains(_context.ExpectedAcknowledgement, StringComparison.Ordinal) ?? false))
                {
                    _context.Recovery = _context.Recovery with { Status = "SUCCESSOR_VALIDATED", UpdatedAt = DateTimeOffset.UtcNow };
                    await _context.SaveAsync(cancellationToken).ConfigureAwait(false);
                    return new(true, "SUCCESSOR_IDENTITY_HEALTH_AND_ACKNOWLEDGEMENT_PROVEN");
                }
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
            }
            return new(false, "SUCCESSOR_ACKNOWLEDGEMENT_TIMEOUT");
        }
    }

    private sealed class StableTipConversationLifecycleStore : IConversationLifecycleStore
    {
        private readonly SqliteStateStore _store;
        private readonly DurableConversationLifecycleStore _domain;
        private readonly IBrowserRuntimeRegistry _registry;
        private readonly BrowserSessionController _sessions;
        private readonly IChatGptBrowserAdapter _adapter;
        private readonly RolloverTransactionContext _context;
        private bool _rolledBack;
        public StableTipConversationLifecycleStore(SqliteStateStore store, IBrowserRuntimeRegistry registry, BrowserSessionController sessions, IChatGptBrowserAdapter adapter, RolloverTransactionContext context)
        { _store = store; _domain = new DurableConversationLifecycleStore(store); _registry = registry; _sessions = sessions; _adapter = adapter; _context = context; }

        public async Task SaveCandidateAsync(ConversationRecord candidate, string checkpointId, CancellationToken cancellationToken = default)
        {
            await ((IConversationLifecycleStore)_store).SaveCandidateAsync(candidate, checkpointId, cancellationToken).ConfigureAwait(false);
            await _domain.SaveCandidateAsync(candidate, checkpointId, cancellationToken).ConfigureAwait(false);
            _context.Recovery = _context.Recovery with { LifecycleCheckpointId = checkpointId, Status = "CANDIDATE_SAVED", UpdatedAt = DateTimeOffset.UtcNow };
            await _context.SaveAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task CommitRolloverAsync(ConversationRecord predecessorArchived, ConversationRecord successorActive, string checkpointId, CancellationToken cancellationToken = default)
        {
            var runtime = await _registry.GetAsync(_context.Runtime.RuntimeId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Rollover runtime disappeared before commit.");
            var provider = runtime.ProviderConversationIdentity;
            if (string.IsNullOrWhiteSpace(provider) || string.Equals(provider, "NEW", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Successor provider identity is not authoritative at commit.");
            var successor = successorActive with { UrlOrProviderIdentity = provider };
            await _domain.CommitRolloverAsync(predecessorArchived, successor, checkpointId, cancellationToken).ConfigureAwait(false);
            if (Guid.TryParse(successor.ConversationId, out var successorGuid) && Guid.TryParse(successor.ProjectRunId, out var runGuid))
            {
                var domainSuccessor = await _store.LoadConversationAsync(new ConversationId(successorGuid), cancellationToken).ConfigureAwait(false);
                if (domainSuccessor is not null)
                    await _store.SaveConversationAsync(domainSuccessor with { ProviderIdentity = provider, Url = ProviderUrl(provider), State = ConversationState.Active }, new ProjectRunId(runGuid), cancellationToken).ConfigureAwait(false);
            }
            await ((IConversationLifecycleStore)_store).CommitRolloverAsync(predecessorArchived, successor, checkpointId, cancellationToken).ConfigureAwait(false);
            _context.Recovery = _context.Recovery with { CandidateProviderIdentity = provider, LifecycleCheckpointId = checkpointId, Status = "COMMITTED", UpdatedAt = DateTimeOffset.UtcNow };
            await _context.SaveAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task RecordFailedRolloverAsync(ConversationRecord predecessorStillActive, ConversationRecord? failedCandidate, string reason, CancellationToken cancellationToken = default)
        {
            await ((IConversationLifecycleStore)_store).RecordFailedRolloverAsync(predecessorStillActive, failedCandidate, reason, cancellationToken).ConfigureAwait(false);
            await _domain.RecordFailedRolloverAsync(predecessorStillActive, failedCandidate, reason, cancellationToken).ConfigureAwait(false);
            await EnsureRollbackAsync(reason, cancellationToken).ConfigureAwait(false);
        }

        public async Task EnsureRollbackAsync(string reason, CancellationToken cancellationToken)
        {
            if (_rolledBack) return;
            _rolledBack = true;
            var owner = _context.Owner;
            await owner.RollbackContextAsync(_context, reason, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RollbackContextAsync(RolloverTransactionContext context, string reason, CancellationToken cancellationToken) =>
        await RollbackFromRecoveryStateAsync(context.Recovery, reason, cancellationToken).ConfigureAwait(false);

    private sealed class RolloverTransactionContext
    {
        public RolloverTransactionContext(BrowserRuntimeRecord runtime, ConversationRecord originalActiveTip, PCCExecutive.Domain.Conversation originalDomainTip, ConversationRecord historicalPredecessor, PCCExecutive.Domain.Conversation historicalDomainPredecessor, LogicalAgentSession originalSession, RolloverRecoveryState recovery)
        { Runtime = runtime; OriginalActiveTip = originalActiveTip; OriginalDomainTip = originalDomainTip; HistoricalPredecessor = historicalPredecessor; HistoricalDomainPredecessor = historicalDomainPredecessor; OriginalSession = originalSession; Recovery = recovery; }
        public required AutonomousConversationRolloverRuntime Owner { get; init; }
        public BrowserRuntimeRecord Runtime { get; }
        public ConversationRecord OriginalActiveTip { get; }
        public PCCExecutive.Domain.Conversation OriginalDomainTip { get; }
        public ConversationRecord HistoricalPredecessor { get; }
        public PCCExecutive.Domain.Conversation HistoricalDomainPredecessor { get; }
        public LogicalAgentSession OriginalSession { get; }
        public RolloverRecoveryState Recovery { get; set; }
        public string? ExpectedAcknowledgement { get; set; }
        public Task SaveAsync(CancellationToken cancellationToken) => Owner.SaveRecoveryStateAsync(Recovery, cancellationToken);
    }

    private sealed record RolloverRecoveryState(
        string ProjectRunId,
        string LogicalAgentId,
        string RuntimeId,
        string StableConversationId,
        string HistoricalPredecessorId,
        int PredecessorSequence,
        string PredecessorProviderIdentity,
        string? PreviousArchivedPredecessorId,
        string? CandidateProviderIdentity,
        string? LifecycleCheckpointId,
        string Reason,
        string Status,
        DateTimeOffset UpdatedAt);
}

internal static class RecoveryGatewayRolloverAccess
{
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_inner")]
    internal static extern ref PccExecutiveRuntimeHost Inner(RecoveryCompletionPresentationGateway gateway);
}

internal static class PccHostConversationAccess
{
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_runtimeRegistry")]
    internal static extern ref IBrowserRuntimeRegistry RuntimeRegistry(PccExecutiveRuntimeHost host);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_sessions")]
    internal static extern ref BrowserSessionController Sessions(PccExecutiveRuntimeHost host);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_ownership")]
    internal static extern ref IOwnershipProofService Ownership(PccExecutiveRuntimeHost host);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_browserAdapter")]
    internal static extern ref IChatGptBrowserAdapter BrowserAdapter(PccExecutiveRuntimeHost host);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "PersistGlobalHealthPauseAsync")]
    internal static extern Task PersistGlobalHealthPauseAsync(PccExecutiveRuntimeHost host, ResilienceDecision resilience, string runtimeId, CancellationToken cancellationToken);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "EnsureAutopilotLoop")]
    internal static extern void EnsureAutopilotLoop(PccExecutiveRuntimeHost host);
}

internal static class BrowserSessionRolloverAccess
{
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_host")]
    internal static extern ref IBrowserRuntimeHost RuntimeHost(BrowserSessionController controller);
}
