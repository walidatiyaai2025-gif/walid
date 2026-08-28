using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using PCCExecutive.Application;

namespace PCCExecutive.Infrastructure;

public sealed record RuntimeDiagnosticRetentionPolicy(
    int MaximumEventCount = 10_000,
    TimeSpan? MaximumAge = null,
    int PruneEveryWrites = 100)
{
    public TimeSpan EffectiveMaximumAge => MaximumAge ?? TimeSpan.FromDays(30);
}

public interface IDiagnosticRedactor
{
    string Redact(string? value);
    IReadOnlyList<RuntimeDiagnosticDetail> Redact(IReadOnlyList<RuntimeDiagnosticDetail> details);
}

public sealed partial class DiagnosticRedactor : IDiagnosticRedactor
{
    private const string Mask = "[REDACTED]";
    private static readonly string[] SensitiveKeys =
    [
        "authorization", "cookie", "set-cookie", "token", "access_token", "refresh_token",
        "oauth", "password", "passwd", "secret", "browserstorage", "localstorage", "sessionstorage",
        "prompt", "response"
    ];

    [GeneratedRegex(@"(?i)\b(bearer\s+)[A-Za-z0-9._~+\-/=]+", RegexOptions.CultureInvariant)]
    private static partial Regex BearerRegex();

    [GeneratedRegex(@"(?i)\b(authorization|cookie|set-cookie|password|passwd|access[_-]?token|refresh[_-]?token|oauth[_-]?token|client[_-]?secret)\s*[:=]\s*([^\s;,]+)", RegexOptions.CultureInvariant)]
    private static partial Regex SecretPairRegex();

    [GeneratedRegex(@"(?i)([?&](?:access_token|refresh_token|token|code|secret)=)[^&#\s]+", RegexOptions.CultureInvariant)]
    private static partial Regex QuerySecretRegex();

    [GeneratedRegex(@"(?i)\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex JwtRegex();

    public string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value ?? string.Empty;
        var redacted = BearerRegex().Replace(value, "$1" + Mask);
        redacted = SecretPairRegex().Replace(redacted, "$1=" + Mask);
        redacted = QuerySecretRegex().Replace(redacted, "$1" + Mask);
        return JwtRegex().Replace(redacted, Mask);
    }

    public IReadOnlyList<RuntimeDiagnosticDetail> Redact(IReadOnlyList<RuntimeDiagnosticDetail> details) =>
        details.Select(detail => new RuntimeDiagnosticDetail(
            detail.Key,
            SensitiveKeys.Any(key => detail.Key.Contains(key, StringComparison.OrdinalIgnoreCase))
                ? Mask
                : Redact(detail.Value))).ToArray();
}

public sealed class SqliteRuntimeDiagnosticStore : IRuntimeDiagnosticSink, IRuntimeDiagnosticReader
{
    private readonly string _databasePath;
    private readonly RuntimeDiagnosticRetentionPolicy _retention;
    private readonly IDiagnosticRedactor _redactor;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _writes;

