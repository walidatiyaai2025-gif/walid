using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using PCCExecutive.Application;

namespace PCCExecutive.GitHub;

public static class GitHubFailureClassifier
{
    public static ExternalReadStatus Classify(HttpStatusCode statusCode, string? rateLimitRemaining = null)
    {
        if (statusCode == HttpStatusCode.NotFound) return ExternalReadStatus.NotFound;
        if (statusCode == HttpStatusCode.Unauthorized) return ExternalReadStatus.Unauthorized;
        if (statusCode == HttpStatusCode.Forbidden && string.Equals(rateLimitRemaining, "0", StringComparison.Ordinal))
            return ExternalReadStatus.RateLimited;
        if (statusCode == HttpStatusCode.Forbidden) return ExternalReadStatus.Unauthorized;
        if ((int)statusCode >= 500) return ExternalReadStatus.TemporaryFailure;
        return ExternalReadStatus.TemporaryFailure;
    }
}

public static class GitHubPayloadMapper
{
    public static GitHubRepositorySnapshot Repository(string repository, JsonElement root) => new(
        repository,
        GetString(root, "default_branch") ?? "main",
        GetBool(root, "private"),
        GetBool(root, "archived"),
        GetString(root, "html_url"));

    public static GitHubBranchSnapshot Branch(string repository, JsonElement root) => new(
        repository,
        GetString(root, "name") ?? "",
        root.GetProperty("commit").GetProperty("sha").GetString() ?? "",
        GetBool(root, "protected"));

    public static GitHubCommitSnapshot Commit(string repository, JsonElement root)
    {
        var commit = root.TryGetProperty("commit", out var commitElement) ? commitElement : root;
        var message = GetString(commit, "message") ?? "";
        var authored = commit.TryGetProperty("author", out var author) ? ParseDate(GetString(author, "date")) : null;
        var committed = commit.TryGetProperty("committer", out var committer) ? ParseDate(GetString(committer, "date")) : null;
        return new(repository, GetString(root, "sha") ?? "", message, authored, committed, GetString(root, "html_url"));
    }

    public static GitHubIssueSnapshot Issue(string repository, JsonElement root) => new(
        repository,
        GetInt(root, "number"),
        GetString(root, "title") ?? "",
        GetString(root, "state") ?? "unknown",
        ParseDate(GetString(root, "updated_at")),
        GetString(root, "html_url"));

    public static GitHubPullRequestSnapshot PullRequest(string repository, JsonElement root, IReadOnlyList<string>? changedFiles = null)
    {
        var head = root.GetProperty("head");
        var @base = root.GetProperty("base");
        var merged = root.TryGetProperty("merged", out var mergedElement) && mergedElement.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? mergedElement.GetBoolean()
            : root.TryGetProperty("merged_at", out var mergedAt) && mergedAt.ValueKind != JsonValueKind.Null;

        return new(
            repository,
            GetInt(root, "number"),
            GetString(root, "title") ?? "",
            GetString(root, "state") ?? "unknown",
            merged,
            GetString(head, "ref") ?? "",
            GetString(head, "sha") ?? "",
            GetString(@base, "ref") ?? "",
            GetString(@base, "sha") ?? "",
            changedFiles ?? Array.Empty<string>(),
            ParseDate(GetString(root, "updated_at")),
            GetString(root, "html_url"));
    }

    public static GitHubCheckSummary Checks(string repository, string sha, JsonElement statusRoot, JsonElement? checkRunsRoot)
    {
        var checks = new List<GitHubCheckSnapshot>();
        if (statusRoot.TryGetProperty("statuses", out var statuses) && statuses.ValueKind == JsonValueKind.Array)
        {
            checks.AddRange(statuses.EnumerateArray().Select(item => new GitHubCheckSnapshot(
                GetString(item, "context") ?? "commit-status",
                GetString(item, "state") ?? "unknown",
                null,
                GetString(item, "target_url"))));
        }

        if (checkRunsRoot is not null &&
            checkRunsRoot.Value.TryGetProperty("check_runs", out var checkRuns) &&
            checkRuns.ValueKind == JsonValueKind.Array)
        {
            checks.AddRange(checkRuns.EnumerateArray().Select(item => new GitHubCheckSnapshot(
                GetString(item, "name") ?? "check-run",
                GetString(item, "status") ?? "unknown",
                GetString(item, "conclusion"),
                GetString(item, "details_url"))));
        }

        var combined = GetString(statusRoot, "state") ?? Aggregate(checks);
        return new(repository, sha, combined, checks);
    }

