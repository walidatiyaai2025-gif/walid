using System.Net;
using System.Text.Json;
using PCCExecutive.Application;
using PCCExecutive.Domain;
using PCCExecutive.GitHub;
using PCCExecutive.Pcc;
using Xunit;

namespace PCCExecutive.Application.Tests;

public sealed class EvidenceIntegrationTests
{
    [Fact]
    public async Task Resolves_exact_alias_for_standalone_project()
    {
        var result = await Resolver().ResolveProjectAsync("pcc executive desktop");
        Assert.Equal(ProjectResolutionStatus.Success, result.Status);
        Assert.NotNull(result.Project);
        Assert.Equal("PCCEXECUTIVE", result.Project!.ProjectControlId);
        Assert.Equal(ProjectModel.Standalone, result.Project.ProjectModel);
        Assert.Equal(ProjectScopeKind.Project, result.Project.Scope);
    }

    [Fact]
    public async Task Unknown_project_returns_project_not_found()
    {
        var result = await Resolver().ResolveProjectAsync("does-not-exist");
        Assert.Equal(ProjectResolutionStatus.ProjectNotFound, result.Status);
        Assert.Null(result.Project);
    }

    [Fact]
    public async Task Alias_resolution_is_exact_not_fuzzy()
    {
        var result = await Resolver().ResolveProjectAsync("pcc exec");
        Assert.Equal(ProjectResolutionStatus.ProjectNotFound, result.Status);
    }

    [Fact]
    public async Task Product_family_core_scope_honors_PCC_core_readiness()
    {
        var result = await Resolver().GetProjectAsync("AIMWWEB", scope: ProjectScopeKind.Core);
        Assert.Equal(ProjectResolutionStatus.RoutingNotReady, result.Status);
        Assert.Null(result.Project);
    }

    [Fact]
    public async Task Resolves_product_family_variant_and_implementation_location()
    {
        var result = await Resolver().ResolveProjectAsync("laravel aiwmweb");
        Assert.Equal(ProjectResolutionStatus.Success, result.Status);
        Assert.Equal(ProjectModel.ProductFamily, result.Project!.ProjectModel);
        Assert.Equal(ProjectScopeKind.Variant, result.Project.Scope);
        Assert.Equal("LARAVEL_AIWMWEB", result.Project.VariantId);
        Assert.Equal("variants/laravel-aiwmweb", result.Project.ImplementationLocation);
    }

    [Fact]
    public async Task Project_family_without_variant_requires_explicit_route()
    {
        var source = CaptureSource(RoutingJson.Replace(
            "\"ALIASES\":[\"aimwweb\",\"aimw web\"]",
            "\"ALIASES\":[\"aimw parent\"]"));
        var result = await new PccProjectControlResolver(source).ResolveProjectAsync("AIMWWEB");
        Assert.Equal(ProjectResolutionStatus.VariantRequired, result.Status);
        Assert.Null(result.Project);
    }

    [Fact]
    public async Task Blocked_variant_fails_safe_instead_of_fabricating_destination()
    {
        var source = CaptureSource(RoutingJson.Replace(
            "\"ROUTING_STATE\":\"READY\",\"BOUNDARY_EVIDENCE_SHA\":\"v2\"",
            "\"ROUTING_STATE\":\"BLOCKED_UNRESOLVED\",\"BOUNDARY_EVIDENCE_SHA\":\"v2\""));
        var result = await new PccProjectControlResolver(source).ResolveProjectAsync("laravel aiwmweb");
        Assert.Equal(ProjectResolutionStatus.RoutingNotReady, result.Status);
        Assert.Null(result.Project);
    }

    [Fact]
    public async Task Stale_PCC_capture_is_never_presented_as_current()
    {
        var result = await Resolver(stale: true).ResolveProjectAsync("pcc executive");
        Assert.Equal(ProjectResolutionStatus.StaleCache, result.Status);
        Assert.Equal(EvidenceFreshness.Stale, result.Project!.Provenance.Freshness);
    }

