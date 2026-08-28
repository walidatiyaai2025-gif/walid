using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PCCExecutive.Application;
using PCCExecutive.Browser;
using PCCExecutive.Domain;
using PCCExecutive.Infrastructure;

namespace PCCExecutive.App.Presentation;

/// <summary>
/// Final recovery/completion composition around the canonical runtime host.
/// It does not replace Browser send/ownership/ledger implementations; it consumes
/// their durable state and enforces final runtime truth before presentation/actions.
/// </summary>
public sealed class RecoveryCompletionPresentationGateway : IPccExecutivePresentationGateway, IAsyncDisposable
{
    private readonly PccExecutiveRuntimeHost _inner;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _synchronization = new(1, 1);
    private readonly AuthoritativeCompletionAuthority _completionAuthority = new();
    private readonly LoopGuardService _loopGuard = new();
    private readonly Dictionary<string, string> _durableAttentionTargets = new(StringComparer.Ordinal);
    private readonly Task _monitor;
    private RuntimeSnapshot _snapshot;
    private string? _lastProjectionSignature;

    private RecoveryCompletionPresentationGateway(PccExecutiveRuntimeHost inner)
    {
        _inner = inner;
        _snapshot = inner.Snapshot;
        _inner.SnapshotChanged += OnInnerSnapshotChanged;
        SynchronizeAsync(inner.Snapshot, CancellationToken.None).GetAwaiter().GetResult();
        _monitor = Task.Run(() => MonitorAsync(_lifetime.Token));
    }

    public static RecoveryCompletionPresentationGateway Create() => new(PccExecutiveRuntimeHost.Create());

    public RuntimeSnapshot Snapshot => _snapshot;
    public event EventHandler<RuntimeSnapshot>? SnapshotChanged;

    public bool CanExecute(UiAction action, string? targetId = null)
    {
        if (IsUnsafeNewSendAction(action) && IsGlobalSendBlocked()) return false;
        if (action == UiAction.OpenAttentionLocation && !string.IsNullOrWhiteSpace(targetId) &&
            _durableAttentionTargets.TryGetValue(targetId, out var runtimeId))
            return _inner.CanExecute(UiAction.BringSessionToFront, runtimeId);
        return _inner.CanExecute(action, targetId);
    }

