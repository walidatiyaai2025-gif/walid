using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PCCExecutive.Application;
using PCCExecutive.Browser;
using PCCExecutive.Domain;
using PCCExecutive.GitHub;
using PCCExecutive.Infrastructure;
using PCCExecutive.Pcc;

namespace PCCExecutive.App.Presentation;

public sealed partial class TerminalPresentationGateway
{
    private async Task<RuntimeSnapshot> BuildSnapshotAsync(CancellationToken cancellationToken)
    {
        var runtimeRecords = await _runtimeRegistry.ListAsync(cancellationToken).ConfigureAwait(false);
        var sessions = new List<SessionSummary>();
        var attention = new List<AttentionSummary>();
        foreach (var runtime in runtimeRecords.Where(x => !x.IsArchived && x.State is not BrowserSessionState.Killed))
        {
            var proof = await _ownership.ProveAsync(runtime, cancellationToken).ConfigureAwait(false);
            var health = await InspectHealthAsync(runtime, cancellationToken).ConfigureAwait(false);
            var logicalName = LogicalName(runtime);
            var summary = new SessionSummary(
                runtime.RuntimeId,
                logicalName,
                runtime.WorkerSlotId is null ? "Manager" : "Worker",
                runtime.State.ToString().ToUpperInvariant(),
                runtime.Visibility == BrowserVisibility.Hidden ? SessionVisibility.Hidden : SessionVisibility.Visible,
                runtime.TaskId ?? runtime.ProviderConversationIdentity ?? runtime.ConversationIdentity ?? "No task/conversation bound",
                runtime.LastActivityAt == default ? null : runtime.LastActivityAt,
                proof.IsProven,
                runtime.ProcessId,
                health)
            {
                LogicalAgentId = runtime.LogicalAgentId,
                ConversationId = runtime.ConversationIdentity,
                TaskId = runtime.TaskId,
                Heartbeat = runtime.LastHeartbeatAt == default ? null : runtime.LastHeartbeatAt,
                OwnershipStatus = proof.IsProven ? "PCC_OWNED_PROVEN" : "OWNERSHIP_NOT_PROVEN",
                OwnershipReason = proof.Reason,
                CanKill = proof.IsProven
            };
            sessions.Add(summary);
            if (health == HealthState.LoginRequired)
                attention.Add(new AttentionSummary($"login-{runtime.RuntimeId}", "ChatGPT sign-in is required", "PCC Executive cannot authenticate on your behalf.", "Open PCC Browser", $"runtime:{runtime.RuntimeId}", "P0"));
            else if (health == HealthState.Challenge)
                attention.Add(new AttentionSummary($"challenge-{runtime.RuntimeId}", "ChatGPT account challenge requires manual action", "CAPTCHA/account challenges are never bypassed automatically.", "Open PCC Browser", $"runtime:{runtime.RuntimeId}", "P0"));
        }

        var workers = await BuildWorkersAsync(sessions, cancellationToken).ConfigureAwait(false);
        var tasks = await BuildTasksAsync(workers, cancellationToken).ConfigureAwait(false);
        var conversations = await BuildConversationsAsync(cancellationToken).ConfigureAwait(false);
        var globalHealth = AggregateHealth(sessions.Select(x => x.Health));
        var blockerCount = _baseline?.KnownBlockers.Count ?? 0;
        var completion = CompletionDisplay();
        var currentWave = CurrentWaveDisplay();
        var evidence = BuildEvidence();
        var update = ReadUpdateState();
        var selectedProject = _lastResolution?.Project;

        return RuntimeSnapshot.Unbound with
        {
            GatewayBound = true,
            HasActiveRun = _run is not null,
            RuntimeStatus = _run is null ? "READY · SELECT A PCC PROJECT" : $"BOUND · {_selectedProjectId}",
            GlobalHealth = globalHealth,
            AutopilotState = _run is null ? "READY · PROJECT REQUIRED" : "READY · ORCHESTRATION COMMAND HOST PENDING",
            CurrentWave = currentWave,
            VerifiedCompletion = completion.Verified,
            ManagerEstimate = completion.Estimate,
            CompletionMode = completion.Mode,
            ActiveWorkers = workers.Count(x => IsActiveWorker(x.State)),
            P0Count = 0,
            P1Count = 0,
            BlockerCount = blockerCount,
            LoopGuardState = _run?.State == ProjectRunState.StalledAutoStopped ? "AUTO STOPPED" : "UNKNOWN · NO ACTIVE LOOP SNAPSHOT",
            LatestManagerHandoff = "No persisted Manager review packet is exposed by the current canonical SQLite store.",
            CurrentExecutionFlow = _run is null
                ? "Project Selection → live PCC resolution → Dashboard"
                : "Live PCC/GitHub evidence + durable logical agents + PCC-owned browser runtime; Manager command host remains safely disabled.",
            ApiConfigured = false,
            ProviderMode = ProviderMode.BrowserWeb,
            DispatchSettings = new DispatchSettingsSummary(
                _uiPreferences.DispatchMode,
                _settings.BaseDispatchIntervalSeconds,
                _settings.AdaptivePacing,
                _settings.MaxWorkers,
                _uiPreferences.AutoPauseOnLimit,
                _settings.AutoResume,
                _uiPreferences.DuplicateSendProtection),
            Update = update,
            Projects = _projects,
            Sessions = sessions.OrderBy(x => x.LogicalName, StringComparer.OrdinalIgnoreCase).ToArray(),
            Workers = workers,
            Tasks = tasks,
            EvidenceGates = evidence,
            AttentionItems = attention,
            RecoveryEvents = _recovery.Take(20).ToArray(),
            SelectedProjectId = _selectedProjectId,
            ProjectDisplayName = _baseline?.DisplayName ?? selectedProject?.DisplayName ?? _selectedProjectId ?? "No project selected",
            Repository = _baseline?.Repository ?? selectedProject?.Repository ?? "—",
            PccSourceSha = _baseline?.PccSourceSha ?? selectedProject?.Provenance.SourceSha ?? "—",
            Branch = _baseline?.DefaultBranch ?? "—",
            HeadSha = _baseline?.DefaultHeadSha ?? "—",
            PullRequest = LatestPr(_baseline),
            CiState = _baseline?.CiState ?? "UNKNOWN",
            EvidenceFreshness = _baseline?.Freshness.ToString().ToUpperInvariant() ?? (selectedProject?.Provenance.Freshness.ToString().ToUpperInvariant() ?? "UNKNOWN"),
            ProjectResolutionState = _lastResolution?.Status.ToString().ToUpperInvariant() ?? (_run is null ? "NO_PROJECT_SELECTED" : "SUCCESS"),
            ProjectResolutionMessage = _lastResolution?.Message ?? (_run is null ? "Select a live PCC project to continue." : null),
            BrowserProviderState = sessions.Count == 0 ? "READY · NO PCC SESSION" : $"{sessions.Count} PCC runtime record(s)",
            AdapterState = sessions.Count == 0 ? "NOT_INSPECTED" : sessions.Any(x => x.Health == HealthState.AdapterUncertain) ? "BROWSER_ADAPTER_UNCERTAIN" : "SEMANTIC_STATE_AVAILABLE",
            LoginState = LoginState(sessions.Select(x => x.Health)),
            ManagerReadiness = ManagerReadiness(sessions),
            WorkerReadiness = WorkerReadiness(workers),
            AutomaticRecoveryAction = RecoveryLabel(globalHealth),
            CooldownStatus = globalHealth == HealthState.RateLimited ? "RATE_LIMITED · new sends must remain paused by runtime policy" : "NONE",
            HealthScope = HealthScope(globalHealth),
            StartupRecoveryState = _run is null ? "STARTUP SAFE · NO_PROJECT_SELECTED" : "Durable project/settings/browser registry loaded from SQLite; no unsupported recovery-success claim.",
            Conversations = conversations
        };
    }
}