    public static IReadOnlyList<GitHubWorkflowRunSnapshot> WorkflowRuns(JsonElement root)
    {
        if (!root.TryGetProperty("workflow_runs", out var runs) || runs.ValueKind != JsonValueKind.Array)
            return Array.Empty<GitHubWorkflowRunSnapshot>();

        return runs.EnumerateArray().Select(item => new GitHubWorkflowRunSnapshot(
            GetLong(item, "id"),
            GetString(item, "name") ?? "",
            GetString(item, "status") ?? "unknown",
            GetString(item, "conclusion"),
            GetString(item, "head_sha") ?? "",
            GetString(item, "html_url"))).ToArray();
    }

    public static GitHubReleaseSnapshot Release(JsonElement root) => new(
        GetString(root, "tag_name") ?? "",
        GetString(root, "name"),
        GetBool(root, "draft"),
        GetBool(root, "prerelease"),
        ParseDate(GetString(root, "published_at")),
        GetString(root, "target_commitish"),
        GetString(root, "html_url"));

    public static IReadOnlyList<GitHubTagSnapshot> Tags(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array) return Array.Empty<GitHubTagSnapshot>();
        return root.EnumerateArray().Select(item =>
        {
            var commit = item.GetProperty("commit");
            return new GitHubTagSnapshot(GetString(item, "name") ?? "", GetString(commit, "sha") ?? "");
        }).ToArray();
    }

    public static IReadOnlyList<string> ChangedFiles(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
        return root.EnumerateArray()
            .Select(item => GetString(item, "filename"))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToArray();
    }

    private static string Aggregate(IReadOnlyList<GitHubCheckSnapshot> checks)
    {
        if (checks.Count == 0) return "UNKNOWN";
        if (checks.Any(x => string.Equals(x.Conclusion, "failure", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(x.Conclusion, "cancelled", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(x.State, "failure", StringComparison.OrdinalIgnoreCase))) return "failure";
        if (checks.Any(x => string.Equals(x.State, "queued", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(x.State, "in_progress", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(x.State, "pending", StringComparison.OrdinalIgnoreCase))) return "pending";
        if (checks.All(x => string.Equals(x.Conclusion, "success", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(x.State, "success", StringComparison.OrdinalIgnoreCase))) return "success";
        return "UNKNOWN";
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString()
            : null;

    private static bool GetBool(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False && value.GetBoolean();

    private static int GetInt(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt32(out var result) ? result : 0;

    private static long GetLong(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt64(out var result) ? result : 0;

    private static DateTimeOffset? ParseDate(string? value) =>
        DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
}

public sealed class GitHubRestEvidenceClient : IGitHubEvidenceClient
{
    private readonly HttpClient _httpClient;

    public GitHubRestEvidenceClient(HttpClient httpClient, string? token = null)
    {
        _httpClient = httpClient;
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("PCCExecutive/0.1.0");
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        if (!string.IsNullOrWhiteSpace(token))
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public async Task<ExternalResult<GitHubRepositorySnapshot>> GetRepositoryAsync(string repository, CancellationToken cancellationToken = default)
    {
        var result = await GetJsonAsync(Api(repository, ""), cancellationToken);
        return Map(result, root => GitHubPayloadMapper.Repository(repository, root));
    }

    public async Task<ExternalResult<GitHubBranchSnapshot>> GetBranchAsync(string repository, string branch, CancellationToken cancellationToken = default)
    {
        var result = await GetJsonAsync(Api(repository, $"branches/{Uri.EscapeDataString(branch)}"), cancellationToken);
        return Map(result, root => GitHubPayloadMapper.Branch(repository, root));
    }

    public async Task<ExternalResult<GitHubCommitSnapshot>> GetCommitAsync(string repository, string sha, CancellationToken cancellationToken = default)
    {
        var result = await GetJsonAsync(Api(repository, $"commits/{Uri.EscapeDataString(sha)}"), cancellationToken);
        return Map(result, root => GitHubPayloadMapper.Commit(repository, root));
    }

    public async Task<ExternalResult<GitHubIssueSnapshot>> GetIssueAsync(string repository, int issueNumber, CancellationToken cancellationToken = default)
    {
        var result = await GetJsonAsync(Api(repository, $"issues/{issueNumber}"), cancellationToken);
        return Map(result, root => GitHubPayloadMapper.Issue(repository, root));
    }

    public async Task<ExternalResult<GitHubPullRequestSnapshot>> GetPullRequestAsync(string repository, int pullRequestNumber, CancellationToken cancellationToken = default)
    {
        var pr = await GetJsonAsync(Api(repository, $"pulls/{pullRequestNumber}"), cancellationToken);
        var prDocument = pr.Document;
        if (!pr.IsSuccess || prDocument is null)
            return new(pr.Status, null, pr.CapturedAt, false, pr.ErrorCode);

        using (prDocument)
        {
            var files = await GetJsonAsync(Api(repository, $"pulls/{pullRequestNumber}/files?per_page=100"), cancellationToken);
            var filesDocument = files.Document;
            if (!files.IsSuccess || filesDocument is null)
                return new(files.Status, null, files.CapturedAt, false, files.ErrorCode);

            using (filesDocument)
            {
                return new(
                    ExternalReadStatus.Success,
                    GitHubPayloadMapper.PullRequest(repository, prDocument.RootElement, GitHubPayloadMapper.ChangedFiles(filesDocument.RootElement)),
                    Max(pr.CapturedAt, files.CapturedAt));
            }
        }
    }

    public async Task<ExternalResult<IReadOnlyList<GitHubPullRequestSnapshot>>> ListPullRequestsAsync(string repository, string state = "open", CancellationToken cancellationToken = default)
    {
        var result = await GetJsonAsync(Api(repository, $"pulls?state={Uri.EscapeDataString(state)}&per_page=100"), cancellationToken);
        var document = result.Document;
        if (!result.IsSuccess || document is null)
            return new(result.Status, null, result.CapturedAt, false, result.ErrorCode);

        using (document)
        {
            var pulls = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().Select(item => GitHubPayloadMapper.PullRequest(repository, item)).ToArray()
                : Array.Empty<GitHubPullRequestSnapshot>();
            return new(ExternalReadStatus.Success, pulls, result.CapturedAt);
        }
    }

    public async Task<ExternalResult<GitHubCheckSummary>> GetChecksAsync(string repository, string commitSha, CancellationToken cancellationToken = default)
    {
        var statuses = await GetJsonAsync(Api(repository, $"commits/{Uri.EscapeDataString(commitSha)}/status"), cancellationToken);
        var statusDocument = statuses.Document;
        if (!statuses.IsSuccess || statusDocument is null)
            return new(statuses.Status, null, statuses.CapturedAt, false, statuses.ErrorCode);

        var checkRuns = await GetJsonAsync(Api(repository, $"commits/{Uri.EscapeDataString(commitSha)}/check-runs?per_page=100"), cancellationToken);
        var checkDocument = checkRuns.Document;
        using (statusDocument)
        {
            if (checkRuns.IsSuccess && checkDocument is not null)
            {
                using (checkDocument)
                {
                    return new(
                        ExternalReadStatus.Success,
                        GitHubPayloadMapper.Checks(repository, commitSha, statusDocument.RootElement, checkDocument.RootElement),
                        Max(statuses.CapturedAt, checkRuns.CapturedAt));
                }
            }

            return new(
                ExternalReadStatus.Success,
                GitHubPayloadMapper.Checks(repository, commitSha, statusDocument.RootElement, null),
                statuses.CapturedAt,
                false,
                checkRuns.ErrorCode);
        }
    }

    public async Task<ExternalResult<IReadOnlyList<GitHubWorkflowRunSnapshot>>> GetWorkflowRunsAsync(string repository, string commitSha, CancellationToken cancellationToken = default)
    {
        var result = await GetJsonAsync(Api(repository, $"actions/runs?head_sha={Uri.EscapeDataString(commitSha)}&per_page=20"), cancellationToken);
        var document = result.Document;
        if (!result.IsSuccess || document is null)
            return new(result.Status, null, result.CapturedAt, false, result.ErrorCode);

        using (document)
            return new(ExternalReadStatus.Success, GitHubPayloadMapper.WorkflowRuns(document.RootElement), result.CapturedAt);
    }

    public async Task<ExternalResult<GitHubReleaseSnapshot>> GetLatestReleaseAsync(string repository, CancellationToken cancellationToken = default)
    {
        var result = await GetJsonAsync(Api(repository, "releases/latest"), cancellationToken);
        return Map(result, GitHubPayloadMapper.Release);
    }

    public async Task<ExternalResult<IReadOnlyList<GitHubTagSnapshot>>> ListTagsAsync(string repository, CancellationToken cancellationToken = default)
    {
        var result = await GetJsonAsync(Api(repository, "tags?per_page=100"), cancellationToken);
        var document = result.Document;
        if (!result.IsSuccess || document is null)
            return new(result.Status, null, result.CapturedAt, false, result.ErrorCode);

        using (document)
            return new(ExternalReadStatus.Success, GitHubPayloadMapper.Tags(document.RootElement), result.CapturedAt);
    }

    private async Task<JsonReadResult> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync(url, cancellationToken);
            var capturedAt = DateTimeOffset.UtcNow;
            if (!response.IsSuccessStatusCode)
            {
                var remaining = response.Headers.TryGetValues("X-RateLimit-Remaining", out var values) ? values.FirstOrDefault() : null;
                return new(GitHubFailureClassifier.Classify(response.StatusCode, remaining), null, capturedAt, $"GITHUB_HTTP_{(int)response.StatusCode}");
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            return new(ExternalReadStatus.Success, JsonDocument.Parse(content), capturedAt, null);
        }
        catch (HttpRequestException ex)
        {
            return new(ExternalReadStatus.Offline, null, DateTimeOffset.UtcNow, ex.GetType().Name);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(ExternalReadStatus.TemporaryFailure, null, DateTimeOffset.UtcNow, "GITHUB_TIMEOUT");
        }
        catch (JsonException ex)
        {
            return new(ExternalReadStatus.TemporaryFailure, null, DateTimeOffset.UtcNow, ex.GetType().Name);
        }
    }

    private static ExternalResult<T> Map<T>(JsonReadResult result, Func<JsonElement, T> map) where T : class
    {
        var document = result.Document;
        if (!result.IsSuccess || document is null)
            return new(result.Status, null, result.CapturedAt, false, result.ErrorCode);

        using (document)
            return new(ExternalReadStatus.Success, map(document.RootElement), result.CapturedAt);
    }

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) => left >= right ? left : right;

    private static string Api(string repository, string path)
    {
        var trimmed = repository.Trim('/');
        return string.IsNullOrEmpty(path)
            ? $"https://api.github.com/repos/{trimmed}"
            : $"https://api.github.com/repos/{trimmed}/{path}";
    }

    private sealed record JsonReadResult(ExternalReadStatus Status, JsonDocument? Document, DateTimeOffset CapturedAt, string? ErrorCode)
    {
        public bool IsSuccess => Status == ExternalReadStatus.Success;
    }
}
