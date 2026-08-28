using System.Security.Cryptography;
using System.Text;
using PCCExecutive.Domain;

namespace PCCExecutive.Application;

public enum ProjectModel { Standalone, ProductFamily }
public enum ProjectScopeKind { Project, Core, Variant }
public enum ProjectResolutionStatus { Success, ProjectNotFound, RoutingNotReady, RoutingConflict, VariantRequired, Unauthorized, RateLimited, TemporaryFailure, Offline, StaleCache }
public enum ExternalReadStatus { Success, NotFound, Unauthorized, RateLimited, TemporaryFailure, Offline, StaleCache, RoutingConflict }
public enum EvidenceFreshness { Current, Stale }
public enum EvidenceConfidence { High, Medium, Low, Unknown }

public sealed record ExternalResult<T>(
    ExternalReadStatus Status,
    T? Value,
    DateTimeOffset CapturedAt,
    bool IsStale = false,
    string? ErrorCode = null)
    where T : class
{
    public bool IsSuccess => Status is ExternalReadStatus.Success or ExternalReadStatus.StaleCache;
}

public sealed record ProjectControlProvenance(
    string Repository,
    string SourceSha,
    string? ControlPlaneVersion,
    string? RoutingContractVersion,
    DateTimeOffset CapturedAt,
    EvidenceFreshness Freshness);

public sealed record VariantRouteSnapshot(
    string VariantId,
    string DisplayName,
    IReadOnlyList<string> Aliases,
    string? ImplementationLocation,
    string ImplementationLocationState,
    string RoutingState);

public sealed record CanonicalTaskSnapshot(
    string TaskId,
    string ProjectControlId,
    string? RequirementId,
    string Title,
    string State,
    string? Priority,
    string? CanonicalBranch,
    string? BaseBranch,
    string? BaseSha,
    string? LatestPushedSha,
    string? TargetVersion,
    IReadOnlyList<string> Scope,
    IReadOnlyList<string> NonScope,
    IReadOnlyList<string> AcceptanceCriteria,
    IReadOnlyList<string> Dependencies,
    IReadOnlyList<string> Evidence);

public sealed record DesiredStateSnapshot(
    string ProjectControlId,
    string Repository,
    string? ControlPlaneVersion,
    string? DesiredPolicyVersion,
    string? PolicyEnforcementMode,
    bool? WriteAuthorized,
    string? CanonicalDevelopmentLineage,
    string? VersionPolicy,
    string? VersionSource,
    string? MonitorTargetScope,
    string? MonitorTargetVariant);

public sealed record ProjectRoutingSnapshot(
    string ProjectControlId,
    string DisplayName,
    string Repository,
    ProjectModel ProjectModel,
    ProjectScopeKind Scope,
    string? VariantId,
    string? VariantDisplayName,
    string? ImplementationLocation,
    string ConstitutionState,
    string RoutingState,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<CanonicalTaskSnapshot> CanonicalTasks,
    DesiredStateSnapshot? DesiredState,
    ProjectControlProvenance Provenance)
{
    public string RoutingIdentity =>
        string.Join("|", ProjectControlId, Repository, ProjectModel, Scope, VariantId ?? "", ImplementationLocation ?? "", ConstitutionState, RoutingState);
}

public sealed record ProjectResolution(
    ProjectResolutionStatus Status,
    ProjectRoutingSnapshot? Project,
    string? Message)
{
    public bool IsSuccess => Status is ProjectResolutionStatus.Success or ProjectResolutionStatus.StaleCache;
}

public interface IProjectControlResolver
{
    Task<ProjectResolution> ResolveProjectAsync(string nameOrAlias, CancellationToken cancellationToken = default);
    Task<ProjectResolution> GetProjectAsync(string projectControlId, string? variantId = null, ProjectScopeKind? scope = null, CancellationToken cancellationToken = default);
    Task<ExternalResult<ProjectRoutingSnapshot>> GetRoutingSnapshotAsync(string projectControlId, string? variantId = null, ProjectScopeKind? scope = null, CancellationToken cancellationToken = default);
    Task<ExternalResult<IReadOnlyList<CanonicalTaskSnapshot>>> GetCanonicalTasksAsync(string projectControlId, CancellationToken cancellationToken = default);
    Task<ExternalResult<DesiredStateSnapshot>> GetDesiredStateAsync(string projectControlId, CancellationToken cancellationToken = default);
}

