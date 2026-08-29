using PCCExecutive.Browser;

namespace PCCExecutive.Browser.Acceptance;

public sealed class FakeProcesses : IProcessInspector
{
    private readonly Dictionary<int, (string Start, bool Alive)> _processes = [];

    public void Set(int processId, string startIdentity, bool alive) => _processes[processId] = (startIdentity, alive);
    public void SetAlive(int processId, bool alive)
    {
        if (_processes.TryGetValue(processId, out var current)) _processes[processId] = (current.Start, alive);
    }

    public bool IsAlive(int processId) => _processes.TryGetValue(processId, out var value) && value.Alive;
    public string? GetStartIdentity(int processId) => _processes.TryGetValue(processId, out var value) ? value.Start : null;
}

public sealed class FakeMarkers : IOwnershipMarkerStore
{
    private readonly Dictionary<string, OwnershipMarker> _markers = new(StringComparer.Ordinal);

    public void Set(OwnershipMarker marker) => _markers[marker.ProfilePath] = marker;
    public Task WriteAsync(OwnershipMarker marker, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Set(marker);
        return Task.CompletedTask;
    }

    public Task<OwnershipMarker?> ReadAsync(string profilePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_markers.TryGetValue(profilePath, out var marker) ? marker : null);
    }
}

public sealed class FakeRuntimeHost : IBrowserRuntimeHost
{
    private readonly string _root;
    private readonly FakeProcesses _processes;
    private int _nextPid = 30_000;

    public FakeRuntimeHost(string root, FakeProcesses processes)
    {
        _root = root;
        _processes = processes;
    }

    public List<string> KilledRuntimeIds { get; } = [];
    public List<string> RecoveredRuntimeIds { get; } = [];
    public Exception? RecoverException { get; set; }

    public Task<BrowserRuntimeRecord> LaunchAsync(BrowserSessionRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = request.RuntimeId ?? Guid.NewGuid().ToString("N");
        var pid = Interlocked.Increment(ref _nextPid);
        var start = $"pid:{pid}:start:fake";
        _processes.Set(pid, start, true);
        var runtime = ControlledBrowserAcceptanceHarness.CreateRuntime(
            id, request.LogicalAgentId, int.TryParse(request.WorkerSlotId, out var slot) ? slot : null,
            request.TaskId ?? "unbound", request.ConversationIdentity ?? $"{id}-conversation", true, pid) with
        {
            ProjectRunId = request.ProjectRunId,
            ProcessStartIdentity = start,
            ContextIdentity = $"ctx-{id}",
            ProfilePath = Path.Combine(_root, id),
            ProviderConversationIdentity = request.ProviderConversationIdentity ?? $"https://chatgpt.com/c/{id}-conversation",
            Visibility = request.DefaultVisibility,
            State = request.DefaultVisibility == BrowserVisibility.Hidden ? BrowserSessionState.Hidden : BrowserSessionState.Visible,
            OwnershipNonce = $"nonce-{id}"
        };
        return Task.FromResult(runtime);
    }

    public Task<bool> RecoverAsync(BrowserRuntimeRecord runtime, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RecoveredRuntimeIds.Add(runtime.RuntimeId);
        if (RecoverException is not null) throw RecoverException;
        return Task.FromResult(runtime.ProcessId is > 0 && _processes.IsAlive(runtime.ProcessId.Value));
    }

    public Task SetVisibilityAsync(BrowserRuntimeRecord runtime, BrowserVisibility visibility, bool bringToFront, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task KillAsync(BrowserRuntimeRecord runtime, OwnershipProof proof, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!proof.IsProven || proof.RuntimeId != runtime.RuntimeId) throw new InvalidOperationException("Fake host refused unproven kill.");
        KilledRuntimeIds.Add(runtime.RuntimeId);
        if (runtime.ProcessId is > 0) _processes.SetAlive(runtime.ProcessId.Value, false);
        return Task.CompletedTask;
    }

    public Task<BrowserRuntimeTelemetry> GetTelemetryAsync(BrowserRuntimeRecord runtime, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var alive = runtime.ProcessId is > 0 && _processes.IsAlive(runtime.ProcessId.Value);
        return Task.FromResult(new BrowserRuntimeTelemetry(runtime.RuntimeId, alive, alive ? 1 : 0, alive ? 1_024L : 0L, TimeSpan.Zero, runtime.LastHeartbeatAt, false, runtime.IsArchived));
    }
}

public sealed class FixedConversationProbe(ConversationDispatchEvidence evidence) : IConversationEvidenceProbe
{
    public ConversationDispatchEvidence Evidence { get; set; } = evidence;

    public Task<ConversationDispatchEvidence> InspectDispatchAsync(string runtimeId, string dispatchId, string contentHash, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Evidence);
    }
}

public sealed class NullConversationProbe : IConversationEvidenceProbe
{
    public Task<ConversationDispatchEvidence> InspectDispatchAsync(string runtimeId, string dispatchId, string contentHash, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<ConversationDispatchEvidence>(null!);
    }
}
public sealed class PartialCapturePort : IPartialResponseCapturePort
{
    public List<PartialResponseCapture> Captures { get; } = [];
    public Task SaveAsync(PartialResponseCapture capture, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Captures.Add(capture);
        return Task.CompletedTask;
    }
}

public sealed class PreservationPort : IRuntimePreservationPort
{
    public List<RuntimePreservationEnvelope> Envelopes { get; } = [];
    public Task PreserveAsync(RuntimePreservationEnvelope envelope, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Envelopes.Add(envelope);
        return Task.CompletedTask;
    }
}