    [Fact]
    public void Maps_GitHub_branch_and_exact_head()
    {
        using var json = JsonDocument.Parse("""{"name":"main","protected":false,"commit":{"sha":"abc123"}}""");
        var branch = GitHubPayloadMapper.Branch("owner/repo", json.RootElement);
        Assert.Equal("main", branch.Name);
        Assert.Equal("abc123", branch.HeadSha);
    }

    [Fact]
    public void Maps_PR_exact_head_and_merged_state()
    {
        using var json = JsonDocument.Parse("""
        {
          "number":4,"title":"worker","state":"closed","merged":true,"updated_at":"2026-08-27T23:00:00Z",
          "head":{"ref":"worker/test","sha":"deadbeef"},"base":{"ref":"task/base","sha":"base123"},"html_url":"https://github.test/pr/4"
        }
        """);
        var pr = GitHubPayloadMapper.PullRequest("owner/repo", json.RootElement, ["src/a.cs"]);
        Assert.True(pr.Merged);
        Assert.Equal("deadbeef", pr.HeadSha);
        Assert.Equal("owner/repo#PR-4@deadbeef", pr.ExactHeadEvidence);
        Assert.Single(pr.ChangedFiles);
    }

    [Fact]
    public void Open_PR_is_not_mapped_as_merged()
    {
        using var json = JsonDocument.Parse("""
        {"number":5,"title":"open","state":"open","merged_at":null,"head":{"ref":"worker/open","sha":"h1"},"base":{"ref":"main","sha":"b1"}}
        """);
        var pr = GitHubPayloadMapper.PullRequest("owner/repo", json.RootElement);
        Assert.False(pr.Merged);
        Assert.Equal("open", pr.State);
    }

    [Fact]
    public void Normalizes_commit_status_and_check_runs()
    {
        using var statuses = JsonDocument.Parse("""
        {"state":"failure","statuses":[{"context":"build","state":"failure","target_url":"https://ci/build"}]}
        """);
        using var checks = JsonDocument.Parse("""
        {"check_runs":[{"name":"tests","status":"completed","conclusion":"success","details_url":"https://ci/tests"}]}
        """);
        var result = GitHubPayloadMapper.Checks("owner/repo", "sha1", statuses.RootElement, checks.RootElement);
        Assert.Equal("failure", result.CombinedState);
        Assert.Equal(2, result.Checks.Count);
    }

    [Fact]
    public async Task Baseline_snapshot_uses_live_PCC_and_exact_GitHub_head()
    {
        var baseline = await new ProjectBaselineBuilder(Resolver(), FakeGitHub()).BuildAsync("pcc executive");
        Assert.Equal(ExternalReadStatus.Success, baseline.Status);
        Assert.Equal("live-main-sha", baseline.Value!.DefaultHeadSha);
        Assert.Equal("PCCEXECUTIVE-T0001", baseline.Value.CanonicalTasks.Single().TaskId);
        Assert.Equal("failure", baseline.Value.CiState);
        Assert.Equal("0.1.0", baseline.Value.CurrentVersion);
    }

    [Fact]
    public async Task Manager_packet_is_built_from_baseline_not_recursive_summary()
    {
        var resolver = Resolver();
        var routing = (await resolver.ResolveProjectAsync("pcc executive")).Project!;
        var baseline = (await new ProjectBaselineBuilder(resolver, FakeGitHub()).BuildAsync("pcc executive")).Value!;
        var evidence = new EvidenceNormalizer().Normalize(
            ProjectRunId.New(),
            null,
            new("GITHUB_BRANCH_HEAD", "GitHub", "owner/repo:main", "live-main-sha", "main@live-main-sha", DateTimeOffset.UtcNow, EvidenceFreshness.Current, EvidenceConfidence.High, ExternalReadStatus.Success));
        var packet = new EvidencePacketBuilder().BuildManager(baseline, routing, [evidence]);
        Assert.Equal("live-main-sha", packet.CurrentHead);
        Assert.Single(packet.LatestEvidence);
        Assert.Equal("failure", packet.CiState);
    }