public sealed record GitHubRepositorySnapshot(string Repository, string DefaultBranch, bool Private, bool Archived, string? HtmlUrl);
public sealed record GitHubBranchSnapshot(string Repository, string Name, string HeadSha, bool Protected);
public sealed record GitHubCommitSnapshot(string Repository, string Sha, string Message, DateTimeOffset? AuthoredAt, DateTimeOffset? CommittedAt, string? HtmlUrl);
public sealed record GitHubIssueSnapshot(string Repository, int Number, string Title, string State, DateTimeOffset? UpdatedAt, string? HtmlUrl);
public sealed record GitHubPullRequestSnapshot(
    string Repository,
    int Number,
    string Title,
    string State,
    bool Merged,
    string HeadBranch,
    string HeadSha,
    string BaseBranch,
    string BaseSha,
    IReadOnlyList<string> ChangedFiles,
    DateTimeOffset? UpdatedAt,
    string? HtmlUrl)
{
    public string ExactHeadEvidence => $"{Repository}#PR-{Number}@{HeadSha}";
}
public sealed record GitHubCheckSnapshot(string Name, string State, string? Conclusion, string? DetailsUrl);
public sealed record GitHubCheckSummary(string Repository, string CommitSha, string CombinedState, IReadOnlyList<GitHubCheckSnapshot> Checks);
public sealed record GitHubWorkflowRunSnapshot(long Id, string Name, string Status, string? Conclusion, string HeadSha, string? HtmlUrl);
public sealed record GitHubReleaseSnapshot(string TagName, string? Name, bool Draft, bool Prerelease, DateTimeOffset? PublishedAt, string? TargetCommitish, string? HtmlUrl);
public sealed record GitHubTagSnapshot(string Name, string CommitSha);

public interface IGitHubEvidenceClient
{
    Task<ExternalResult<GitHubRepositorySnapshot>> GetRepositoryAsync(string repository, CancellationToken cancellationToken = default);
    Task<ExternalResult<GitHubBranchSnapshot>> GetBranchAsync(string repository, string branch, CancellationToken cancellationToken = default);
    Task<ExternalResult<GitHubCommitSnapshot>> GetCommitAsync(string repository, string sha, CancellationToken cancellationToken = default);
    Task<ExternalResult<GitHubIssueSnapshot>> GetIssueAsync(string repository, int issueNumber, CancellationToken cancellationToken = default);
    Task<ExternalResult<GitHubPullRequestSnapshot>> GetPullRequestAsync(string repository, int pullRequestNumber, CancellationToken cancellationToken = default);
    Task<ExternalResult<IReadOnlyList<GitHubPullRequestSnapshot>>> ListPullRequestsAsync(string repository, string state = "open", CancellationToken cancellationToken = default);
    Task<ExternalResult<GitHubCheckSummary>> GetChecksAsync(string repository, string commitSha, CancellationToken cancellationToken = default);
    Task<ExternalResult<IReadOnlyList<GitHubWorkflowRunSnapshot>>> GetWorkflowRunsAsync(string repository, string commitSha, CancellationToken cancellationToken = default);
    Task<ExternalResult<GitHubReleaseSnapshot>> GetLatestReleaseAsync(string repository, CancellationToken cancellationToken = default);
    Task<ExternalResult<IReadOnlyList<GitHubTagSnapshot>>> ListTagsAsync(string repository, CancellationToken cancellationToken = default);
}

public sealed record ExternalEvidenceObservation(
    string Kind,
    string Source,
    string Locator,
    string? ExactHead,
    string Value,
    DateTimeOffset CapturedAt,
    EvidenceFreshness Freshness,
    EvidenceConfidence Confidence,
    ExternalReadStatus Status);

public sealed record EvidenceEnvelope(
    EvidenceRecord Record,
    string Locator,
    EvidenceFreshness Freshness,
    EvidenceConfidence Confidence,
    ExternalReadStatus Status);

public interface IEvidenceNormalizer
{
    EvidenceEnvelope Normalize(ProjectRunId projectRunId, TaskId? taskId, ExternalEvidenceObservation observation);
}

public sealed class EvidenceNormalizer : IEvidenceNormalizer
{
    public EvidenceEnvelope Normalize(ProjectRunId projectRunId, TaskId? taskId, ExternalEvidenceObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var fingerprintSource = string.Join("|", observation.Kind, observation.Source, observation.Locator, observation.ExactHead ?? "", observation.Value, observation.Status, observation.Freshness);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintSource))).ToLowerInvariant();
        var record = new EvidenceRecord(
            EvidenceId.New(),
            projectRunId,
            taskId,
            observation.Kind,
            observation.Source,
            fingerprint,
            observation.ExactHead,
            observation.CapturedAt);
        return new(record, observation.Locator, observation.Freshness, observation.Confidence, observation.Status);
    }
}

