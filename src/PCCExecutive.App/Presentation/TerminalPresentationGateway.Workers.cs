using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PCCExecutive.Application;
using PCCExecutive.Browser;
using PCCExecutive.Domain;
using PCCExecutive.Infrastructure;

namespace PCCExecutive.App.Presentation;

public sealed partial class TerminalPresentationGateway
{
    private async Task<IReadOnlyList<WorkerSummary>> BuildWorkersAsync(IReadOnlyList<SessionSummary> sessions, CancellationToken cancellationToken)
    {
        if (_run is null || _workerAgentIds.Length == 0) return Array.Empty<WorkerSummary>();
        var result = new List<WorkerSummary>();
        for (var index = 0; index < _workerAgentIds.Length; index++)
        {
            var id = _workerAgentIds[index];
            var agent = await _store.LoadLogicalAgentAsync(id, cancellationToken).ConfigureAwait(false);
            if (agent is null) continue;
            WorkerTask? task = null;
            if (agent.CurrentTaskId is { } taskId)
                task = await _store.LoadTaskAsync(taskId, cancellationToken).ConfigureAwait(false);
            var logicalName = $"Worker {index + 1}";
            var session = sessions.FirstOrDefault(x => StringComparer.Ordinal.Equals(x.LogicalAgentId, id.ToString()));
            result.Add(new WorkerSummary(
                id.ToString(), logicalName, "Worker", agent.State.ToString().ToUpperInvariant(), null,
                task?.Objective ?? "No task assigned",
                session?.Health ?? HealthState.Unknown,
                null)
            {
                LogicalAgentId = id.ToString(),
                TaskScope = task is null ? null : ScopeText(task.Scope),
                ConversationId = agent.CurrentConversationId?.ToString(),
                DispatchState = null,
                Blocker = task?.State == TaskState.Blocked ? "TASK_BLOCKED" : null,
                LastActivity = session?.LastActivity
            });
        }
        return result;
    }

    private async Task<IReadOnlyList<TaskSummary>> BuildTasksAsync(IReadOnlyList<WorkerSummary> workers, CancellationToken cancellationToken)
    {
        if (_run is null || _workerAgentIds.Length == 0) return Array.Empty<TaskSummary>();
        var tasks = new List<TaskSummary>();
        for (var index = 0; index < _workerAgentIds.Length; index++)
        {
            var agent = await _store.LoadLogicalAgentAsync(_workerAgentIds[index], cancellationToken).ConfigureAwait(false);
            if (agent?.CurrentTaskId is not { } taskId) continue;
            var task = await _store.LoadTaskAsync(taskId, cancellationToken).ConfigureAwait(false);
            if (task is null) continue;
            tasks.Add(new TaskSummary(task.Id.ToString(), task.Objective, task.State.ToString(), "—", $"Worker {index + 1}", false)
            {
                Wave = CurrentWaveDisplay(),
                Blocker = task.State == TaskState.Blocked ? "TASK_BLOCKED" : null,
                Scope = ScopeText(task.Scope)
            });
        }
        return tasks;
    }

    private async Task<IReadOnlyList<ConversationHistorySummary>> BuildConversationsAsync(CancellationToken cancellationToken)
    {
        if (_run is null) return Array.Empty<ConversationHistorySummary>();
        var result = new List<ConversationHistorySummary>();
        var agents = new List<(string Name, LogicalAgentId Id)>();
        if (_managerAgentId is { } managerId) agents.Add(("Manager", managerId));
        for (var i = 0; i < _workerAgentIds.Length; i++) agents.Add(($"Worker {i + 1}", _workerAgentIds[i]));
        foreach (var item in agents)
        {
            var agent = await _store.LoadLogicalAgentAsync(item.Id, cancellationToken).ConfigureAwait(false);
            if (agent?.CurrentConversationId is not { } conversationId) continue;
            var conversation = await _store.LoadConversationAsync(conversationId, cancellationToken).ConfigureAwait(false);
            if (conversation is null) continue;
            result.Add(new ConversationHistorySummary(
                item.Name, conversation.Sequence, conversation.State.ToString().ToUpperInvariant(), conversation.CreatedAt,
                conversation.RetiredAt, conversation.RolloverReason, conversation.CheckpointId?.ToString(),
                conversation.PredecessorId?.ToString(), conversation.SuccessorId?.ToString(), conversation.ProviderIdentity));
        }
        return result;
    }

