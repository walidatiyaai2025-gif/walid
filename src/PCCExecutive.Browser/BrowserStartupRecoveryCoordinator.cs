namespace PCCExecutive.Browser;

public sealed record BrowserStartupRecoveryResult(
    bool StartupMayContinue,
    IReadOnlyList<SessionActionResult> Reconciliations,
    IReadOnlyList<string> UnresolvedRuntimeIds);

public sealed class BrowserStartupRecoveryCoordinator
{
    private readonly IBrowserRuntimeRegistry _registry;
    private readonly BrowserSessionController _sessions;

    public BrowserStartupRecoveryCoordinator(IBrowserRuntimeRegistry registry, BrowserSessionController sessions)
    {
        _registry = registry;
        _sessions = sessions;
    }

    public async Task<BrowserStartupRecoveryResult> ReconcileAsync(string? projectRunId = null, CancellationToken cancellationToken = default)
    {
        var persisted = await _registry.ListAsync(cancellationToken).ConfigureAwait(false);
        var candidates = persisted
            .Where(x => !x.IsArchived && x.State is not BrowserSessionState.Killed and not BrowserSessionState.Archived)
            .Where(x => projectRunId is null || StringComparer.Ordinal.Equals(x.ProjectRunId, projectRunId))
            .GroupBy(x => (x.ProjectRunId, x.LogicalAgentId), StringTupleComparer.Instance)
            .Select(group => group.OrderByDescending(x => x.LastActivityAt).ThenByDescending(x => x.LastHeartbeatAt).First())
            .OrderBy(x => x.LogicalAgentId, StringComparer.Ordinal)
            .ToArray();

        var results = new List<SessionActionResult>(candidates.Length);
        foreach (var runtime in candidates)
            results.Add(await _sessions.RecoverOrphanAsync(runtime.RuntimeId, cancellationToken).ConfigureAwait(false));

        var unresolved = results.Where(x => !x.Succeeded).Select(x => x.RuntimeId).ToArray();
        return new(unresolved.Length == 0, results, unresolved);
    }

    private sealed class StringTupleComparer : IEqualityComparer<(string ProjectRunId, string LogicalAgentId)>
    {
        public static StringTupleComparer Instance { get; } = new();
        public bool Equals((string ProjectRunId, string LogicalAgentId) x, (string ProjectRunId, string LogicalAgentId) y) =>
            StringComparer.Ordinal.Equals(x.ProjectRunId, y.ProjectRunId) && StringComparer.Ordinal.Equals(x.LogicalAgentId, y.LogicalAgentId);
        public int GetHashCode((string ProjectRunId, string LogicalAgentId) value) =>
            HashCode.Combine(StringComparer.Ordinal.GetHashCode(value.ProjectRunId), StringComparer.Ordinal.GetHashCode(value.LogicalAgentId));
    }
}