public sealed record ProjectBaselineSnapshot(
    string ProjectControlId,
    string DisplayName,
    string Repository,
    ProjectModel ProjectModel,
    ProjectScopeKind Scope,
    string? VariantId,
    string? ImplementationLocation,
    string PccSourceSha,
    string RoutingIdentity,
    string DefaultBranch,
    string DefaultHeadSha,
    IReadOnlyList<CanonicalTaskSnapshot> CanonicalTasks,
    IReadOnlyList<GitHubPullRequestSnapshot> RelevantPullRequests,
    GitHubCheckSummary? Checks,
    DesiredStateSnapshot? DesiredState,
    GitHubReleaseSnapshot? LatestRelease,
    IReadOnlyList<string> KnownBlockers,
    DateTimeOffset CapturedAt,
    EvidenceFreshness Freshness)
{
    public string CiState => Checks is not null && string.Equals(Checks.CommitSha, DefaultHeadSha, StringComparison.OrdinalIgnoreCase)
        ? Checks.CombinedState
        : "UNKNOWN";
    public string? CurrentVersion => LatestRelease?.TagName ?? CanonicalTasks.Select(x => x.TargetVersion).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
}

public interface IProjectBaselineBuilder
{
    Task<ExternalResult<ProjectBaselineSnapshot>> BuildAsync(string nameOrAlias, CancellationToken cancellationToken = default);
}

public sealed class ProjectBaselineBuilder : IProjectBaselineBuilder
{
    private readonly IProjectControlResolver _pcc;
    private readonly IGitHubEvidenceClient _github;

    public ProjectBaselineBuilder(IProjectControlResolver pcc, IGitHubEvidenceClient github)
    {
        _pcc = pcc;
        _github = github;
    }

    public async Task<ExternalResult<ProjectBaselineSnapshot>> BuildAsync(string nameOrAlias, CancellationToken cancellationToken = default)
    {
        var resolved = await _pcc.ResolveProjectAsync(nameOrAlias, cancellationToken);
        if (!resolved.IsSuccess || resolved.Project is null)
            return new(MapResolution(resolved.Status), null, DateTimeOffset.UtcNow, resolved.Status == ProjectResolutionStatus.StaleCache, resolved.Message);

        var route = resolved.Project;
        var repo = await _github.GetRepositoryAsync(route.Repository, cancellationToken);
        if (!repo.IsSuccess || repo.Value is null)
            return new(repo.Status, null, repo.CapturedAt, repo.IsStale, repo.ErrorCode);

        var branch = await _github.GetBranchAsync(route.Repository, repo.Value.DefaultBranch, cancellationToken);
        if (!branch.IsSuccess || branch.Value is null)
            return new(branch.Status, null, branch.CapturedAt, branch.IsStale, branch.ErrorCode);

        var prs = await _github.ListPullRequestsAsync(route.Repository, "all", cancellationToken);
        var relevantPrs = prs.Value is null ? Array.Empty<GitHubPullRequestSnapshot>() : FilterRelevant(route.CanonicalTasks, prs.Value);

        var checks = await _github.GetChecksAsync(route.Repository, branch.Value.HeadSha, cancellationToken);
        var release = await _github.GetLatestReleaseAsync(route.Repository, cancellationToken);

        var blockers = new List<string>();
        if (route.ConstitutionState != "READY") blockers.Add($"CONSTITUTION_STATE={route.ConstitutionState}");
        if (route.RoutingState != "READY") blockers.Add($"ROUTING_STATE={route.RoutingState}");
        if (!checks.IsSuccess && checks.Status is not ExternalReadStatus.NotFound) blockers.Add($"CI_EVIDENCE={checks.Status}");
        blockers.AddRange(route.CanonicalTasks
            .Where(task => task.State.Contains("BLOCK", StringComparison.OrdinalIgnoreCase))
            .Select(task => $"TASK_BLOCKED={task.TaskId}"));
        blockers.AddRange(relevantPrs
            .Where(pr => string.Equals(pr.State, "closed", StringComparison.OrdinalIgnoreCase) && !pr.Merged)
            .Select(pr => $"PR_CLOSED_UNMERGED=#{pr.Number}@{pr.HeadSha}"));

        var isStale = route.Provenance.Freshness == EvidenceFreshness.Stale || repo.IsStale || branch.IsStale || prs.IsStale || checks.IsStale || release.IsStale;
        var captured = new[] { route.Provenance.CapturedAt, repo.CapturedAt, branch.CapturedAt, prs.CapturedAt, checks.CapturedAt, release.CapturedAt }.Max();

        var snapshot = new ProjectBaselineSnapshot(
            route.ProjectControlId,
            route.DisplayName,
            route.Repository,
            route.ProjectModel,
            route.Scope,
            route.VariantId,
            route.ImplementationLocation,
            route.Provenance.SourceSha,
            route.RoutingIdentity,
            repo.Value.DefaultBranch,
            branch.Value.HeadSha,
            route.CanonicalTasks,
            relevantPrs,
            checks.Value,
            route.DesiredState,
            release.Value,
            blockers,
            captured,
            isStale ? EvidenceFreshness.Stale : EvidenceFreshness.Current);

        return new(isStale ? ExternalReadStatus.StaleCache : ExternalReadStatus.Success, snapshot, captured, isStale);
    }