    [Fact]
    public async Task Worker_packet_contains_scope_dependencies_acceptance_and_do_not_touch()
    {
        var baseline = (await new ProjectBaselineBuilder(Resolver(), FakeGitHub()).BuildAsync("pcc executive")).Value!;
        var dependency = TaskId.New();
        var task = new WorkerTask(
            TaskId.New(),
            "Read live evidence",
            TaskScope.Create("walidatiyaai2025-gif/walid", ["src/PCCExecutive.Pcc"]),
            new HashSet<TaskId> { dependency },
            ["routing is exact"],
            TaskState.Ready,
            "fingerprint");
        var packet = new EvidencePacketBuilder().BuildWorker(baseline, task, doNotTouch: ["SQLite", "Browser"]);
        Assert.Equal(baseline.DefaultHeadSha, packet.CurrentHead);
        Assert.Contains(dependency, packet.Dependencies);
        Assert.Contains("routing is exact", packet.AcceptanceCriteria);
        Assert.Contains("SQLite", packet.DoNotTouch);
    }

    [Fact]
    public async Task Reconciliation_surfaces_head_CI_task_and_stale_changes()
    {
        var old = (await new ProjectBaselineBuilder(Resolver(), FakeGitHub()).BuildAsync("pcc executive")).Value!;
        var live = old with
        {
            DefaultHeadSha = "new-head",
            Checks = new GitHubCheckSummary(old.Repository, "new-head", "success", []),
            CanonicalTasks = old.CanonicalTasks.Select(task => task with { State = "DONE" }).ToArray(),
            Freshness = EvidenceFreshness.Stale
        };
        var reconciliation = new SnapshotReconciler().Compare(old, live);
        Assert.Contains(reconciliation.Differences, x => x.Kind == ReconciliationDifferenceKind.HeadChanged);
        Assert.Contains(reconciliation.Differences, x => x.Kind == ReconciliationDifferenceKind.CiChanged);
        Assert.Contains(reconciliation.Differences, x => x.Kind == ReconciliationDifferenceKind.TaskStateChanged);
        Assert.Contains(reconciliation.Differences, x => x.Kind == ReconciliationDifferenceKind.EvidenceStale);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, null, ExternalReadStatus.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, "0", ExternalReadStatus.RateLimited)]
    [InlineData(HttpStatusCode.ServiceUnavailable, null, ExternalReadStatus.TemporaryFailure)]
    public void GitHub_failure_states_are_explicit(HttpStatusCode code, string? remaining, ExternalReadStatus expected) =>
        Assert.Equal(expected, GitHubFailureClassifier.Classify(code, remaining));

    [Fact]
    public void Evidence_normalizer_retains_exact_head_provenance()
    {
        var envelope = new EvidenceNormalizer().Normalize(
            ProjectRunId.New(),
            null,
            new("GITHUB_PR_HEAD", "GitHub", "owner/repo#4", "exact-sha", "open", DateTimeOffset.UtcNow, EvidenceFreshness.Current, EvidenceConfidence.High, ExternalReadStatus.Success));
        Assert.Equal("exact-sha", envelope.Record.ExactHead);
        Assert.Equal(EvidenceFreshness.Current, envelope.Freshness);
    }

    private static PccProjectControlResolver Resolver(bool stale = false) => new(CaptureSource(stale: stale));

    private static IPccDocumentSource CaptureSource(string? routing = null, bool stale = false) =>
        new FakePccSource(new PccDocumentCapture(
            stale ? ExternalReadStatus.StaleCache : ExternalReadStatus.Success,
            "pcc-live-sha",
            DateTimeOffset.Parse("2026-08-28T00:00:00Z"),
            stale,
            new Dictionary<string, string>
            {
                [GitHubPccDocumentSource.RoutingPath] = routing ?? RoutingJson,
                [GitHubPccDocumentSource.ProjectsPath] = ProjectsJson,
                [GitHubPccDocumentSource.DesiredStatePath] = DesiredJson
            }));

    private static FakeGitHubClient FakeGitHub() => new();

    private sealed class FakePccSource(PccDocumentCapture capture) : IPccDocumentSource
    {
        public Task<PccDocumentCapture> CaptureAsync(CancellationToken cancellationToken = default) => Task.FromResult(capture);
    }

    private sealed class FakeGitHubClient : IGitHubEvidenceClient
    {
        private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-28T00:01:00Z");

        public Task<ExternalResult<GitHubRepositorySnapshot>> GetRepositoryAsync(string repository, CancellationToken cancellationToken = default) =>
            Ok(new GitHubRepositorySnapshot(repository, "main", false, false, $"https://github.com/{repository}"));

        public Task<ExternalResult<GitHubBranchSnapshot>> GetBranchAsync(string repository, string branch, CancellationToken cancellationToken = default) =>
            Ok(new GitHubBranchSnapshot(repository, branch, "live-main-sha", false));

        public Task<ExternalResult<GitHubCommitSnapshot>> GetCommitAsync(string repository, string sha, CancellationToken cancellationToken = default) =>
            Ok(new GitHubCommitSnapshot(repository, sha, "commit", Now, Now, null));

        public Task<ExternalResult<GitHubIssueSnapshot>> GetIssueAsync(string repository, int issueNumber, CancellationToken cancellationToken = default) =>
            Ok(new GitHubIssueSnapshot(repository, issueNumber, "issue", "open", Now, null));

        public Task<ExternalResult<GitHubPullRequestSnapshot>> GetPullRequestAsync(string repository, int pullRequestNumber, CancellationToken cancellationToken = default) =>
            Ok(Pull(repository));

        public Task<ExternalResult<IReadOnlyList<GitHubPullRequestSnapshot>>> ListPullRequestsAsync(string repository, string state = "open", CancellationToken cancellationToken = default) =>
            Ok<IReadOnlyList<GitHubPullRequestSnapshot>>([Pull(repository)]);

        public Task<ExternalResult<GitHubCheckSummary>> GetChecksAsync(string repository, string commitSha, CancellationToken cancellationToken = default) =>
            Ok(new GitHubCheckSummary(repository, commitSha, "failure", [new("tests", "completed", "failure", null)]));

        public Task<ExternalResult<IReadOnlyList<GitHubWorkflowRunSnapshot>>> GetWorkflowRunsAsync(string repository, string commitSha, CancellationToken cancellationToken = default) =>
            Ok<IReadOnlyList<GitHubWorkflowRunSnapshot>>([]);

        public Task<ExternalResult<GitHubReleaseSnapshot>> GetLatestReleaseAsync(string repository, CancellationToken cancellationToken = default) =>
            Ok(new GitHubReleaseSnapshot("0.1.0", "dev", false, false, Now, "main", null));

        public Task<ExternalResult<IReadOnlyList<GitHubTagSnapshot>>> ListTagsAsync(string repository, CancellationToken cancellationToken = default) =>
            Ok<IReadOnlyList<GitHubTagSnapshot>>([new("0.1.0", "live-main-sha")]);

        private static GitHubPullRequestSnapshot Pull(string repository) =>
            new(repository, 4, "PCCEXECUTIVE-T0001: evidence", "open", false, "task/pcc-executive-t0001-v1", "pr-head", "main", "base", ["src/a.cs"], Now, null);

        private static Task<ExternalResult<T>> Ok<T>(T value) where T : class =>
            Task.FromResult(new ExternalResult<T>(ExternalReadStatus.Success, value, Now));
    }

    private const string RoutingJson = """
    {
      "CONTROL_PLANE_VERSION":"v1.6.0",
      "ROUTING_CONTRACT_VERSION":"1.2.0",
      "PROJECTS":[
        {
          "PROJECT_ID":"PCCEXECUTIVE","DISPLAY_NAME":"PCC Executive","REPOSITORY":"walidatiyaai2025-gif/walid",
          "PROJECT_MODEL":"STANDALONE","ALIASES":["pcc executive","pcc executive desktop","walid"],
          "CONSTITUTION_STATE":"READY","DEFAULT_SCOPE":"PROJECT","VARIANTS":[]
        },
        {
          "PROJECT_ID":"AIMWWEB","DISPLAY_NAME":"AIMWWeb","REPOSITORY":"walidatiyaai2025-gif/AIMWWeb",
          "PROJECT_MODEL":"PRODUCT_FAMILY","ALIASES":["aimwweb","aimw web"],"CONSTITUTION_STATE":"READY",
          "DEFAULT_SCOPE":null,"CORE_ROUTING_STATE":"BLOCKED_UNRESOLVED",
          "VARIANTS":[
            {
              "VARIANT_ID":"AIMWWEB_CURRENT","DISPLAY_NAME":"AIMWWeb Current","ALIASES":["current aimwweb"],
              "IMPLEMENTATION_LOCATION":".","IMPLEMENTATION_LOCATION_STATE":"MAPPED","ROUTING_STATE":"READY","BOUNDARY_EVIDENCE_SHA":"v1"
            },
            {
              "VARIANT_ID":"LARAVEL_AIWMWEB","DISPLAY_NAME":"Laravel AIWMWeb","ALIASES":["laravel aiwmweb","laravel edition"],
              "IMPLEMENTATION_LOCATION":"variants/laravel-aiwmweb","IMPLEMENTATION_LOCATION_STATE":"MAPPED","ROUTING_STATE":"READY","BOUNDARY_EVIDENCE_SHA":"v2"
            }
          ]
        }
      ]
    }
    """;

    private const string ProjectsJson = """
    {
      "CONTROL_PLANE_VERSION":"v1.6.0",
      "PROJECTS":[
        {
          "PROJECT_ID":"PCCEXECUTIVE","DISPLAY_NAME":"PCC Executive","REPOSITORY":"walidatiyaai2025-gif/walid",
          "TASKS":[
            {
              "TASK_ID":"PCCEXECUTIVE-T0001","PROJECT_ID":"PCCEXECUTIVE","REQUIREMENT_ID":"ISSUE-1",
              "TITLE":"PCC Executive v1","STATE":"IN_PROGRESS","PRIORITY":"P0",
              "CANONICAL_BRANCH":"task/pcc-executive-t0001-v1","BASE_BRANCH":"main","BASE_SHA":"base",
              "LATEST_PUSHED_SHA":null,"TARGET_VERSION":"0.1.0",
              "SCOPE":["v1"],"NON_SCOPE":["unrelated"],"ACCEPTANCE_CRITERIA":["exact evidence"],
              "DEPENDENCIES":[],"EVIDENCE":["Issue #1"]
            }
          ]
        },
        {"PROJECT_ID":"AIMWWEB","TASKS":[]}
      ]
    }
    """;

    private const string DesiredJson = """
    {
      "CONTROL_PLANE_VERSION":"v1.6.0",
      "PROJECTS":[
        {
          "PROJECT_ID":"PCCEXECUTIVE","REPOSITORY":"walidatiyaai2025-gif/walid","CONTROL_PLANE_VERSION":"v1.6.0",
          "DESIRED_POLICY_VERSION":"1.1.0","POLICY_ENFORCEMENT_MODE":"OBSERVE","WRITE_AUTHORIZED":false,
          "CANONICAL_DEVELOPMENT_LINEAGE":"main","VERSION_POLICY":"semver","VERSION_SOURCE":"repository"
        },
        {"PROJECT_ID":"AIMWWEB","REPOSITORY":"walidatiyaai2025-gif/AIMWWeb","WRITE_AUTHORIZED":false}
      ]
    }
    """;
}
