using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PCCExecutive.Application;

namespace PCCExecutive.Pcc;

public sealed record PccDocumentCapture(
    ExternalReadStatus Status,
    string? SourceSha,
    DateTimeOffset CapturedAt,
    bool IsStale,
    IReadOnlyDictionary<string, string> Documents,
    string? ErrorCode = null);

public interface IPccDocumentSource
{
    Task<PccDocumentCapture> CaptureAsync(CancellationToken cancellationToken = default);
}

public interface IPccDocumentCache
{
    Task<PccDocumentCapture?> GetAsync(CancellationToken cancellationToken = default);
    Task PutAsync(PccDocumentCapture capture, CancellationToken cancellationToken = default);
}

public sealed class GitHubPccDocumentSource : IPccDocumentSource
{
    public const string RoutingPath = "portfolio/project-routing.json";
    public const string ProjectsPath = "portfolio/projects.yml";
    public const string DesiredStatePath = "orchestration/desired-state.json";

    private static readonly string[] RequiredPaths = [RoutingPath, ProjectsPath, DesiredStatePath];
    private readonly HttpClient _httpClient;
    private readonly string _repository;
    private readonly string _branch;
    private readonly IPccDocumentCache? _cache;

    public GitHubPccDocumentSource(
        HttpClient httpClient,
        string repository = "walidatiyaai2025-gif/project-control-center",
        string branch = "main",
        string? token = null,
        IPccDocumentCache? cache = null)
    {
        _httpClient = httpClient;
        _repository = repository;
        _branch = branch;
        _cache = cache;

        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("PCCExecutive/0.1.0");
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        if (!string.IsNullOrWhiteSpace(token))
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public async Task<PccDocumentCapture> CaptureAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var branchResponse = await _httpClient.GetAsync(Api($"branches/{Uri.EscapeDataString(_branch)}"), cancellationToken);
            if (!branchResponse.IsSuccessStatusCode)
                return await FailureOrCacheAsync(Classify(branchResponse), $"PCC_BRANCH_{(int)branchResponse.StatusCode}", cancellationToken);

            using var branchJson = JsonDocument.Parse(await branchResponse.Content.ReadAsStringAsync(cancellationToken));
            var sourceSha = branchJson.RootElement.GetProperty("commit").GetProperty("sha").GetString();
            if (string.IsNullOrWhiteSpace(sourceSha))
                return await FailureOrCacheAsync(ExternalReadStatus.TemporaryFailure, "PCC_SOURCE_SHA_MISSING", cancellationToken);

            var documents = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var path in RequiredPaths)
            {
                using var response = await _httpClient.GetAsync(Api($"contents/{path}?ref={Uri.EscapeDataString(sourceSha)}"), cancellationToken);
                if (!response.IsSuccessStatusCode)
                    return await FailureOrCacheAsync(Classify(response), $"PCC_CONTENT_{path}_{(int)response.StatusCode}", cancellationToken);

                using var documentJson = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                var encoded = documentJson.RootElement.GetProperty("content").GetString()?.Replace("\n", "", StringComparison.Ordinal);
                var encoding = documentJson.RootElement.TryGetProperty("encoding", out var encodingElement) ? encodingElement.GetString() : null;
                if (!string.Equals(encoding, "base64", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(encoded))
                    return await FailureOrCacheAsync(ExternalReadStatus.TemporaryFailure, $"PCC_CONTENT_ENCODING_{path}", cancellationToken);

                documents[path] = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            }

            var capture = new PccDocumentCapture(ExternalReadStatus.Success, sourceSha, DateTimeOffset.UtcNow, false, documents);
            if (_cache is not null) await _cache.PutAsync(capture, cancellationToken);
            return capture;
        }
        catch (HttpRequestException ex)
        {
            return await FailureOrCacheAsync(ExternalReadStatus.Offline, ex.GetType().Name, cancellationToken);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return await FailureOrCacheAsync(ExternalReadStatus.TemporaryFailure, "PCC_TIMEOUT", cancellationToken);
        }
        catch (JsonException ex)
        {
            return await FailureOrCacheAsync(ExternalReadStatus.TemporaryFailure, ex.GetType().Name, cancellationToken);
        }
    }

    private async Task<PccDocumentCapture> FailureOrCacheAsync(ExternalReadStatus status, string errorCode, CancellationToken cancellationToken)
    {
        if (_cache is not null)
        {
            var cached = await _cache.GetAsync(cancellationToken);
            if (cached is not null)
                return cached with { Status = ExternalReadStatus.StaleCache, IsStale = true, ErrorCode = errorCode };
        }

        return new(status, null, DateTimeOffset.UtcNow, false, new Dictionary<string, string>(), errorCode);
    }

    private string Api(string path) => $"https://api.github.com/repos/{_repository}/{path}";

    public static ExternalReadStatus Classify(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.NotFound) return ExternalReadStatus.NotFound;
        if (response.StatusCode == HttpStatusCode.Unauthorized) return ExternalReadStatus.Unauthorized;
        if (response.StatusCode == HttpStatusCode.Forbidden &&
            response.Headers.TryGetValues("X-RateLimit-Remaining", out var values) &&
            values.Contains("0", StringComparer.Ordinal))
            return ExternalReadStatus.RateLimited;
        if (response.StatusCode == HttpStatusCode.Forbidden) return ExternalReadStatus.Unauthorized;
        if ((int)response.StatusCode >= 500) return ExternalReadStatus.TemporaryFailure;
        return ExternalReadStatus.TemporaryFailure;
    }
}