    public async Task ExecuteAsync(UiAction action, string? targetId = null, CancellationToken cancellationToken = default)
    {
        if (IsUnsafeNewSendAction(action) && IsGlobalSendBlocked())
            throw new InvalidOperationException("Global Browser sends are blocked until fresh semantic health proves authenticated, healthy, online, not challenged, and not rate-limited.");

        if (action == UiAction.OpenAttentionLocation && !string.IsNullOrWhiteSpace(targetId) &&
            _durableAttentionTargets.TryGetValue(targetId, out var runtimeId) &&
            !_inner.CanExecute(action, targetId))
        {
            await _inner.ExecuteAsync(UiAction.BringSessionToFront, runtimeId, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _inner.ExecuteAsync(action, targetId, cancellationToken).ConfigureAwait(false);
        }

        await SynchronizeAsync(_inner.Snapshot, cancellationToken).ConfigureAwait(false);
    }

    private void OnInnerSnapshotChanged(object? sender, RuntimeSnapshot snapshot)
    {
        _ = Task.Run(async () =>
        {
            try { await SynchronizeAsync(snapshot, _lifetime.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch { /* fail-safe monitor retries; presentation never claims success on projection failure */ }
        });
    }

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await SynchronizeAsync(_inner.Snapshot, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch { }
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SynchronizeAsync(RuntimeSnapshot raw, CancellationToken cancellationToken)
    {
        await _synchronization.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var run = PccHostRecoveryAccess.Run(_inner);
            if (run is null)
            {
                Publish(Project(raw, null, null, Array.Empty<AttentionRequest>(), null));
                return;
            }

            var store = PccHostRecoveryAccess.Store(_inner);
            await SynchronizeProviderAttentionAsync(store, run.Id, raw.AttentionItems, cancellationToken).ConfigureAwait(false);
            var health = await LoadRuntimeHealthAsync(store, run.Id, cancellationToken).ConfigureAwait(false);
            var attention = await LoadIndexedAttentionAsync(store, run.Id, cancellationToken).ConfigureAwait(false);

            if (health is { Active: false, FreshSemanticRecovery: true })
            {
                await ResolveRecoveredProviderAttentionAsync(store, run.Id, attention, cancellationToken).ConfigureAwait(false);
                attention = await LoadIndexedAttentionAsync(store, run.Id, cancellationToken).ConfigureAwait(false);
            }

            await EnforceAuthoritativeCompletionAsync(store, cancellationToken).ConfigureAwait(false);
            run = PccHostRecoveryAccess.Run(_inner);
            var loop = await ObserveDurableLoopAsync(store, run!, raw, attention, cancellationToken).ConfigureAwait(false);
            if (loop.Level is LoopGuardLevel.LoopDetected or LoopGuardLevel.AutoStopped)
                await EnforceFiniteLoopStopAsync(store, run!, loop, cancellationToken).ConfigureAwait(false);

            Publish(Project(raw, PccHostRecoveryAccess.Run(_inner), health, attention, loop));
        }
        finally
        {
            _synchronization.Release();
        }
    }

    private async Task EnforceAuthoritativeCompletionAsync(SqliteStateStore store, CancellationToken cancellationToken)
    {
        var run = PccHostRecoveryAccess.Run(_inner);
        if (run is null || run.VerifiedCompletion.Percent < 99m || run.CompletionMode is not (ProjectCompletionMode.ClosureMode or ProjectCompletionMode.VerifiedComplete))
            return;

        var request = await store.LoadCheckpointAsync($"final-verification-request:{run.Id}", cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            if (run.VerifiedCompletion.Percent == 100m)
                await ApplyCompletionDecisionAsync(store, run, new AuthoritativeCompletionDecision(new VerifiedCompletion(99m), ProjectCompletionMode.ClosureMode, false, ["FINAL_VERIFICATION_NOT_REQUESTED"]), cancellationToken).ConfigureAwait(false);
            return;
        }

        AuthoritativeCompletionEvidence? evidence = null;
        var project = PccHostRecoveryAccess.ProjectControlId(_inner);
        if (!string.IsNullOrWhiteSpace(project))
        {
            var fresh = await PccHostRecoveryAccess.Baseline(_inner).BuildAsync(project, cancellationToken).ConfigureAwait(false);
            if (fresh.IsSuccess && fresh.Value is not null)
            {
                evidence = AuthoritativeCompletionAuthority.FromBaseline(fresh.Value);
                var runtimeTasks = PccHostRecoveryAccess.RuntimeTasks(_inner);
                if (runtimeTasks.Count > 0 && runtimeTasks.Any(x => x.State != TaskState.Completed))
                    evidence = evidence with { RequiredFamiliesGreen = false };
            }
        }

        var decision = _completionAuthority.Reconcile(run.ManagerEstimate, run.VerifiedCompletion, true, evidence, DateTimeOffset.UtcNow);
        if (decision.IsAuthoritativelyVerified)
        {
            if (run.VerifiedCompletion.Percent != 100m || run.CompletionMode != ProjectCompletionMode.VerifiedComplete)
                await ApplyCompletionDecisionAsync(store, run, decision, cancellationToken).ConfigureAwait(false);
            await SaveAuthoritativeCompletionReceiptAsync(store, run.Id, evidence!, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (run.VerifiedCompletion.Percent == 100m || run.CompletionMode == ProjectCompletionMode.VerifiedComplete)
            await ApplyCompletionDecisionAsync(store, run, decision, cancellationToken).ConfigureAwait(false);
    }

    private async Task ApplyCompletionDecisionAsync(SqliteStateStore store, ProjectRun run, AuthoritativeCompletionDecision decision, CancellationToken cancellationToken)
    {
        var updated = run with
        {
            State = decision.IsAuthoritativelyVerified ? ProjectRunState.VerifiedComplete : ProjectRunState.ClosureMode,
            VerifiedCompletion = decision.VerifiedCompletion,
            CompletionMode = decision.Mode
        };
        PccHostRecoveryAccess.Run(_inner) = updated;
        PccHostRecoveryAccess.Autopilot(_inner) = decision.IsAuthoritativelyVerified ? "DONE" : "CLOSURE_VERIFY";
        PccHostRecoveryAccess.LatestManagerHandoff(_inner) = decision.IsAuthoritativelyVerified
            ? "100% VERIFIED — fresh exact-head CI/test/PCC evidence passed authoritative completion reconciliation."
            : $"Closure remains <=99%: {string.Join(", ", decision.Reasons)}.";
        await store.SaveProjectRunAsync(updated, cancellationToken).ConfigureAwait(false);
        await PersistOrchestrationAsync(updated, decision.IsAuthoritativelyVerified ? OrchestrationPhase.VerifiedComplete : OrchestrationPhase.ClosureMode, cancellationToken).ConfigureAwait(false);
    }

    private async Task PersistOrchestrationAsync(ProjectRun run, OrchestrationPhase phase, CancellationToken cancellationToken)
    {
        var orchestration = PccHostRecoveryAccess.OrchestrationStore(_inner);
        var existing = await orchestration.LoadAsync(run.Id, cancellationToken).ConfigureAwait(false);
        var snapshot = new OrchestrationRecoverySnapshot(
            run,
            PccHostRecoveryAccess.CurrentWave(_inner),
            PccHostRecoveryAccess.RuntimeTasks(_inner),
            PccHostRecoveryAccess.Assignments(_inner),
            existing?.Dispatches ?? Array.Empty<PCCExecutive.Domain.Dispatch>(),
            existing?.ManagerReview,
            phase,
            DateTimeOffset.UtcNow);
        await orchestration.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
    }

    private static async Task SaveAuthoritativeCompletionReceiptAsync(SqliteStateStore store, ProjectRunId runId, AuthoritativeCompletionEvidence evidence, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new
        {
            evidence.ExpectedHead,
            evidence.ActualHead,
            evidence.ChecksHead,
            evidence.CapturedAt,
            evidence.Ci,
            evidence.Tests,
            evidence.RequiredFamiliesGreen,
            evidence.Blockers,
            VerifiedAt = DateTimeOffset.UtcNow
        });
        await store.SaveCheckpointAsync(new DurableCheckpoint($"authoritative-completion:{runId}", runId.ToString(), "authoritative-completion-v1", payload, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
    }

    private async Task SynchronizeProviderAttentionAsync(SqliteStateStore store, ProjectRunId runId, IReadOnlyList<AttentionSummary> current, CancellationToken cancellationToken)
    {
        var index = await LoadAttentionIndexAsync(store, runId, cancellationToken).ConfigureAwait(false);
        var changed = false;
        foreach (var item in current.Where(x => x.WhatHappened is "LOGIN_REQUIRED" or "CHALLENGE"))
        {
            var runtimeId = item.Id.StartsWith("browser-attention:", StringComparison.Ordinal) ? item.Id["browser-attention:".Length..] : item.ExactLocation;
            var id = new AttentionRequestId(StableGuid($"provider-attention:{runId}:{item.Id}"));
            var existing = await store.LoadAttentionAsync(id, cancellationToken).ConfigureAwait(false);
            if (existing is null || existing.State is AttentionState.Resolved or AttentionState.Dismissed)
            {
                var request = new AttentionRequest(id, runId, AttentionState.Open, item.WhatHappened, item.WhyActionRequired, item.ActionLabel, runtimeId, false, DateTimeOffset.UtcNow);
                await store.SaveAttentionAsync(request, cancellationToken).ConfigureAwait(false);
            }
            if (index.Add(id.ToString())) changed = true;
        }
        if (changed) await SaveAttentionIndexAsync(store, runId, index, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<AttentionRequest>> LoadIndexedAttentionAsync(SqliteStateStore store, ProjectRunId runId, CancellationToken cancellationToken)
    {
        var result = new List<AttentionRequest>();
        foreach (var idText in await LoadAttentionIndexAsync(store, runId, cancellationToken).ConfigureAwait(false))
        {
            if (!Guid.TryParse(idText, out var guid) || guid == Guid.Empty) continue;
            var item = await store.LoadAttentionAsync(new AttentionRequestId(guid), cancellationToken).ConfigureAwait(false);
            if (item is not null && item.State is AttentionState.Open or AttentionState.InProgress) result.Add(item);
        }
        return result;
    }

    private static async Task<HashSet<string>> LoadAttentionIndexAsync(SqliteStateStore store, ProjectRunId runId, CancellationToken cancellationToken)
    {
        var checkpoint = await store.LoadCheckpointAsync($"attention-index:{runId}", cancellationToken).ConfigureAwait(false);
        if (checkpoint is null) return new HashSet<string>(StringComparer.Ordinal);
        try
        {
            return (JsonSerializer.Deserialize<string[]>(checkpoint.Payload) ?? Array.Empty<string>()).ToHashSet(StringComparer.Ordinal);
        }
        catch (JsonException) { return new HashSet<string>(StringComparer.Ordinal); }
    }

    private static Task SaveAttentionIndexAsync(SqliteStateStore store, ProjectRunId runId, IReadOnlyCollection<string> ids, CancellationToken cancellationToken) =>
        store.SaveCheckpointAsync(new DurableCheckpoint($"attention-index:{runId}", runId.ToString(), "attention-index-v1", JsonSerializer.Serialize(ids.OrderBy(x => x, StringComparer.Ordinal)), DateTimeOffset.UtcNow), cancellationToken);

    private static async Task ResolveRecoveredProviderAttentionAsync(SqliteStateStore store, ProjectRunId runId, IReadOnlyList<AttentionRequest> active, CancellationToken cancellationToken)
    {
        foreach (var item in active.Where(x => x.Category is "LOGIN_REQUIRED" or "CHALLENGE"))
            await store.SaveAttentionAsync(item with { State = AttentionState.Resolved }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<DurableLoopProjection> ObserveDurableLoopAsync(SqliteStateStore store, ProjectRun run, RuntimeSnapshot raw, IReadOnlyList<AttentionRequest> attention, CancellationToken cancellationToken)
    {
        var checkpointKey = $"authoritative-loop:{run.Id}";
        var checkpoint = await store.LoadCheckpointAsync(checkpointKey, cancellationToken).ConfigureAwait(false);
        DurableLoopEnvelope envelope;
        try { envelope = checkpoint is null ? new([], false, LoopGuardLevel.Normal) : JsonSerializer.Deserialize<DurableLoopEnvelope>(checkpoint.Payload) ?? new([], false, LoopGuardLevel.Normal); }
        catch (JsonException) { envelope = new([], false, LoopGuardLevel.Normal); }

        if (envelope.AutoStopped)
            return new(LoopGuardLevel.AutoStopped, true, envelope.Observations.Count, "persisted auto-stop");

        var taskFingerprints = PccHostRecoveryAccess.RuntimeTasks(_inner).Select(x => x.Fingerprint).Where(x => !string.IsNullOrWhiteSpace(x)).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var blockers = PccHostRecoveryAccess.CurrentPlan(_inner)?.KnownBlockers
            .Concat(attention.Select(x => $"ATTENTION:{x.Category}:{x.OpenTarget}"))
            .Concat(raw.Tasks.Where(x => x.State.Contains("BLOCK", StringComparison.OrdinalIgnoreCase)).Select(x => $"TASK:{x.Id}:{x.State}"))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray()
            ?? attention.Select(x => $"ATTENTION:{x.Category}:{x.OpenTarget}").OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var evidence = raw.EvidenceGates.Select(x => Fingerprint($"{x.Name}|{x.State}|{x.Evidence}")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var failed = raw.EvidenceGates.Where(x => x.State.Contains("FAIL", StringComparison.OrdinalIgnoreCase)).Select(x => Fingerprint($"{x.Name}|{x.State}|{x.Evidence}"))
            .Concat(raw.Tasks.Where(x => x.State.Contains("FAIL", StringComparison.OrdinalIgnoreCase)).Select(x => Fingerprint($"{x.Id}|{x.State}"))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var reassignments = PccHostRecoveryAccess.Assignments(_inner).Select(x => $"{x.Key}->{x.Value.Value}").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var observationKey = Fingerprint(string.Join("|", PccHostRecoveryAccess.CurrentWave(_inner)?.Id.ToString(), PccHostRecoveryAccess.CurrentWave(_inner)?.State, raw.AutopilotState, raw.LatestManagerHandoff, string.Join(",", raw.Tasks.Select(x => $"{x.Id}:{x.State}"))));

        if (envelope.Observations.LastOrDefault()?.ObservationKey != observationKey)
        {
            var observation = new DurableLoopObservation(observationKey, taskFingerprints, blockers, evidence.ToArray(), failed.ToArray(), reassignments.ToArray(), run.VerifiedCompletion.Percent);
            var observations = envelope.Observations.Concat([observation]).TakeLast(6).ToArray();
            var domain = observations.Select((x, index) => new LoopSnapshot(
                new WaveId(StableGuid($"loop:{run.Id}:{index}:{x.ObservationKey}")),
                x.TaskFingerprints.ToHashSet(StringComparer.OrdinalIgnoreCase),
                x.BlockerFingerprints.ToHashSet(StringComparer.OrdinalIgnoreCase),
                x.EvidenceFingerprints.ToHashSet(StringComparer.OrdinalIgnoreCase),
                x.FailedCheckFingerprints.ToHashSet(StringComparer.OrdinalIgnoreCase),
                x.ReassignmentFingerprints.ToHashSet(StringComparer.OrdinalIgnoreCase),
                new VerifiedCompletion(x.VerifiedCompletion),
                DateTimeOffset.UtcNow)).ToArray();
            var assessment = _loopGuard.Analyze(domain, 3, 0.25m);
            envelope = new(observations, assessment.Level == LoopGuardLevel.LoopDetected, assessment.Level);
            await store.SaveCheckpointAsync(new DurableCheckpoint(checkpointKey, run.Id.ToString(), "authoritative-loop-v1", JsonSerializer.Serialize(envelope), DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
        }

        return new(envelope.AutoStopped ? LoopGuardLevel.AutoStopped : envelope.Level, envelope.AutoStopped, envelope.Observations.Count, envelope.Level.ToString());
    }

    private async Task EnforceFiniteLoopStopAsync(SqliteStateStore store, ProjectRun run, DurableLoopProjection loop, CancellationToken cancellationToken)
    {
        if (run.State == ProjectRunState.StalledAutoStopped) return;
        var updated = run with { State = ProjectRunState.StalledAutoStopped };
        PccHostRecoveryAccess.Run(_inner) = updated;
        PccHostRecoveryAccess.Autopilot(_inner) = "STALLED_AUTO_STOPPED";
        PccHostRecoveryAccess.LatestManagerHandoff(_inner) = "STALLED_AUTO_STOPPED — durable loop guard reached its finite governed threshold across restart-safe observations.";
        await PccHostRecoveryAccess.NewSendPause(_inner).PauseNewSendsAsync("STALLED_AUTO_STOPPED: durable Loop Guard threshold reached.", cancellationToken).ConfigureAwait(false);
        await store.SaveProjectRunAsync(updated, cancellationToken).ConfigureAwait(false);
        await PersistOrchestrationAsync(updated, OrchestrationPhase.StalledAutoStopped, cancellationToken).ConfigureAwait(false);
    }

    private RuntimeSnapshot Project(RuntimeSnapshot raw, ProjectRun? run, DurableRuntimeHealthProjection? health, IReadOnlyList<AttentionRequest> attention, DurableLoopProjection? loop)
    {
        _durableAttentionTargets.Clear();
        var durableAttention = attention.Select(x =>
        {
            var id = $"durable-attention:{x.Id}";
            if (!string.IsNullOrWhiteSpace(x.OpenTarget)) _durableAttentionTargets[id] = x.OpenTarget!;
            return new AttentionSummary(id, x.Category, x.Reason, x.RequiredAction, x.OpenTarget ?? "Runtime location unavailable", SeverityFor(x.Category));
        }).ToArray();
        var allAttention = raw.AttentionItems
            .Concat(durableAttention)
            .GroupBy(x => (x.WhatHappened, x.ExactLocation), StringComparerTuple.Instance)
            .Select(x => x.First()).ToArray();

        var p0 = raw.Tasks.Count(x => !x.EvidenceVerified && (x.Priority == "0" || x.Priority.Equals("P0", StringComparison.OrdinalIgnoreCase)));
        var p1 = raw.Tasks.Count(x => !x.EvidenceVerified && (x.Priority == "1" || x.Priority.Equals("P1", StringComparison.OrdinalIgnoreCase)));
        var blockers = raw.Tasks.Count(x => x.State.Contains("BLOCK", StringComparison.OrdinalIgnoreCase) || x.State.Contains("FAIL", StringComparison.OrdinalIgnoreCase)) + allAttention.Length;
        var globalHealth = health is null ? HealthState.Unknown : health.Active ? MapHealth(health.State) : health.FreshSemanticRecovery ? HealthState.Healthy : HealthState.Unknown;
        var sessions = raw.Sessions.Select(x => x with { Health = health?.Active == true ? MapHealth(health.State) : health?.FreshSemanticRecovery == true ? HealthState.Healthy : x.Health }).ToArray();
        var workers = raw.Workers.Select(worker =>
        {
            var task = raw.Tasks.FirstOrDefault(x => string.Equals(x.Owner, worker.LogicalName, StringComparison.OrdinalIgnoreCase));
            if (task is null) return worker;
            if (task.EvidenceVerified || task.State.Equals(TaskState.Completed.ToString(), StringComparison.OrdinalIgnoreCase))
                return worker with { Progress = 100, LatestHandoff = "Verified task completion recorded from reconciled runtime state." };
            if (task.State is "HandoffReceived" or "Validating")
                return worker with { LatestHandoff = "Worker handoff received; authoritative verification is pending." };
            return worker;
        }).ToArray();

        var schema = PccHostRecoveryAccess.Store(_inner).GetSchemaVersionAsync().GetAwaiter().GetResult();
        var gates = raw.EvidenceGates.Select(g => g.Name switch
        {
            "Foundation" => new EvidenceGateSummary("Foundation", "UNKNOWN", null, "No authoritative Foundation verification record is projected; local code presence is not PASS evidence."),
            "Persistence" => new EvidenceGateSummary("Persistence", "UNKNOWN", null, $"SQLite schema v{schema} is present; schema presence alone is not persistence acceptance evidence."),
            "Browser Runtime" when health?.Active == true => new EvidenceGateSummary("Browser Runtime", health.State, null, health.Reason),
            "Browser Runtime" when health?.FreshSemanticRecovery == true => new EvidenceGateSummary("Browser Runtime", "PARTIAL", null, "Fresh semantic authentication/health proof succeeded; broader Browser acceptance remains separately evidenced."),
            _ => g
        }).ToArray();

        var completion = run is null ? raw.VerifiedCompletion : (int)run.VerifiedCompletion.Percent;
        var mode = run is null ? raw.CompletionMode : run.CompletionMode switch
        {
            ProjectCompletionMode.Active => CompletionMode.Running,
            ProjectCompletionMode.ClosureMode => CompletionMode.ClosureMode,
            ProjectCompletionMode.VerifiedComplete => CompletionMode.Verified,
            ProjectCompletionMode.Blocked => CompletionMode.Blocked,
            _ => CompletionMode.Unknown
        };

        return raw with
        {
            GlobalHealth = globalHealth,
            VerifiedCompletion = completion,
            CompletionMode = mode,
            P0Count = p0,
            P1Count = p1,
            BlockerCount = blockers,
            LoopGuardState = run?.State == ProjectRunState.StalledAutoStopped || loop?.AutoStopped == true ? "STALLED_AUTO_STOPPED" : loop is null ? "UNKNOWN" : loop.Level.ToString().ToUpperInvariant(),
            Sessions = sessions,
            Workers = workers,
            EvidenceGates = gates,
            AttentionItems = allAttention
        };
    }

    private void Publish(RuntimeSnapshot snapshot)
    {
        var signature = Fingerprint(JsonSerializer.Serialize(new
        {
            snapshot.RuntimeStatus,
            snapshot.GlobalHealth,
            snapshot.AutopilotState,
            snapshot.CurrentWave,
            snapshot.VerifiedCompletion,
            snapshot.ManagerEstimate,
            snapshot.CompletionMode,
            snapshot.P0Count,
            snapshot.P1Count,
            snapshot.BlockerCount,
            snapshot.LoopGuardState,
            Attention = snapshot.AttentionItems.Select(x => new { x.Id, x.WhatHappened, x.ExactLocation }),
            Tasks = snapshot.Tasks.Select(x => new { x.Id, x.State, x.Priority, x.EvidenceVerified }),
            Workers = snapshot.Workers.Select(x => new { x.Id, x.State, x.Progress, x.LatestHandoff })
        }));
        _snapshot = snapshot;
        if (signature == _lastProjectionSignature) return;
        _lastProjectionSignature = signature;
        SnapshotChanged?.Invoke(this, snapshot);
    }

    private bool IsGlobalSendBlocked()
    {
        var fault = PccHostRecoveryAccess.RuntimeHealthFault(_inner);
        return !string.IsNullOrWhiteSpace(fault) || PccHostRecoveryAccess.SendGate(_inner).Snapshot.IsPaused ||
               PccHostRecoveryAccess.Run(_inner)?.State == ProjectRunState.StalledAutoStopped;
    }

    private static bool IsUnsafeNewSendAction(UiAction action) => action is UiAction.StartManager or UiAction.StartDispatch or UiAction.ReconcileWave;

    private static async Task<DurableRuntimeHealthProjection?> LoadRuntimeHealthAsync(SqliteStateStore store, ProjectRunId runId, CancellationToken cancellationToken)
    {
        var checkpoint = await store.LoadCheckpointAsync($"runtime-health:{runId}", cancellationToken).ConfigureAwait(false);
        if (checkpoint is null) return null;
        try
        {
            using var document = JsonDocument.Parse(checkpoint.Payload);
            var root = document.RootElement;
            var active = GetBool(root, "Active");
            var state = GetString(root, "State") ?? "UNKNOWN";
            var reason = GetString(root, "Reason") ?? "No semantic reason recorded.";
            var target = GetString(root, "RuntimeId");
            var fresh = !active && reason.Contains("Fresh semantic health proven", StringComparison.OrdinalIgnoreCase);
            return new(active, state, reason, target, fresh);
        }
        catch (JsonException) { return null; }
    }

    private static bool GetBool(JsonElement root, string name)
    {
        if (TryProperty(root, name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False) return value.GetBoolean();
        return false;
    }

    private static string? GetString(JsonElement root, string name) => TryProperty(root, name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool TryProperty(JsonElement root, string name, out JsonElement value)
    {
        if (root.TryGetProperty(name, out value)) return true;
        foreach (var property in root.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) { value = property.Value; return true; }
        value = default;
        return false;
    }

    private static HealthState MapHealth(string state) => state.ToUpperInvariant() switch
    {
        "RATE_LIMITED" or "RATELIMITED" => HealthState.RateLimited,
        "OFFLINE" => HealthState.Offline,
        "LOGIN_REQUIRED" or "LOGINREQUIRED" => HealthState.LoginRequired,
        "CHALLENGE" => HealthState.Challenge,
        "TEMPERROR" or "TEMP_ERROR" => HealthState.TemporaryError,
        "RECOVERING" => HealthState.Recovering,
        "SLOW" => HealthState.Slow,
        "THROTTLED" => HealthState.Throttled,
        _ => HealthState.Unknown
    };

    private static string SeverityFor(string category) => category is "LOGIN_REQUIRED" or "CHALLENGE" ? "P0" : "P1";
    private static string Fingerprint(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static Guid StableGuid(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        var guid = bytes[..16];
        guid[6] = (byte)((guid[6] & 0x0f) | 0x50);
        guid[8] = (byte)((guid[8] & 0x3f) | 0x80);
        return new Guid(guid);
    }

    public async ValueTask DisposeAsync()
    {
        _inner.SnapshotChanged -= OnInnerSnapshotChanged;
        _lifetime.Cancel();
        try { await _monitor.ConfigureAwait(false); } catch (OperationCanceledException) { }
        await _inner.DisposeAsync().ConfigureAwait(false);
        _synchronization.Dispose();
        _lifetime.Dispose();
    }

    private sealed record DurableRuntimeHealthProjection(bool Active, string State, string Reason, string? RuntimeId, bool FreshSemanticRecovery);
    private sealed record DurableLoopObservation(string ObservationKey, IReadOnlyList<string> TaskFingerprints, IReadOnlyList<string> BlockerFingerprints, IReadOnlyList<string> EvidenceFingerprints, IReadOnlyList<string> FailedCheckFingerprints, IReadOnlyList<string> ReassignmentFingerprints, decimal VerifiedCompletion);
    private sealed record DurableLoopEnvelope(IReadOnlyList<DurableLoopObservation> Observations, bool AutoStopped, LoopGuardLevel Level);
    private sealed record DurableLoopProjection(LoopGuardLevel Level, bool AutoStopped, int ObservationCount, string Reason);

    private sealed class StringComparerTuple : IEqualityComparer<(string What, string Location)>
    {
        public static StringComparerTuple Instance { get; } = new();
        public bool Equals((string What, string Location) x, (string What, string Location) y) => StringComparer.OrdinalIgnoreCase.Equals(x.What, y.What) && StringComparer.Ordinal.Equals(x.Location, y.Location);
        public int GetHashCode((string What, string Location) obj) => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.What), StringComparer.Ordinal.GetHashCode(obj.Location));
    }
}

internal static class PccHostRecoveryAccess
{
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_store")]
    internal static extern ref SqliteStateStore Store(PccExecutiveRuntimeHost host);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_run")]
    internal static extern ref ProjectRun? Run(PccExecutiveRuntimeHost host);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_autopilot")]
    internal static extern ref string Autopilot(PccExecutiveRuntimeHost host);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_latestManagerHandoff")]
    internal static extern ref string LatestManagerHandoff(PccExecutiveRuntimeHost host);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_runtimeHealthFault")]
    internal static extern ref string? RuntimeHealthFault(PccExecutiveRuntimeHost host);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_sendGate")]
    internal static extern ref GlobalBrowserSendGate SendGate(PccExecutiveRuntimeHost host);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_newSendPause")]
    internal static extern ref INewSendPausePort NewSendPause(PccExecutiveRuntimeHost host);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_baseline")]
    internal static extern ref IProjectBaselineBuilder Baseline(PccExecutiveRuntimeHost host);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_projectControlId")]
    internal static extern ref string? ProjectControlId(PccExecutiveRuntimeHost host);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_orchestrationStore")]
    internal static extern ref CrashConsistentOrchestrationStore OrchestrationStore(PccExecutiveRuntimeHost host);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_currentWave")]
    internal static extern ref Wave? CurrentWave(PccExecutiveRuntimeHost host);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_runtimeTasks")]
    internal static extern ref IReadOnlyList<WorkerTask> RuntimeTasks(PccExecutiveRuntimeHost host);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_assignments")]
    internal static extern ref IReadOnlyDictionary<TaskId, WorkerSlotId> Assignments(PccExecutiveRuntimeHost host);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_currentPlan")]
    internal static extern ref StructuredManagerPlan? CurrentPlan(PccExecutiveRuntimeHost host);
}