    private async Task<HealthState> InspectHealthAsync(BrowserRuntimeRecord runtime, CancellationToken cancellationToken)
    {
        if (runtime.State == BrowserSessionState.Recovering) return HealthState.Recovering;
        if (runtime.State == BrowserSessionState.FailedRequiresAttention) return HealthState.Failed;
        try
        {
            var snapshot = await _adapter.InspectAsync(runtime, new BrowserDispatchExpectation(
                runtime.ProjectRunId,
                runtime.LogicalAgentId,
                runtime.TaskId ?? string.Empty,
                runtime.ConversationIdentity ?? string.Empty,
                runtime.ProviderConversationIdentity ?? string.Empty), cancellationToken).ConfigureAwait(false);

            if (snapshot.Auth.State == AuthState.Challenge) return HealthState.Challenge;
            if (snapshot.Auth.State == AuthState.LoginRequired) return HealthState.LoginRequired;
            if (snapshot.Health.State == PageHealth.Offline) return HealthState.Offline;
            if (snapshot.Health.State == PageHealth.RateLimited) return HealthState.RateLimited;
            if (snapshot.Health.State == PageHealth.TempError) return HealthState.TemporaryError;
            if (snapshot.ResponseCompleteness == ResponseCompleteness.Partial) return HealthState.PartialResponse;
            if (HasEvidence(snapshot, "context-limit", "conversation-too-long", "maximum conversation length")) return HealthState.ContextLimitDetected;
            if (HasEvidence(snapshot, "session-expired", "session has expired")) return HealthState.SessionExpired;
            if (snapshot.Generation.State == GenerationState.Generating) return HealthState.Generating;
            if (snapshot.Health.State == PageHealth.Slow) return HealthState.Slow;
            if (!new ChatGptAdapterDriftGuard().Evaluate(snapshot).IsCertain) return HealthState.AdapterUncertain;
            if (snapshot.Auth.State == AuthState.Authenticated && snapshot.Input.State == InputState.Ready) return HealthState.Ready;
            return HealthState.Unknown;
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException)
        {
            _recovery.Insert(0, new RecoveryEventSummary(DateTimeOffset.UtcNow, "BROWSER_ADAPTER_UNCERTAIN", ex.Message, true));
            return HealthState.AdapterUncertain;
        }
    }

    private IReadOnlyList<EvidenceGateSummary> BuildEvidence()
    {
        var gates = new List<EvidenceGateSummary>
        {
            new("PCC Routing", _lastResolution?.IsSuccess == true ? "PASS" : _run is null ? "UNKNOWN" : "PARTIAL", null,
                _lastResolution?.Project is { } route ? $"{route.ProjectControlId} · {route.Provenance.SourceSha}" : _lastResolution?.Message ?? "No project selected")
            {
                Freshness = _lastResolution?.Project?.Provenance.Freshness.ToString().ToUpperInvariant() ?? "UNKNOWN",
                ExactHead = _lastResolution?.Project?.Provenance.SourceSha
            },
            new("GitHub / CI", _baseline is null ? "UNKNOWN" : "LIVE", null,
                _baseline is null ? "No live baseline loaded" : $"HEAD {_baseline.DefaultHeadSha} · CI {_baseline.CiState}")
            {
                Freshness = _baseline?.Freshness.ToString().ToUpperInvariant() ?? "UNKNOWN",
                ExactHead = _baseline?.DefaultHeadSha
            },
            new("Persistence", "LIVE", null, $"SQLite schema {_store.GetSchemaVersionAsync().GetAwaiter().GetResult()} · {_store.DatabasePath}"),
            new("Browser Runtime", Snapshot.Sessions.Count > 0 ? "LIVE" : "READY", null, "PCC ownership proof is required for destructive session controls"),
            new("Verified Completion", "UNKNOWN", null, "No completion percentage is synthesized from rendered UI/evidence cards")
        };
        return gates;
    }
}