public sealed class PccProjectControlResolver : IProjectControlResolver
{
    private readonly IPccDocumentSource _source;

    public PccProjectControlResolver(IPccDocumentSource source) => _source = source;

    public async Task<ProjectResolution> ResolveProjectAsync(string nameOrAlias, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(nameOrAlias))
            return new(ProjectResolutionStatus.ProjectNotFound, null, "Project name or alias is required.");

        var capture = await _source.CaptureAsync(cancellationToken);
        if (!TryMapCaptureFailure(capture, out var failure))
            return failure!;

        using var routing = Parse(capture, GitHubPccDocumentSource.RoutingPath);
        using var projects = Parse(capture, GitHubPccDocumentSource.ProjectsPath);
        using var desired = Parse(capture, GitHubPccDocumentSource.DesiredStatePath);

        var normalized = Normalize(nameOrAlias);
        var projectCandidates = routing.RootElement.GetProperty("PROJECTS").EnumerateArray()
            .Where(project => MatchesProject(project, normalized))
            .ToArray();

        var variantCandidates = routing.RootElement.GetProperty("PROJECTS").EnumerateArray()
            .SelectMany(project => EnumerateVariants(project).Where(variant => MatchesVariant(variant, normalized)).Select(variant => (Project: project, Variant: variant)))
            .ToArray();

        if (variantCandidates.Length == 1)
            return BuildResolution(variantCandidates[0].Project, variantCandidates[0].Variant, projects, desired, capture, ProjectScopeKind.Variant);

        if (variantCandidates.Length > 1)
            return new(ProjectResolutionStatus.RoutingConflict, null, $"Alias '{nameOrAlias}' resolves to multiple variants.");

        if (projectCandidates.Length == 0)
            return new(ProjectResolutionStatus.ProjectNotFound, null, $"Project '{nameOrAlias}' was not found in PCC routing.");

        if (projectCandidates.Length > 1)
            return new(ProjectResolutionStatus.RoutingConflict, null, $"Alias '{nameOrAlias}' resolves to multiple projects.");

        var project = projectCandidates[0];
        var model = GetString(project, "PROJECT_MODEL");
        if (string.Equals(model, "PRODUCT_FAMILY", StringComparison.OrdinalIgnoreCase))
        {
            var defaultScope = GetString(project, "DEFAULT_SCOPE");
            if (string.IsNullOrWhiteSpace(defaultScope))
                return new(ProjectResolutionStatus.VariantRequired, null, $"Project family '{GetString(project, "PROJECT_ID")}' requires an explicit CORE or VARIANT route.");
        }

