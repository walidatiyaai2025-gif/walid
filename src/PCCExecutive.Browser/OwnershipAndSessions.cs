using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;

namespace PCCExecutive.Browser;

public sealed class InMemoryBrowserRuntimeRegistry : IBrowserRuntimeRegistry
{
    private readonly ConcurrentDictionary<string, BrowserRuntimeRecord> _runtimes = new(StringComparer.Ordinal);

    public Task UpsertAsync(BrowserRuntimeRecord runtime, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _runtimes[runtime.RuntimeId] = runtime;
        return Task.CompletedTask;
    }

    public Task<BrowserRuntimeRecord?> GetAsync(string runtimeId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _runtimes.TryGetValue(runtimeId, out var runtime);
        return Task.FromResult(runtime);
    }

    public Task<IReadOnlyList<BrowserRuntimeRecord>> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<BrowserRuntimeRecord> snapshot = _runtimes.Values.OrderBy(x => x.RuntimeId, StringComparer.Ordinal).ToArray();
        return Task.FromResult(snapshot);
    }
}

public sealed class FileOwnershipMarkerStore : IOwnershipMarkerStore
{
    public const string MarkerFileName = ".pcc-browser-owner.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task WriteAsync(OwnershipMarker marker, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(marker.ProfilePath);
        var path = Path.Combine(marker.ProfilePath, MarkerFileName);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, marker, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OwnershipMarker?> ReadAsync(string profilePath, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(profilePath, MarkerFileName);
        if (!File.Exists(path))
            return null;

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<OwnershipMarker>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}

public sealed class SystemProcessInspector : IProcessInspector
{
    public bool IsAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public string? GetStartIdentity(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return BrowserProcessIdentity.From(process);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}

public static class BrowserProcessIdentity
{
    public static string From(Process process)
    {
        var startTicks = process.StartTime.ToUniversalTime().Ticks;
        return $"pid:{process.Id}:start:{startTicks}";
    }
}

public sealed class OwnershipProofService : IOwnershipProofService
{
    private readonly string _pccProfileRoot;
    private readonly IOwnershipMarkerStore _markers;
    private readonly IProcessInspector _processes;

    public OwnershipProofService(string pccProfileRoot, IOwnershipMarkerStore markers, IProcessInspector processes)
    {
        _pccProfileRoot = Path.GetFullPath(pccProfileRoot);
        _markers = markers;
        _processes = processes;
    }

    public async Task<OwnershipProof> ProveAsync(BrowserRuntimeRecord runtime, CancellationToken cancellationToken = default)
        => await ProveCoreAsync(runtime, requireLiveProcess: true, cancellationToken).ConfigureAwait(false);

    public async Task<OwnershipProof> ProveRecordedOwnershipAsync(BrowserRuntimeRecord runtime, CancellationToken cancellationToken = default)
        => await ProveCoreAsync(runtime, requireLiveProcess: false, cancellationToken).ConfigureAwait(false);

    private async Task<OwnershipProof> ProveCoreAsync(BrowserRuntimeRecord runtime, bool requireLiveProcess, CancellationToken cancellationToken)
    {
        if (!runtime.CreatedByPcc && !runtime.AdoptedExplicitly)
            return OwnershipProof.Denied(runtime.RuntimeId, "NO_PCC_OWNERSHIP_FLAG");

        if (runtime.ProcessId is not > 0 || string.IsNullOrWhiteSpace(runtime.ProcessStartIdentity))
            return OwnershipProof.Denied(runtime.RuntimeId, "PROCESS_IDENTITY_MISSING");

        if (string.IsNullOrWhiteSpace(runtime.ContextIdentity) || string.IsNullOrWhiteSpace(runtime.OwnershipNonce))
            return OwnershipProof.Denied(runtime.RuntimeId, "CONTEXT_OWNERSHIP_EVIDENCE_MISSING");

        if (!IsPathUnderRoot(runtime.ProfilePath, _pccProfileRoot))
            return OwnershipProof.Denied(runtime.RuntimeId, "PROFILE_OUTSIDE_PCC_ROOT");

        var marker = await _markers.ReadAsync(runtime.ProfilePath, cancellationToken).ConfigureAwait(false);
        if (marker is null)
            return OwnershipProof.Denied(runtime.RuntimeId, "OWNERSHIP_MARKER_MISSING");

        if (!MarkerMatches(runtime, marker))
            return OwnershipProof.Denied(runtime.RuntimeId, "OWNERSHIP_MARKER_MISMATCH");

        if (requireLiveProcess && !_processes.IsAlive(runtime.ProcessId.Value))
            return OwnershipProof.Denied(runtime.RuntimeId, "PROCESS_NOT_ALIVE");

        var currentStartIdentity = _processes.GetStartIdentity(runtime.ProcessId.Value);
        if (_processes.IsAlive(runtime.ProcessId.Value) && !StringComparer.Ordinal.Equals(currentStartIdentity, runtime.ProcessStartIdentity))
            return OwnershipProof.Denied(runtime.RuntimeId, "PROCESS_START_IDENTITY_MISMATCH");

        return OwnershipProof.Proven(runtime.RuntimeId);
    }

