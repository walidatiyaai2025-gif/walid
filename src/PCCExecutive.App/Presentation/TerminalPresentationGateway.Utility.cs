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
    private static HealthState AggregateHealth(IEnumerable<HealthState> states)
    {
        var values = states.ToArray();
        if (values.Length == 0) return HealthState.Unknown;
        foreach (var state in new[] { HealthState.Challenge, HealthState.LoginRequired, HealthState.AdapterUncertain, HealthState.Offline, HealthState.RateLimited, HealthState.Failed, HealthState.Recovering, HealthState.Stuck, HealthState.TemporaryError, HealthState.PartialResponse, HealthState.Slow, HealthState.Generating, HealthState.Ready, HealthState.Healthy })
            if (values.Contains(state)) return state;
        return HealthState.Unknown;
    }

    private static string LoginState(IEnumerable<HealthState> states)
    {
        var values = states.ToArray();
        if (values.Contains(HealthState.Challenge)) return "CHALLENGE";
        if (values.Contains(HealthState.LoginRequired)) return "LOGIN_REQUIRED";
        if (values.Any(x => x is HealthState.Ready or HealthState.Healthy or HealthState.Generating)) return "AUTHENTICATED / READY";
        return values.Length == 0 ? "NOT_CHECKED" : "UNKNOWN";
    }

    private static string RecoveryLabel(HealthState health) => health switch
    {
        HealthState.RateLimited => "Runtime cooldown / global send pause policy",
        HealthState.Offline => "Recovery watch; do not discard project state",
        HealthState.Recovering => "Automatic recovery active",
        HealthState.PartialResponse => "Preserve partial response; reconcile before continuation",
        HealthState.AdapterUncertain => "Safe-fail: no new send until semantics are proven",
        HealthState.LoginRequired or HealthState.Challenge => "Manual action required in exact PCC browser",
        _ => "None"
    };

    private static string HealthScope(HealthState health) => health switch
    {
        HealthState.RateLimited or HealthState.LoginRequired or HealthState.Challenge or HealthState.Offline => "GLOBAL",
        HealthState.AdapterUncertain or HealthState.PartialResponse or HealthState.Stuck => "PER_SESSION",
        _ => "NONE"
    };

    private SessionSummary? FindSession(string? id) => string.IsNullOrWhiteSpace(id) ? null : Snapshot.Sessions.FirstOrDefault(x => StringComparer.Ordinal.Equals(x.RuntimeId, id));
    private AttentionSummary? FindAttention(string? id) => string.IsNullOrWhiteSpace(id) ? null : Snapshot.AttentionItems.FirstOrDefault(x => StringComparer.Ordinal.Equals(x.Id, id));

    private static void Ensure(SessionActionResult result)
    {
        if (!result.Succeeded) throw new InvalidOperationException(result.Reason);
    }

    private static bool HasEvidence(ChatGptSemanticSnapshot snapshot, params string[] terms)
    {
        var evidence = snapshot.Input.Evidence
            .Concat(snapshot.Generation.Evidence)
            .Concat(snapshot.Auth.Evidence)
            .Concat(snapshot.Conversation.Evidence)
            .Concat(snapshot.Health.Evidence);
        return evidence.Any(item => terms.Any(term => item.Contains(term, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool IsActiveWorker(string state) => state is "ACTIVE" or "DEGRADED" or "RECOVERING";

    private static string ScopeText(TaskScope scope) =>
        string.Join(" · ", new[]
        {
            scope.Repository,
            scope.Paths.Count == 0 ? null : string.Join(", ", scope.Paths),
            scope.Components.Count == 0 ? null : string.Join(", ", scope.Components)
        }.Where(x => !string.IsNullOrWhiteSpace(x)));

    private static string LatestPr(ProjectBaselineSnapshot? baseline)
    {
        var pr = baseline?.RelevantPullRequests.OrderByDescending(x => x.UpdatedAt).FirstOrDefault();
        return pr is null ? "—" : $"#{pr.Number} {pr.State}{(pr.Merged ? " · MERGED" : string.Empty)} @ {Short(pr.HeadSha)}";
    }

    private static string Short(string? sha) => string.IsNullOrWhiteSpace(sha) ? "—" : sha[..Math.Min(8, sha.Length)];

    private static ProjectId DeterministicProjectId(string projectId) => new(DeterministicGuid($"project:{projectId}"));
    private static ProjectRunId DeterministicRunId(string projectId) => new(DeterministicGuid($"run:{projectId}"));
    private static LogicalAgentId DeterministicAgentId(string projectId, string role) => new(DeterministicGuid($"agent:{projectId}:{role}"));

    private static Guid DeterministicGuid(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(bytes.Take(16).ToArray());
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null ? value.ToString() : null;

    private static string? FindString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        return null;
    }

    private static Dictionary<string, string> ParseSettings(string? value)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in (value ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var index = part.IndexOf('=');
            if (index <= 0) continue;
            result[part[..index].Trim()] = part[(index + 1)..].Trim();
        }
        return result;
    }

    private static T ParseEnum<T>(IReadOnlyDictionary<string, string> values, string key, T fallback) where T : struct, Enum =>
        values.TryGetValue(key, out var text) && Enum.TryParse<T>(text, true, out var parsed) ? parsed : fallback;

    private static int ParseInt(IReadOnlyDictionary<string, string> values, string key, int fallback, int min, int max) =>
        values.TryGetValue(key, out var text) && int.TryParse(text, out var parsed) ? Math.Clamp(parsed, min, max) : fallback;

    private static bool ParseBool(IReadOnlyDictionary<string, string> values, string key, bool fallback) =>
        values.TryGetValue(key, out var text) && bool.TryParse(text, out var parsed) ? parsed : fallback;

    private static ProjectResolutionStatus MapResolution(ExternalReadStatus status) => status switch
    {
        ExternalReadStatus.NotFound => ProjectResolutionStatus.ProjectNotFound,
        ExternalReadStatus.Unauthorized => ProjectResolutionStatus.Unauthorized,
        ExternalReadStatus.RateLimited => ProjectResolutionStatus.RateLimited,
        ExternalReadStatus.Offline => ProjectResolutionStatus.Offline,
        ExternalReadStatus.StaleCache => ProjectResolutionStatus.StaleCache,
        ExternalReadStatus.RoutingConflict => ProjectResolutionStatus.RoutingConflict,
        _ => ProjectResolutionStatus.TemporaryFailure
    };

    private bool UpdateManifestConfigured()
    {
        var source = Environment.GetEnvironmentVariable("PCC_EXECUTIVE_UPDATE_MANIFEST");
        return !string.IsNullOrWhiteSpace(source) && File.Exists(source);
    }

    public async ValueTask DisposeAsync()
    {
        _pccHttp.Dispose();
        _githubHttp.Dispose();
        await _store.DisposeAsync().ConfigureAwait(false);
        _projectLock.Dispose();
    }

    private sealed record UiPreferences(DispatchMode DispatchMode, bool AutoPauseOnLimit, bool DuplicateSendProtection)
    {
        public static UiPreferences Default { get; } = new(DispatchMode.AutomaticStaged, true, true);
    }
}