        return BuildResolution(project, null, projects, desired, capture, null);
    }

    public async Task<ProjectResolution> GetProjectAsync(string projectControlId, string? variantId = null, ProjectScopeKind? scope = null, CancellationToken cancellationToken = default)
    {
        var capture = await _source.CaptureAsync(cancellationToken);
        if (!TryMapCaptureFailure(capture, out var failure))
            return failure!;

        using var routing = Parse(capture, GitHubPccDocumentSource.RoutingPath);
        using var projects = Parse(capture, GitHubPccDocumentSource.ProjectsPath);
        using var desired = Parse(capture, GitHubPccDocumentSource.DesiredStatePath);

        var project = routing.RootElement.GetProperty("PROJECTS").EnumerateArray()
            .FirstOrDefault(item => string.Equals(GetString(item, "PROJECT_ID"), projectControlId, StringComparison.OrdinalIgnoreCase));

        if (project.ValueKind == JsonValueKind.Undefined)
            return new(ProjectResolutionStatus.ProjectNotFound, null, $"Project '{projectControlId}' was not found in PCC routing.");

        JsonElement? variant = null;
        if (!string.IsNullOrWhiteSpace(variantId))
        {
            var found = EnumerateVariants(project)
                .FirstOrDefault(item => string.Equals(GetString(item, "VARIANT_ID"), variantId, StringComparison.OrdinalIgnoreCase));
            if (found.ValueKind == JsonValueKind.Undefined)
                return new(ProjectResolutionStatus.ProjectNotFound, null, $"Variant '{variantId}' was not found for project '{projectControlId}'.");
            variant = found;
        }
        else if (string.Equals(GetString(project, "PROJECT_MODEL"), "PRODUCT_FAMILY", StringComparison.OrdinalIgnoreCase) &&
                 scope != ProjectScopeKind.Core &&
                 string.IsNullOrWhiteSpace(GetString(project, "DEFAULT_SCOPE")))
        {
            return new(ProjectResolutionStatus.VariantRequired, null, $"Project family '{projectControlId}' requires an explicit CORE or VARIANT route.");
        }

        return BuildResolution(project, variant, projects, desired, capture, scope);
    }

    public async Task<ExternalResult<ProjectRoutingSnapshot>> GetRoutingSnapshotAsync(string projectControlId, string? variantId = null, ProjectScopeKind? scope = null, CancellationToken cancellationToken = default)
    {
        var resolution = await GetProjectAsync(projectControlId, variantId, scope, cancellationToken);
        return resolution.Project is null
            ? new(MapStatus(resolution.Status), null, DateTimeOffset.UtcNow, resolution.Status == ProjectResolutionStatus.StaleCache, resolution.Message)
            : new(resolution.Status == ProjectResolutionStatus.StaleCache ? ExternalReadStatus.StaleCache : ExternalReadStatus.Success, resolution.Project, resolution.Project.Provenance.CapturedAt, resolution.Project.Provenance.Freshness == EvidenceFreshness.Stale);
    }

    public async Task<ExternalResult<IReadOnlyList<CanonicalTaskSnapshot>>> GetCanonicalTasksAsync(string projectControlId, CancellationToken cancellationToken = default)
    {
        var capture = await _source.CaptureAsync(cancellationToken);
        if (!TryMapCaptureFailure(capture, out var failure))
            return new(MapStatus(failure!.Status), null, capture.CapturedAt, capture.IsStale, failure.Message);

        using var projects = Parse(capture, GitHubPccDocumentSource.ProjectsPath);
        var project = FindProject(projects.RootElement, projectControlId);
        if (project.ValueKind == JsonValueKind.Undefined)
            return new(ExternalReadStatus.NotFound, null, capture.CapturedAt, capture.IsStale, "PCC_PROJECT_NOT_FOUND");

        var tasks = ParseTasks(project);
        return new(capture.IsStale ? ExternalReadStatus.StaleCache : ExternalReadStatus.Success, tasks, capture.CapturedAt, capture.IsStale);
    }

    public async Task<ExternalResult<DesiredStateSnapshot>> GetDesiredStateAsync(string projectControlId, CancellationToken cancellationToken = default)
    {
        var capture = await _source.CaptureAsync(cancellationToken);
        if (!TryMapCaptureFailure(capture, out var failure))
            return new(MapStatus(failure!.Status), null, capture.CapturedAt, capture.IsStale, failure.Message);

        using var desired = Parse(capture, GitHubPccDocumentSource.DesiredStatePath);
        var project = FindProject(desired.RootElement, projectControlId);
        if (project.ValueKind == JsonValueKind.Undefined)
            return new(ExternalReadStatus.NotFound, null, capture.CapturedAt, capture.IsStale, "PCC_DESIRED_STATE_NOT_FOUND");

        var snapshot = ParseDesired(project);
        return new(capture.IsStale ? ExternalReadStatus.StaleCache : ExternalReadStatus.Success, snapshot, capture.CapturedAt, capture.IsStale);
    }

    private static ProjectResolution BuildResolution(
        JsonElement project,
        JsonElement? variant,
        JsonDocument projects,
        JsonDocument desired,
        PccDocumentCapture capture,
        ProjectScopeKind? requestedScope)
    {
        var constitution = GetString(project, "CONSTITUTION_STATE") ?? "UNKNOWN";
        if (!string.Equals(constitution, "READY", StringComparison.OrdinalIgnoreCase))
            return new(ProjectResolutionStatus.RoutingNotReady, null, $"CONSTITUTION_STATE={constitution}");

        var projectId = GetString(project, "PROJECT_ID")!;
        var modelText = GetString(project, "PROJECT_MODEL") ?? "STANDALONE";
        var model = string.Equals(modelText, "PRODUCT_FAMILY", StringComparison.OrdinalIgnoreCase) ? ProjectModel.ProductFamily : ProjectModel.Standalone;

        if (requestedScope == ProjectScopeKind.Variant && variant is null)
            return new(ProjectResolutionStatus.VariantRequired, null, $"Project family '{projectId}' requires a concrete variant ID.");
        if (model == ProjectModel.ProductFamily && requestedScope == ProjectScopeKind.Project)
            return new(ProjectResolutionStatus.RoutingConflict, null, $"Project family '{projectId}' cannot be routed as repository-root PROJECT scope without PCC authority.");

        ProjectScopeKind scope;
        string routingState;
        string? implementationLocation = null;
        string? variantId = null;
        string? variantDisplay = null;

        if (variant is not null)
        {
            variantId = GetString(variant.Value, "VARIANT_ID");
            variantDisplay = GetString(variant.Value, "DISPLAY_NAME");
            implementationLocation = GetString(variant.Value, "IMPLEMENTATION_LOCATION");
            routingState = GetString(variant.Value, "ROUTING_STATE") ?? "UNKNOWN";
            scope = ProjectScopeKind.Variant;

            if (!string.Equals(routingState, "READY", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(GetString(variant.Value, "IMPLEMENTATION_LOCATION_STATE"), "MAPPED", StringComparison.OrdinalIgnoreCase))
                return new(ProjectResolutionStatus.RoutingNotReady, null, $"VARIANT_ROUTING_STATE={routingState}");
        }
        else
        {
            var defaultScope = GetString(project, "DEFAULT_SCOPE");
            scope = requestedScope == ProjectScopeKind.Core || string.Equals(defaultScope, "CORE", StringComparison.OrdinalIgnoreCase)
                ? ProjectScopeKind.Core
                : ProjectScopeKind.Project;
            routingState = scope == ProjectScopeKind.Core ? GetString(project, "CORE_ROUTING_STATE") ?? "UNKNOWN" : "READY";
            implementationLocation = scope == ProjectScopeKind.Project ? "." : null;

            if (scope == ProjectScopeKind.Core && !string.Equals(routingState, "READY", StringComparison.OrdinalIgnoreCase))
                return new(ProjectResolutionStatus.RoutingNotReady, null, $"CORE_ROUTING_STATE={routingState}");
        }

        var projectsProject = FindProject(projects.RootElement, projectId);
        var desiredProject = FindProject(desired.RootElement, projectId);
        var tasks = projectsProject.ValueKind == JsonValueKind.Undefined ? Array.Empty<CanonicalTaskSnapshot>() : ParseTasks(projectsProject);
        var desiredSnapshot = desiredProject.ValueKind == JsonValueKind.Undefined ? null : ParseDesired(desiredProject);

        var aliases = ReadStrings(project, "ALIASES");
        var provenance = new ProjectControlProvenance(
            "walidatiyaai2025-gif/project-control-center",
            capture.SourceSha ?? "UNKNOWN",
            GetString(projects.RootElement, "CONTROL_PLANE_VERSION") ?? GetString(project, "CONTROL_PLANE_VERSION"),
            ReadRootString(capture, GitHubPccDocumentSource.RoutingPath, "ROUTING_CONTRACT_VERSION"),
            capture.CapturedAt,
            capture.IsStale ? EvidenceFreshness.Stale : EvidenceFreshness.Current);

        var snapshot = new ProjectRoutingSnapshot(
            projectId,
            GetString(project, "DISPLAY_NAME") ?? projectId,
            GetString(project, "REPOSITORY") ?? "",
            model,
            scope,
            variantId,
            variantDisplay,
            implementationLocation,
            constitution,
            routingState,
            aliases,
            tasks,
            desiredSnapshot,
            provenance);

        var status = capture.IsStale ? ProjectResolutionStatus.StaleCache : ProjectResolutionStatus.Success;
        return new(status, snapshot, capture.IsStale ? "PCC live refresh unavailable; using explicitly stale cached routing." : null);
    }

    private static bool TryMapCaptureFailure(PccDocumentCapture capture, out ProjectResolution? failure)
    {
        failure = capture.Status switch
        {
            ExternalReadStatus.Success or ExternalReadStatus.StaleCache => null,
            ExternalReadStatus.NotFound => new(ProjectResolutionStatus.ProjectNotFound, null, capture.ErrorCode),
            ExternalReadStatus.Unauthorized => new(ProjectResolutionStatus.Unauthorized, null, capture.ErrorCode),
            ExternalReadStatus.RateLimited => new(ProjectResolutionStatus.RateLimited, null, capture.ErrorCode),
            ExternalReadStatus.Offline => new(ProjectResolutionStatus.Offline, null, capture.ErrorCode),
            ExternalReadStatus.RoutingConflict => new(ProjectResolutionStatus.RoutingConflict, null, capture.ErrorCode),
            _ => new(ProjectResolutionStatus.TemporaryFailure, null, capture.ErrorCode)
        };
        return failure is null;
    }

    private static ExternalReadStatus MapStatus(ProjectResolutionStatus status) => status switch
    {
        ProjectResolutionStatus.ProjectNotFound => ExternalReadStatus.NotFound,
        ProjectResolutionStatus.Unauthorized => ExternalReadStatus.Unauthorized,
        ProjectResolutionStatus.RateLimited => ExternalReadStatus.RateLimited,
        ProjectResolutionStatus.Offline => ExternalReadStatus.Offline,
        ProjectResolutionStatus.StaleCache => ExternalReadStatus.StaleCache,
        ProjectResolutionStatus.RoutingConflict or ProjectResolutionStatus.RoutingNotReady or ProjectResolutionStatus.VariantRequired => ExternalReadStatus.RoutingConflict,
        _ => ExternalReadStatus.TemporaryFailure
    };

    private static string? ReadRootString(PccDocumentCapture capture, string path, string property)
    {
        using var document = Parse(capture, path);
        return GetString(document.RootElement, property);
    }

    private static JsonDocument Parse(PccDocumentCapture capture, string path)
    {
        if (!capture.Documents.TryGetValue(path, out var json))
            throw new InvalidOperationException($"PCC capture does not contain required document '{path}'.");
        return JsonDocument.Parse(json);
    }

    private static JsonElement FindProject(JsonElement root, string projectControlId) =>
        root.GetProperty("PROJECTS").EnumerateArray()
            .FirstOrDefault(item => string.Equals(GetString(item, "PROJECT_ID"), projectControlId, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<JsonElement> EnumerateVariants(JsonElement project) =>
        project.TryGetProperty("VARIANTS", out var variants) && variants.ValueKind == JsonValueKind.Array
            ? variants.EnumerateArray().ToArray()
            : Array.Empty<JsonElement>();

    private static bool MatchesProject(JsonElement project, string normalized)
    {
        if (Normalize(GetString(project, "PROJECT_ID")) == normalized) return true;
        if (Normalize(GetString(project, "DISPLAY_NAME")) == normalized) return true;
        return ReadStrings(project, "ALIASES").Any(alias => Normalize(alias) == normalized);
    }

    private static bool MatchesVariant(JsonElement variant, string normalized)
    {
        if (Normalize(GetString(variant, "VARIANT_ID")) == normalized) return true;
        if (Normalize(GetString(variant, "DISPLAY_NAME")) == normalized) return true;
        return ReadStrings(variant, "ALIASES").Any(alias => Normalize(alias) == normalized);
    }

    private static IReadOnlyList<CanonicalTaskSnapshot> ParseTasks(JsonElement project)
    {
        if (!project.TryGetProperty("TASKS", out var tasks) || tasks.ValueKind != JsonValueKind.Array)
            return Array.Empty<CanonicalTaskSnapshot>();

        return tasks.EnumerateArray().Select(task => new CanonicalTaskSnapshot(
            GetString(task, "TASK_ID") ?? "",
            GetString(task, "PROJECT_ID") ?? "",
            GetString(task, "REQUIREMENT_ID"),
            GetString(task, "TITLE") ?? "",
            GetString(task, "STATE") ?? "UNKNOWN",
            GetString(task, "PRIORITY"),
            GetString(task, "CANONICAL_BRANCH"),
            GetString(task, "BASE_BRANCH"),
            GetString(task, "BASE_SHA"),
            GetString(task, "LATEST_PUSHED_SHA"),
            GetString(task, "TARGET_VERSION"),
            ReadStrings(task, "SCOPE"),
            ReadStrings(task, "NON_SCOPE"),
            ReadStrings(task, "ACCEPTANCE_CRITERIA"),
            ReadStrings(task, "DEPENDENCIES"),
            ReadStrings(task, "EVIDENCE"))).ToArray();
    }

    private static DesiredStateSnapshot ParseDesired(JsonElement project) => new(
        GetString(project, "PROJECT_ID") ?? "",
        GetString(project, "REPOSITORY") ?? "",
        GetString(project, "CONTROL_PLANE_VERSION"),
        GetString(project, "DESIRED_POLICY_VERSION"),
        GetString(project, "POLICY_ENFORCEMENT_MODE"),
        GetBool(project, "WRITE_AUTHORIZED"),
        GetString(project, "CANONICAL_DEVELOPMENT_LINEAGE"),
        GetString(project, "VERSION_POLICY"),
        GetString(project, "VERSION_SOURCE"),
        GetString(project, "MONITOR_TARGET_SCOPE"),
        GetString(project, "MONITOR_TARGET_VARIANT"));

    private static string? GetString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static bool? GetBool(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static IReadOnlyList<string> ReadStrings(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var values) || values.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        return values.EnumerateArray().Select(value => value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToArray();
    }

    private static string Normalize(string? value) =>
        string.Join(' ', (value ?? "").Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