    private static bool MarkerMatches(BrowserRuntimeRecord runtime, OwnershipMarker marker) =>
        StringComparer.Ordinal.Equals(marker.RuntimeId, runtime.RuntimeId) &&
        marker.ProcessId == runtime.ProcessId &&
        StringComparer.Ordinal.Equals(marker.ProcessStartIdentity, runtime.ProcessStartIdentity) &&
        StringComparer.Ordinal.Equals(marker.ContextIdentity, runtime.ContextIdentity) &&
        PathsEqual(marker.ProfilePath, runtime.ProfilePath) &&
        marker.CreatedByPcc == runtime.CreatedByPcc &&
        marker.AdoptedExplicitly == runtime.AdoptedExplicitly &&
        StringComparer.Ordinal.Equals(marker.OwnershipNonce, runtime.OwnershipNonce) &&
        (marker.CreatedByPcc || marker.AdoptedExplicitly);

    private static bool IsPathUnderRoot(string candidate, string root)
    {
        var fullCandidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (PathsEqual(fullCandidate, fullRoot))
            return true;

        var prefix = fullRoot + Path.DirectorySeparatorChar;
        return fullCandidate.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

public sealed class BrowserSessionController
{
    private readonly IBrowserRuntimeRegistry _registry;
    private readonly IBrowserRuntimeHost _host;
    private readonly IOwnershipProofService _ownership;
    private readonly IOwnershipMarkerStore _markers;
    private readonly IProcessInspector _processes;
    private readonly IBrowserRecoveryTelemetrySink _recoveryTelemetry;

    public BrowserSessionController(
        IBrowserRuntimeRegistry registry,
        IBrowserRuntimeHost host,
        IOwnershipProofService ownership,
        IOwnershipMarkerStore markers,
        IProcessInspector processes,
        IBrowserRecoveryTelemetrySink? recoveryTelemetry = null)
    {
        _registry = registry;
        _host = host;
        _ownership = ownership;
        _markers = markers;
        _processes = processes;
        _recoveryTelemetry = recoveryTelemetry ?? NullBrowserRecoveryTelemetrySink.Instance;
    }

    public async Task<BrowserRuntimeRecord> CreateAsync(BrowserSessionRequest request, CancellationToken cancellationToken = default)
    {
        var runtime = await _host.LaunchAsync(request, cancellationToken).ConfigureAwait(false);
        if (runtime.ProcessId is not > 0 || string.IsNullOrWhiteSpace(runtime.ProcessStartIdentity) || string.IsNullOrWhiteSpace(runtime.ContextIdentity))
            throw new InvalidOperationException("Browser host did not return sufficient PCC ownership evidence.");

        await _markers.WriteAsync(ToMarker(runtime), cancellationToken).ConfigureAwait(false);
        await _registry.UpsertAsync(runtime, cancellationToken).ConfigureAwait(false);
        return runtime;
    }

    public async Task<SessionActionResult> BindDispatchTargetAsync(
        string runtimeId,
        string taskId,
        string conversationIdentity,
        string providerConversationIdentity,
        CancellationToken cancellationToken = default)
    {
        var runtime = await _registry.GetAsync(runtimeId, cancellationToken).ConfigureAwait(false);
        if (runtime is null)
            return new(false, runtimeId, "RUNTIME_NOT_FOUND");

        var updated = runtime with
        {
            TaskId = taskId,
            ConversationIdentity = conversationIdentity,
            ProviderConversationIdentity = providerConversationIdentity,
            LastActivityAt = DateTimeOffset.UtcNow
        };
        await _registry.UpsertAsync(updated, cancellationToken).ConfigureAwait(false);
        return new(true, runtimeId, "DISPATCH_TARGET_BOUND", updated);
    }

    public Task<SessionActionResult> OpenAsync(string runtimeId, CancellationToken cancellationToken = default) =>
        SetVisibilityAsync(runtimeId, BrowserVisibility.Visible, false, cancellationToken);

    public Task<SessionActionResult> BringToFrontAsync(string runtimeId, CancellationToken cancellationToken = default) =>
        SetVisibilityAsync(runtimeId, BrowserVisibility.Visible, true, cancellationToken);

    public Task<SessionActionResult> HideAsync(string runtimeId, CancellationToken cancellationToken = default) =>
        SetVisibilityAsync(runtimeId, BrowserVisibility.Hidden, false, cancellationToken);

    public async Task<SessionActionResult> HeartbeatAsync(string runtimeId, CancellationToken cancellationToken = default)
    {
        var runtime = await _registry.GetAsync(runtimeId, cancellationToken).ConfigureAwait(false);
        if (runtime is null)
            return new(false, runtimeId, "RUNTIME_NOT_FOUND");

        var now = DateTimeOffset.UtcNow;
        var updated = runtime with { LastHeartbeatAt = now };
        await _registry.UpsertAsync(updated, cancellationToken).ConfigureAwait(false);
        return new(true, runtimeId, "HEARTBEAT_RECORDED", updated);
    }

    public async Task<SessionActionResult> KillAsync(string runtimeId, CancellationToken cancellationToken = default)
    {
        var runtime = await _registry.GetAsync(runtimeId, cancellationToken).ConfigureAwait(false);
        if (runtime is null)
            return new(false, runtimeId, "RUNTIME_NOT_FOUND");

        var proof = await _ownership.ProveAsync(runtime, cancellationToken).ConfigureAwait(false);
        if (!proof.IsProven)
            return new(false, runtimeId, proof.Reason, runtime);

        await _host.KillAsync(runtime, proof, cancellationToken).ConfigureAwait(false);
        var updated = runtime with { State = BrowserSessionState.Killed, IsArchived = true, LastActivityAt = DateTimeOffset.UtcNow };
        await _registry.UpsertAsync(updated, cancellationToken).ConfigureAwait(false);
        return new(true, runtimeId, "PCC_OWNED_SESSION_KILLED", updated);
    }

    public async Task<KillAllResult> KillAllPccSessionsAsync(CancellationToken cancellationToken = default)
    {
        var runtimes = await _registry.ListAsync(cancellationToken).ConfigureAwait(false);
        var killed = new List<string>();
        var skipped = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var runtime in runtimes.Where(x => x.State is not BrowserSessionState.Killed and not BrowserSessionState.Archived))
        {
            var result = await KillAsync(runtime.RuntimeId, cancellationToken).ConfigureAwait(false);
            if (result.Succeeded)
                killed.Add(runtime.RuntimeId);
            else
                skipped[runtime.RuntimeId] = result.Reason;
        }

        return new(killed, new ReadOnlyDictionary<string, string>(skipped));
    }

    public async Task<SessionActionResult> RestartAsync(string runtimeId, CancellationToken cancellationToken = default)
    {
        var runtime = await _registry.GetAsync(runtimeId, cancellationToken).ConfigureAwait(false);
        if (runtime is null)
            return new(false, runtimeId, "RUNTIME_NOT_FOUND");

        var killed = await KillAsync(runtimeId, cancellationToken).ConfigureAwait(false);
        if (!killed.Succeeded)
            return killed;

        var archived = runtime with { State = BrowserSessionState.Archived, IsArchived = true, LastActivityAt = DateTimeOffset.UtcNow };
        await _registry.UpsertAsync(archived, cancellationToken).ConfigureAwait(false);

        var replacement = await CreateAsync(new BrowserSessionRequest(
            runtime.ProjectRunId,
            runtime.LogicalAgentId,
            runtime.WorkerSlotId,
            runtime.TaskId,
            runtime.ConversationIdentity,
            runtime.ProviderConversationIdentity,
            runtime.Visibility), cancellationToken).ConfigureAwait(false);

        return new(true, replacement.RuntimeId, "SESSION_RESTARTED", replacement);
    }

    public async Task<IReadOnlyList<BrowserRuntimeRecord>> DetectOrphansAsync(TimeSpan staleAfter, CancellationToken cancellationToken = default)
    {
        var cutoff = DateTimeOffset.UtcNow - staleAfter;
        var runtimes = await _registry.ListAsync(cancellationToken).ConfigureAwait(false);
        return runtimes
            .Where(x => x.State is not BrowserSessionState.Killed and not BrowserSessionState.Archived)
            .Where(x => x.LastHeartbeatAt <= cutoff || x.ProcessId is not > 0 || !_processes.IsAlive(x.ProcessId.Value))
            .ToArray();
    }

    public async Task<SessionActionResult> RecoverOrphanAsync(string runtimeId, CancellationToken cancellationToken = default)
    {
        var runtime = await _registry.GetAsync(runtimeId, cancellationToken).ConfigureAwait(false);
        if (runtime is null)
            return new(false, runtimeId, "RUNTIME_NOT_FOUND");

        var correlationId = Guid.NewGuid();
        await EmitRecoveryAsync(correlationId, BrowserRecoveryPhase.Detect, "PERSISTED_RUNTIME_DETECTED", runtime, true, cancellationToken).ConfigureAwait(false);
        Exception? endpointFailure = null;
        var processAlive = runtime.ProcessId is > 0 && _processes.IsAlive(runtime.ProcessId.Value);
        await EmitRecoveryAsync(correlationId, BrowserRecoveryPhase.Classify,
            processAlive ? "PROCESS_ALIVE_ENDPOINT_REQUIRES_PROBE" : "PROCESS_DEAD_ENDPOINT_STALE", runtime, true, cancellationToken).ConfigureAwait(false);

        if (processAlive)
        {
            var proof = await _ownership.ProveAsync(runtime, cancellationToken).ConfigureAwait(false);
            await EmitRecoveryAsync(correlationId, BrowserRecoveryPhase.ProveOwnership, proof.Reason, runtime, proof.IsProven, cancellationToken).ConfigureAwait(false);
            if (!proof.IsProven)
                return new(false, runtimeId, proof.Reason, runtime);

            try
            {
                var recovered = await _host.RecoverAsync(runtime, cancellationToken).ConfigureAwait(false);
                if (recovered)
                {
                    var now = DateTimeOffset.UtcNow;
                    var updated = runtime with { State = BrowserSessionState.Ready, LastHeartbeatAt = now, LastActivityAt = now };
                    await _registry.UpsertAsync(updated, cancellationToken).ConfigureAwait(false);
                    await EmitRecoveryAsync(correlationId, BrowserRecoveryPhase.Recover, "OWNED_ENDPOINT_RECONNECTED", updated, true, cancellationToken).ConfigureAwait(false);
                    await EmitRecoveryAsync(correlationId, BrowserRecoveryPhase.Reconcile, "PERSISTED_RUNTIME_REFRESHED", updated, true, cancellationToken).ConfigureAwait(false);
                    await EmitRecoveryAsync(correlationId, BrowserRecoveryPhase.Verify, "RUNTIME_READY_VERIFIED", updated, true, cancellationToken).ConfigureAwait(false);
                    await EmitRecoveryAsync(correlationId, BrowserRecoveryPhase.Resume, "OWNED_PROCESS_RECOVERED", updated, true, cancellationToken).ConfigureAwait(false);
                    return new(true, runtimeId, "OWNED_PROCESS_RECOVERED", updated);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                endpointFailure = ex;
            }

            if (endpointFailure is not null && !BrowserEndpointFailureClassifier.IsRecoverableStaleEndpoint(endpointFailure))
            {
                await EmitRecoveryAsync(correlationId, BrowserRecoveryPhase.Recover, "RECOVERY_FAILED_UNCLASSIFIED", runtime, false, cancellationToken).ConfigureAwait(false);
                return new(false, runtimeId, $"OWNED_PROCESS_RECOVERY_FAILED:{endpointFailure.GetType().Name}", runtime);
            }

            var endpointKind = endpointFailure is null ? BrowserEndpointFailureKind.Unknown : BrowserEndpointFailureClassifier.Classify(endpointFailure);
            await EmitRecoveryAsync(correlationId, BrowserRecoveryPhase.Classify, $"ENDPOINT_{endpointKind.ToString().ToUpperInvariant()}", runtime, true, cancellationToken).ConfigureAwait(false);

            try
            {
                // Positive ownership proof was established above, so it is safe to terminate only
                // this PCC-owned stale process before reusing the persistent Manager/Worker profile.
                await _host.KillAsync(runtime, proof, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new(false, runtimeId, $"OWNED_PROCESS_RECOVERY_FAILED:{ex.GetType().Name}", runtime);
            }
        }
        else
        {
            var recordedProof = await _ownership.ProveRecordedOwnershipAsync(runtime, cancellationToken).ConfigureAwait(false);
            await EmitRecoveryAsync(correlationId, BrowserRecoveryPhase.ProveOwnership, recordedProof.Reason, runtime, recordedProof.IsProven, cancellationToken).ConfigureAwait(false);
            if (!recordedProof.IsProven)
                return new(false, runtimeId, recordedProof.Reason, runtime);
        }

        var archived = runtime with { State = BrowserSessionState.Archived, IsArchived = true, LastActivityAt = DateTimeOffset.UtcNow };
        await _registry.UpsertAsync(archived, cancellationToken).ConfigureAwait(false);
        await EmitRecoveryAsync(correlationId, BrowserRecoveryPhase.Recover, "PCC_RUNTIME_REPLACEMENT_STARTED", archived, true, cancellationToken).ConfigureAwait(false);

        try
        {
            var replacement = await CreateAsync(new BrowserSessionRequest(
                runtime.ProjectRunId,
                runtime.LogicalAgentId,
                runtime.WorkerSlotId,
                runtime.TaskId,
                runtime.ConversationIdentity,
                runtime.ProviderConversationIdentity,
                runtime.Visibility), cancellationToken).ConfigureAwait(false);

            await EmitRecoveryAsync(correlationId, BrowserRecoveryPhase.Reconcile, "ENDPOINT_AND_PROCESS_METADATA_REPLACED", archived, true, cancellationToken, replacement.RuntimeId).ConfigureAwait(false);
            await EmitRecoveryAsync(correlationId, BrowserRecoveryPhase.Verify, "REPLACEMENT_RUNTIME_READY_VERIFIED", replacement, true, cancellationToken, replacement.RuntimeId).ConfigureAwait(false);
            await EmitRecoveryAsync(correlationId, BrowserRecoveryPhase.Resume, "LOGICAL_AGENT_RESUMED", replacement, true, cancellationToken, replacement.RuntimeId).ConfigureAwait(false);
            return new(true, replacement.RuntimeId, "STALE_OR_DEAD_ORPHAN_REPLACED_WITH_NEW_PCC_RUNTIME", replacement);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Replacement failure is surfaced as a semantic recovery state. Startup stays alive so
            // the operator gets one clear guided action instead of a raw ECONNREFUSED dialog.
            return new(false, runtimeId, $"PCC_RUNTIME_REPLACEMENT_FAILED:{ex.GetType().Name}", archived);
        }
    }

    private Task EmitRecoveryAsync(Guid correlationId, BrowserRecoveryPhase phase, string reason, BrowserRuntimeRecord runtime,
        bool succeeded, CancellationToken cancellationToken, string? replacementRuntimeId = null) =>
        _recoveryTelemetry.EmitAsync(new BrowserRecoveryTelemetryEvent(correlationId, DateTimeOffset.UtcNow, phase, reason,
            runtime.RuntimeId, runtime.LogicalAgentId, runtime.ProjectRunId, succeeded, replacementRuntimeId), cancellationToken);

    public async Task<ResourceGovernorSnapshot> CaptureResourceSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var runtimes = await _registry.ListAsync(cancellationToken).ConfigureAwait(false);
        var telemetry = new List<BrowserRuntimeTelemetry>();
        foreach (var runtime in runtimes.Where(x => !x.IsArchived && x.State is not BrowserSessionState.Killed))
            telemetry.Add(await _host.GetTelemetryAsync(runtime, cancellationToken).ConfigureAwait(false));

        return new(
            telemetry.Count(x => x.ProcessAlive),
            telemetry.Sum(x => x.WorkingSetBytes),
            TimeSpan.FromTicks(telemetry.Sum(x => x.CpuTime.Ticks)),
            DateTimeOffset.UtcNow,
            telemetry);
    }

    private async Task<SessionActionResult> SetVisibilityAsync(string runtimeId, BrowserVisibility visibility, bool bringToFront, CancellationToken cancellationToken)
    {
        var runtime = await _registry.GetAsync(runtimeId, cancellationToken).ConfigureAwait(false);
        if (runtime is null)
            return new(false, runtimeId, "RUNTIME_NOT_FOUND");

        await _host.SetVisibilityAsync(runtime, visibility, bringToFront, cancellationToken).ConfigureAwait(false);
        var state = visibility == BrowserVisibility.Hidden ? BrowserSessionState.Hidden : BrowserSessionState.Visible;
        var updated = runtime with { Visibility = visibility, State = state, LastActivityAt = DateTimeOffset.UtcNow };
        await _registry.UpsertAsync(updated, cancellationToken).ConfigureAwait(false);
        return new(true, runtimeId, bringToFront ? "BROUGHT_TO_FRONT" : visibility == BrowserVisibility.Hidden ? "HIDDEN" : "OPENED", updated);
    }

    private static OwnershipMarker ToMarker(BrowserRuntimeRecord runtime) => new(
        runtime.RuntimeId,
        runtime.ProcessId!.Value,
        runtime.ProcessStartIdentity!,
        runtime.ContextIdentity!,
        runtime.ProfilePath,
        runtime.CreatedByPcc,
        runtime.AdoptedExplicitly,
        runtime.OwnershipNonce);
}