    private static IReadOnlyList<GitHubPullRequestSnapshot> FilterRelevant(IReadOnlyList<CanonicalTaskSnapshot> tasks, IReadOnlyList<GitHubPullRequestSnapshot> pulls)
    {
        if (tasks.Count == 0) return pulls;
        var branches = tasks.Select(x => x.CanonicalBranch).Where(x => !string.IsNullOrWhiteSpace(x)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ids = tasks.Select(x => x.TaskId).ToArray();
        return pulls.Where(pr =>
            branches.Contains(pr.HeadBranch) ||
            ids.Any(id => pr.Title.Contains(id, StringComparison.OrdinalIgnoreCase))).ToArray();
    }

    private static ExternalReadStatus MapResolution(ProjectResolutionStatus status) => status switch
    {
        ProjectResolutionStatus.ProjectNotFound => ExternalReadStatus.NotFound,
        ProjectResolutionStatus.Unauthorized => ExternalReadStatus.Unauthorized,
        ProjectResolutionStatus.RateLimited => ExternalReadStatus.RateLimited,
        ProjectResolutionStatus.Offline => ExternalReadStatus.Offline,
        ProjectResolutionStatus.StaleCache => ExternalReadStatus.StaleCache,
        ProjectResolutionStatus.RoutingConflict or ProjectResolutionStatus.RoutingNotReady or ProjectResolutionStatus.VariantRequired => ExternalReadStatus.RoutingConflict,
        _ => ExternalReadStatus.TemporaryFailure
    };
}

public sealed record ManagerEvidencePacket(
    string Project,
    ProjectRoutingSnapshot Routing,
    string CurrentHead,
    IReadOnlyList<CanonicalTaskSnapshot> CanonicalTasks,
    IReadOnlyList<GitHubPullRequestSnapshot> OpenPullRequests,
    string CiState,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<EvidenceEnvelope> LatestEvidence,
    IReadOnlyList<CompletionGate> VerifiedCompletionInputs,
    DateTimeOffset CapturedAt,
    EvidenceFreshness Freshness);

public sealed record WorkerEvidencePacket(
    string Project,
    WorkerTask Task,
    TaskScope Scope,
    string BaseBranch,
    string CurrentHead,
    IReadOnlySet<TaskId> Dependencies,
    IReadOnlyList<GitHubPullRequestSnapshot> RelevantPullRequests,
    IReadOnlyList<EvidenceEnvelope> KnownEvidence,
    IReadOnlyList<string> AcceptanceCriteria,
    IReadOnlyList<string> DoNotTouch,
    DateTimeOffset CapturedAt,
    EvidenceFreshness Freshness);

public sealed class EvidencePacketBuilder
{
    public ManagerEvidencePacket BuildManager(
        ProjectBaselineSnapshot baseline,
        ProjectRoutingSnapshot routing,
        IReadOnlyList<EvidenceEnvelope>? evidence = null,
        IReadOnlyList<CompletionGate>? completionInputs = null) =>
        new(
            baseline.ProjectControlId,
            routing,
            baseline.DefaultHeadSha,
            baseline.CanonicalTasks,
            baseline.RelevantPullRequests.Where(pr => string.Equals(pr.State, "open", StringComparison.OrdinalIgnoreCase)).ToArray(),
            baseline.CiState,
            baseline.KnownBlockers,
            evidence ?? Array.Empty<EvidenceEnvelope>(),
            completionInputs ?? Array.Empty<CompletionGate>(),
            baseline.CapturedAt,
            baseline.Freshness);