    public SqliteRuntimeDiagnosticStore(
        string databasePath,
        RuntimeDiagnosticRetentionPolicy? retention = null,
        IDiagnosticRedactor? redactor = null)
    {
        _databasePath = databasePath;
        _retention = retention ?? new RuntimeDiagnosticRetentionPolicy();
        _redactor = redactor ?? new DiagnosticRedactor();
        if (_retention.MaximumEventCount < 1) throw new ArgumentOutOfRangeException(nameof(retention));
        if (_retention.PruneEveryWrites < 1) throw new ArgumentOutOfRangeException(nameof(retention));
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS runtime_diagnostic_events(
                    sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                    event_id TEXT NOT NULL UNIQUE,
                    correlation_id TEXT NOT NULL,
                    timestamp_utc TEXT NOT NULL,
                    kind TEXT NOT NULL,
                    reason_code TEXT NOT NULL,
                    summary TEXT NOT NULL,
                    screen TEXT NULL, control_name TEXT NULL, command_name TEXT NULL, target TEXT NULL,
                    allowed INTEGER NULL, before_state TEXT NULL, after_state TEXT NULL,
                    project_run_id TEXT NULL, runtime_id TEXT NULL, exception_classification TEXT NULL,
                    detail_json TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_runtime_diagnostic_timestamp ON runtime_diagnostic_events(timestamp_utc DESC);
                CREATE INDEX IF NOT EXISTS ix_runtime_diagnostic_correlation ON runtime_diagnostic_events(correlation_id, sequence);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task RecordAsync(RuntimeDiagnosticRecord record, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT OR IGNORE INTO runtime_diagnostic_events
                (event_id,correlation_id,timestamp_utc,kind,reason_code,summary,screen,control_name,command_name,target,allowed,before_state,after_state,project_run_id,runtime_id,exception_classification,detail_json)
                VALUES($id,$correlation,$timestamp,$kind,$reason,$summary,$screen,$control,$command,$target,$allowed,$before,$after,$run,$runtime,$exception,$details);
                """;
            Add(command, "$id", record.Event.Id.ToString("D"));
            Add(command, "$correlation", record.Event.CorrelationId.ToString("D"));
            Add(command, "$timestamp", record.Event.Timestamp.ToUniversalTime().ToString("O"));
            Add(command, "$kind", record.Event.Kind.ToString());
            Add(command, "$reason", record.Event.ReasonCode);
            Add(command, "$summary", _redactor.Redact(record.Event.Summary));
            Add(command, "$screen", record.Event.Screen); Add(command, "$control", record.Event.Control);
            Add(command, "$command", record.Event.Command); Add(command, "$target", _redactor.Redact(record.Event.Target));
            Add(command, "$allowed", record.Event.Allowed is null ? null : record.Event.Allowed.Value ? 1 : 0);
            Add(command, "$before", _redactor.Redact(record.Event.BeforeState)); Add(command, "$after", _redactor.Redact(record.Event.AfterState));
            Add(command, "$run", record.Event.ProjectRunId); Add(command, "$runtime", record.Event.RuntimeId);
            Add(command, "$exception", record.ExceptionClassification);
            Add(command, "$details", JsonSerializer.Serialize(_redactor.Redact(record.Details)));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (Interlocked.Increment(ref _writes) % _retention.PruneEveryWrites == 0)
                await PruneCoreAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<RuntimeDiagnosticRecord>> ReadRecentAsync(int limit, CancellationToken cancellationToken = default)
    {
        if (limit < 1) return Array.Empty<RuntimeDiagnosticRecord>();
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT sequence,event_id,correlation_id,timestamp_utc,kind,reason_code,summary,screen,control_name,command_name,target,allowed,before_state,after_state,project_run_id,runtime_id,exception_classification,detail_json FROM runtime_diagnostic_events ORDER BY sequence DESC LIMIT $limit;";
            command.Parameters.AddWithValue("$limit", Math.Min(limit, _retention.MaximumEventCount));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var results = new List<RuntimeDiagnosticRecord>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) results.Add(Read(reader));
            return results;
        }
        finally { _gate.Release(); }
    }

    public async Task PruneAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false); await PruneCoreAsync(connection, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task PruneCoreAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM runtime_diagnostic_events WHERE timestamp_utc < $cutoff;
            DELETE FROM runtime_diagnostic_events WHERE sequence NOT IN
                (SELECT sequence FROM runtime_diagnostic_events ORDER BY sequence DESC LIMIT $maximum);
            """;
        command.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.Subtract(_retention.EffectiveMaximumAge).ToString("O"));
        command.Parameters.AddWithValue("$maximum", _retention.MaximumEventCount);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(_databasePath));
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        return await SqliteDurabilityConnection.OpenAsync(_databasePath, new(), cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static void Add(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static RuntimeDiagnosticRecord Read(SqliteDataReader reader)
    {
        string? N(int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
        bool? allowed = reader.IsDBNull(11) ? null : reader.GetInt32(11) != 0;
        var evt = new RuntimeDiagnosticEvent(Guid.Parse(reader.GetString(1)), Guid.Parse(reader.GetString(2)), DateTimeOffset.Parse(reader.GetString(3)), Enum.Parse<RuntimeDiagnosticKind>(reader.GetString(4)), reader.GetString(5), reader.GetString(6), N(7), N(8), N(9), N(10), allowed, N(12), N(13), N(14), N(15));
        var details = JsonSerializer.Deserialize<RuntimeDiagnosticDetail[]>(reader.GetString(17)) ?? [];
        return new(evt, details, N(16), reader.GetInt64(0));
    }
}

public sealed record DiagnosticSnapshotEnvelope(
    string Schema,
    string ApplicationVersion,
    string? SourceIdentity,
    DateTimeOffset ExportedAtUtc,
    RuntimeInspectorState CurrentState,
    IReadOnlyList<RuntimeDiagnosticRecord> RecentEvents);

public sealed class RuntimeDiagnosticSnapshotService
{
    private readonly IRuntimeDiagnosticReader _reader;
    private readonly IRuntimeInspectorStateSource _state;
    private readonly IDiagnosticRedactor _redactor;

    public RuntimeDiagnosticSnapshotService(IRuntimeDiagnosticReader reader, IRuntimeInspectorStateSource state, IDiagnosticRedactor? redactor = null)
    { _reader = reader; _state = state; _redactor = redactor ?? new DiagnosticRedactor(); }

    public async Task<string> CreateJsonAsync(string applicationVersion, string? sourceIdentity, int eventLimit = 250, CancellationToken cancellationToken = default)
    {
        var events = await _reader.ReadRecentAsync(Math.Clamp(eventLimit, 1, 1000), cancellationToken).ConfigureAwait(false);
        var safeEvents = events.Select(r => r with
        {
            Event = r.Event with { Summary = _redactor.Redact(r.Event.Summary), Target = _redactor.Redact(r.Event.Target), BeforeState = _redactor.Redact(r.Event.BeforeState), AfterState = _redactor.Redact(r.Event.AfterState) },
            Details = _redactor.Redact(r.Details)
        }).ToArray();
        var state = RedactState(await _state.CaptureAsync(cancellationToken).ConfigureAwait(false));
        var envelope = new DiagnosticSnapshotEnvelope("pcc-runtime-diagnostic/v1", applicationVersion, _redactor.Redact(sourceIdentity), DateTimeOffset.UtcNow, state, safeEvents);
        return JsonSerializer.Serialize(envelope, new JsonSerializerOptions { WriteIndented = true });
    }

    public async Task SaveAsync(string path, string applicationVersion, string? sourceIdentity, int eventLimit = 250, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(fullPath, await CreateJsonAsync(applicationVersion, sourceIdentity, eventLimit, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
    }

    private RuntimeInspectorState RedactState(RuntimeInspectorState state) => state with
    {
        Provider = _redactor.Redact(state.Provider), BrowserHealth = _redactor.Redact(state.BrowserHealth),
        ManagerState = _redactor.Redact(state.ManagerState), WorkerState = _redactor.Redact(state.WorkerState), DispatchState = _redactor.Redact(state.DispatchState),
        NextAction = state.NextAction is null ? null : state.NextAction with { Instruction = _redactor.Redact(state.NextAction.Instruction), Control = _redactor.Redact(state.NextAction.Control) },
        Prerequisites = state.Prerequisites.Select(p => p with { Prerequisite = _redactor.Redact(p.Prerequisite), ReasonCode = _redactor.Redact(p.ReasonCode) }).ToArray(),
        BrowserRuntimes = state.BrowserRuntimes.Select(b => b with { ProfileSource = _redactor.Redact(b.ProfileSource), RuntimeId = _redactor.Redact(b.RuntimeId), OwnershipProof = _redactor.Redact(b.OwnershipProof) }).ToArray()
    };
}