public sealed class CheckpointPort : IConversationCheckpointPort
{
    private int _counter;
    public Task<string> CreateCheckpointAsync(ConversationRecord activeConversation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult($"checkpoint-{Interlocked.Increment(ref _counter)}");
    }
}

public sealed class ConversationCreator : IConversationCreator
{
    private int _counter;
    public Task<ConversationCreationResult> CreateAsync(ConversationRecord predecessor, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sequence = Interlocked.Increment(ref _counter);
        var id = $"{predecessor.LogicalAgentId}-next-{sequence}";
        return Task.FromResult(new ConversationCreationResult(id, $"https://chatgpt.com/c/{id}"));
    }
}

public sealed class ContinuationSender(bool result = true) : IContinuationSender
{
    public bool Result { get; set; } = result;
    public int Calls { get; private set; }

    public Task<bool> SendContinuationAsync(ConversationRecord candidate, string checkpointId, string continuationPacket, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        return Task.FromResult(Result);
    }
}

public sealed class ContinuationProofPort(bool valid = true) : IContinuationProofPort
{
    public bool Valid { get; set; } = valid;

    public Task<ContinuationProof> ValidateAsync(ConversationRecord candidate, string checkpointId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = Valid
            ? new ContinuationProof(true, true, true, true, true, true, true, ["continuation:validated"])
            : new ContinuationProof(true, true, true, true, true, false, true, ["continuation:task-mismatch"]);
        return Task.FromResult(result);
    }
}

public sealed class LifecycleStore : IConversationLifecycleStore
{
    public ConversationRecord? SavedCandidate { get; private set; }
    public ConversationRecord? Archived { get; private set; }
    public ConversationRecord? Active { get; private set; }
    public ConversationRecord? FailedCandidate { get; private set; }
    public string? FailureReason { get; private set; }
    public bool Committed { get; private set; }

    public Task SaveCandidateAsync(ConversationRecord candidate, string checkpointId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SavedCandidate = candidate;
        return Task.CompletedTask;
    }

    public Task CommitRolloverAsync(ConversationRecord predecessorArchived, ConversationRecord successorActive, string checkpointId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Archived = predecessorArchived;
        Active = successorActive;
        Committed = true;
        return Task.CompletedTask;
    }

    public Task RecordFailedRolloverAsync(ConversationRecord predecessorStillActive, ConversationRecord? failedCandidate, string reason, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Active = predecessorStillActive;
        FailedCandidate = failedCandidate;
        FailureReason = reason;
        return Task.CompletedTask;
    }
}

public sealed class RolloverJournal : IRolloverJournalPort
{
    public List<(string Agent, string Conversation, RolloverStage Stage, string Reason)> Events { get; } = [];

    public Task RecordAsync(string logicalAgentId, string conversationId, RolloverStage stage, string reason, DateTimeOffset occurredAt, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Events.Add((logicalAgentId, conversationId, stage, reason));
        return Task.CompletedTask;
    }
}

public sealed class ArchiveEvidencePort : IConversationArchiveEvidencePort
{
    private readonly HashSet<string> _archived = new(StringComparer.Ordinal);

    public void Prove(string logicalAgentId, string conversationIdentity) => _archived.Add($"{logicalAgentId}|{conversationIdentity}");

    public Task<bool> IsLineageSafelyArchivedAsync(string logicalAgentId, string conversationIdentity, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_archived.Contains($"{logicalAgentId}|{conversationIdentity}"));
    }
}

public sealed class RecoveryEvidencePort : IRecoveryEvidencePort
{
    public List<RecoveryEvidence> Events { get; } = [];
    public Task RecordAsync(RecoveryEvidence evidence, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Events.Add(evidence);
        return Task.CompletedTask;
    }
}

public static class AcceptanceTestFactory
{
    public static OwnershipMarker Marker(BrowserRuntimeRecord runtime) => new(
        runtime.RuntimeId,
        runtime.ProcessId!.Value,
        runtime.ProcessStartIdentity!,
        runtime.ContextIdentity!,
        runtime.ProfilePath,
        runtime.CreatedByPcc,
        runtime.AdoptedExplicitly,
        runtime.OwnershipNonce);

    public static ConversationRecord Conversation(string id, string logicalAgentId, int sequence = 1, ConversationLifecycleState state = ConversationLifecycleState.Active) =>
        new()
        {
            ConversationId = id,
            LogicalAgentId = logicalAgentId,
            ProjectRunId = "acceptance-project-run",
            Sequence = sequence,
            UrlOrProviderIdentity = $"https://chatgpt.com/c/{id}",
            CreatedAt = DateTimeOffset.UtcNow,
            State = state
        };

    public static RolloverRequest RolloverRequest(ConversationRecord active, string reason = "acceptance-rollover") =>
        new(active, reason, checkpoint => new ContinuationPacketData(
            "PCCEXECUTIVE",
            "walidatiyaai2025-gif/walid",
            active.LogicalAgentId,
            "PCCEXECUTIVE-T0001",
            "wave-2",
            "worker/pcc-executive-browser-e2e-acceptance",
            "acceptance-head",
            "PR",
            ["browser acceptance predecessor complete"],
            [],
            ["preserve logical identity"],
            checkpoint,
            active.ConversationId,
            "Continue controlled acceptance after fetching live state."));
}