    public WorkerEvidencePacket BuildWorker(
        ProjectBaselineSnapshot baseline,
        WorkerTask task,
        IReadOnlyList<EvidenceEnvelope>? evidence = null,
        IReadOnlyList<string>? doNotTouch = null)
    {
        var canonical = baseline.CanonicalTasks.FirstOrDefault();
        return new(
            baseline.ProjectControlId,
            task,
            task.Scope,
            canonical?.BaseBranch ?? baseline.DefaultBranch,
            baseline.DefaultHeadSha,
            task.Dependencies,
            baseline.RelevantPullRequests,
            evidence ?? Array.Empty<EvidenceEnvelope>(),
            task.AcceptanceCriteria,
            doNotTouch ?? Array.Empty<string>(),
            baseline.CapturedAt,
            baseline.Freshness);
    }
}

public enum ReconciliationDifferenceKind { HeadChanged, PrMerged, PrClosedUnmerged, CiChanged, TaskStateChanged, RoutingChanged, VersionChanged, EvidenceStale }
public sealed record ReconciliationDifference(ReconciliationDifferenceKind Kind, string Previous, string Current, string Description);
public sealed record ReconciliationSnapshot(ProjectBaselineSnapshot Persisted, ProjectBaselineSnapshot Live, IReadOnlyList<ReconciliationDifference> Differences)
{
    public bool HasContradiction => Differences.Count > 0;
}

public sealed class SnapshotReconciler
{
    public ReconciliationSnapshot Compare(ProjectBaselineSnapshot persisted, ProjectBaselineSnapshot live)
    {
        var differences = new List<ReconciliationDifference>();
        if (!string.Equals(persisted.DefaultHeadSha, live.DefaultHeadSha, StringComparison.OrdinalIgnoreCase))
            differences.Add(new(ReconciliationDifferenceKind.HeadChanged, persisted.DefaultHeadSha, live.DefaultHeadSha, "Default branch HEAD changed."));

        if (!string.Equals(persisted.RoutingIdentity, live.RoutingIdentity, StringComparison.Ordinal))
            differences.Add(new(ReconciliationDifferenceKind.RoutingChanged, persisted.RoutingIdentity, live.RoutingIdentity, "PCC routing identity changed."));

        if (!string.Equals(persisted.CiState, live.CiState, StringComparison.OrdinalIgnoreCase))
            differences.Add(new(ReconciliationDifferenceKind.CiChanged, persisted.CiState, live.CiState, "CI/check state changed."));

        if (!string.Equals(persisted.CurrentVersion, live.CurrentVersion, StringComparison.OrdinalIgnoreCase))
            differences.Add(new(ReconciliationDifferenceKind.VersionChanged, persisted.CurrentVersion ?? "", live.CurrentVersion ?? "", "Version/release evidence changed."));

        var oldTasks = persisted.CanonicalTasks.ToDictionary(x => x.TaskId, StringComparer.OrdinalIgnoreCase);
        foreach (var task in live.CanonicalTasks)
            if (oldTasks.TryGetValue(task.TaskId, out var old) && !string.Equals(old.State, task.State, StringComparison.OrdinalIgnoreCase))
                differences.Add(new(ReconciliationDifferenceKind.TaskStateChanged, old.State, task.State, $"Task {task.TaskId} state changed."));

        var oldPrs = persisted.RelevantPullRequests.ToDictionary(x => x.Number);
        foreach (var pr in live.RelevantPullRequests)
        {
            if (!oldPrs.TryGetValue(pr.Number, out var old)) continue;
            if (!old.Merged && pr.Merged)
                differences.Add(new(ReconciliationDifferenceKind.PrMerged, old.State, pr.State, $"PR #{pr.Number} merged at exact head {pr.HeadSha}."));
            else if (!string.Equals(old.State, pr.State, StringComparison.OrdinalIgnoreCase) && !pr.Merged && string.Equals(pr.State, "closed", StringComparison.OrdinalIgnoreCase))
                differences.Add(new(ReconciliationDifferenceKind.PrClosedUnmerged, old.State, pr.State, $"PR #{pr.Number} closed without merge."));
        }

        if (live.Freshness == EvidenceFreshness.Stale)
            differences.Add(new(ReconciliationDifferenceKind.EvidenceStale, persisted.Freshness.ToString(), live.Freshness.ToString(), "Live refresh unavailable; evidence is explicitly stale."));

        return new(persisted, live, differences);
    }
}