using System.Collections.Concurrent;

namespace PCCExecutive.Application;

public sealed record RuntimeDiagnosticDetail(string Key, string? Value);

public sealed record RuntimeDiagnosticRecord(
    RuntimeDiagnosticEvent Event,
    IReadOnlyList<RuntimeDiagnosticDetail> Details,
    string? ExceptionClassification = null,
    long Sequence = 0);

public sealed record RuntimePrerequisiteEvidence(
    GuidedStepId Step,
    string Prerequisite,
    bool Satisfied,
    string ReasonCode,
    bool AutomaticallyRecoverable,
    bool HumanActionRequired);

public sealed record BrowserRuntimeEvidence(
    string? ProfileSource,
    string? RuntimeId,
    string? LogicalRole,
    int? ProcessId,
    string EndpointHealth,
    string OwnershipProof,
    bool PersonalChromeExcluded);

public sealed record RuntimeInspectorState(
    string? ProjectRunId,
    string Provider,
    string BrowserHealth,
    string ManagerState,
    string WorkerState,
    int ActiveSessionCount,
    string DispatchState,
    GuidedNextAction? NextAction,
    IReadOnlyList<RuntimePrerequisiteEvidence> Prerequisites,
    IReadOnlyList<BrowserRuntimeEvidence> BrowserRuntimes);

public interface IRuntimeDiagnosticSink
{
    Task RecordAsync(RuntimeDiagnosticRecord record, CancellationToken cancellationToken = default);
}

public interface IRuntimeDiagnosticReader
{
    Task<IReadOnlyList<RuntimeDiagnosticRecord>> ReadRecentAsync(int limit, CancellationToken cancellationToken = default);
}

public interface IRuntimeInspectorStateSource
{
    Task<RuntimeInspectorState> CaptureAsync(CancellationToken cancellationToken = default);
}

public interface IRuntimeDiagnosticCollector : IRuntimeDiagnosticSink, IRuntimeDiagnosticReader
{
    Guid BeginCorrelation(Guid? correlationId = null);
    RuntimeDiagnosticRecord Create(
        RuntimeDiagnosticKind kind,
        string reasonCode,
        string summary,
        Guid? correlationId = null,
        string? screen = null,
        string? control = null,
        string? command = null,
        string? target = null,
        bool? allowed = null,
        string? beforeState = null,
        string? afterState = null,
        string? projectRunId = null,
        string? runtimeId = null,
        string? exceptionClassification = null,
        IReadOnlyList<RuntimeDiagnosticDetail>? details = null);
}

public sealed class RuntimeDiagnosticCollector : IRuntimeDiagnosticCollector
{
    private readonly IRuntimeDiagnosticSink _sink;
    private readonly IRuntimeDiagnosticReader _reader;
    private long _sequence;

    public RuntimeDiagnosticCollector(IRuntimeDiagnosticSink sink, IRuntimeDiagnosticReader reader)
    {
        _sink = sink;
        _reader = reader;
    }

    public Guid BeginCorrelation(Guid? correlationId = null) => correlationId ?? Guid.NewGuid();

    public RuntimeDiagnosticRecord Create(
        RuntimeDiagnosticKind kind,
        string reasonCode,
        string summary,
        Guid? correlationId = null,
        string? screen = null,
        string? control = null,
        string? command = null,
        string? target = null,
        bool? allowed = null,
        string? beforeState = null,
        string? afterState = null,
        string? projectRunId = null,
        string? runtimeId = null,
        string? exceptionClassification = null,
        IReadOnlyList<RuntimeDiagnosticDetail>? details = null)
    {
        var correlation = correlationId ?? BeginCorrelation();
        var diagnosticEvent = new RuntimeDiagnosticEvent(
            Guid.NewGuid(), correlation, DateTimeOffset.UtcNow, kind, reasonCode, summary,
            screen, control, command, target, allowed, beforeState, afterState, projectRunId, runtimeId);
        return new RuntimeDiagnosticRecord(
            diagnosticEvent,
            details ?? Array.Empty<RuntimeDiagnosticDetail>(),
            exceptionClassification,
            Interlocked.Increment(ref _sequence));
    }

    public Task RecordAsync(RuntimeDiagnosticRecord record, CancellationToken cancellationToken = default) =>
        _sink.RecordAsync(record, cancellationToken);

    public Task<IReadOnlyList<RuntimeDiagnosticRecord>> ReadRecentAsync(int limit, CancellationToken cancellationToken = default) =>
        _reader.ReadRecentAsync(limit, cancellationToken);
}

public sealed class InMemoryRuntimeDiagnosticStore : IRuntimeDiagnosticSink, IRuntimeDiagnosticReader
{
    private readonly ConcurrentQueue<RuntimeDiagnosticRecord> _events = new();
    private readonly int _capacity;

    public InMemoryRuntimeDiagnosticStore(int capacity = 500)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public Task RecordAsync(RuntimeDiagnosticRecord record, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _events.Enqueue(record);
        while (_events.Count > _capacity) _events.TryDequeue(out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RuntimeDiagnosticRecord>> ReadRecentAsync(int limit, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (limit < 1) return Task.FromResult<IReadOnlyList<RuntimeDiagnosticRecord>>(Array.Empty<RuntimeDiagnosticRecord>());
        return Task.FromResult<IReadOnlyList<RuntimeDiagnosticRecord>>(_events.ToArray().TakeLast(limit).Reverse().ToArray());
    }
}
