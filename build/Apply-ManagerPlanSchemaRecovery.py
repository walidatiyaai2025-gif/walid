from pathlib import Path


def replace_once(text: str, old: str, new: str, label: str) -> str:
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected exactly one match, found {count}")
    return text.replace(old, new, 1)

root = Path.cwd()
manager_path = root / "src/PCCExecutive.Application/ManagerOrchestration.cs"
gateway_path = root / "src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs"
tests_path = root / "tests/PCCExecutive.Application.Tests/ManagerOrchestrationTests.cs"

manager = manager_path.read_text(encoding="utf-8")

manager = replace_once(manager,
'''public sealed record ManagerTaskProposal(
    WorkerTask Task,
    IReadOnlyList<string> EvidenceExpected,
    int Priority,
    WorkerSlotId? SuggestedWorkerSlot,
    string Reason,
    IReadOnlyList<string> KnownBlockers,
    IReadOnlySet<TaskId> RequiredPreviousTasks,
    ManagerExecutionMode RecommendedExecutionMode,
    ProjectScopeKind TargetScope,
    string? TargetVariant,
    string? ExpectedHead,
    int? RelatedPullRequest,
    string? ExpectedPullRequestState,
    string? TargetBranch,
    bool FeatureExpansion);

public sealed record StructuredManagerPlan(
    ManagerEstimate ManagerEstimate,
    IReadOnlyList<ManagerTaskProposal> Tasks,
    string? ExpectedHead,
    string? ExpectedRoutingIdentity,
    string? ProjectDecision,
    IReadOnlyList<string> KnownBlockers);''',
'''public sealed record ManagerTaskProposal(
    WorkerTask Task,
    IReadOnlyList<string> EvidenceExpected,
    int Priority,
    WorkerSlotId? SuggestedWorkerSlot,
    string Reason,
    IReadOnlyList<string> KnownBlockers,
    IReadOnlySet<TaskId> RequiredPreviousTasks,
    ManagerExecutionMode RecommendedExecutionMode,
    ProjectScopeKind TargetScope,
    string? TargetVariant,
    string? ExpectedHead,
    int? RelatedPullRequest,
    string? ExpectedPullRequestState,
    string? TargetBranch,
    bool FeatureExpansion)
{
    public IReadOnlyList<int> RelatedPullRequests { get; init; } = [];
}

public sealed record ManagerRoutingExpectation(
    string? ProjectId,
    string? DisplayName,
    string? PccSourceSha,
    string? Repository,
    string? CanonicalTask,
    string? TargetScope,
    string? TargetVariant,
    string? ImplementationRoot,
    string? DefaultBranch,
    string? DefaultHead,
    string? ConvergenceBranch,
    string? ConvergenceHead);

public sealed record StructuredManagerPlan(
    ManagerEstimate ManagerEstimate,
    IReadOnlyList<ManagerTaskProposal> Tasks,
    string? ExpectedHead,
    string? ExpectedRoutingIdentity,
    string? ProjectDecision,
    IReadOnlyList<string> KnownBlockers)
{
    public ManagerRoutingExpectation? ExpectedRouting { get; init; }
}''',
"record extensions")

manager = replace_once(manager,
'''        var findings = new List<ManagerPlanFinding>();
        if (wire.Tasks.Count > WorkerSlotPolicy.MaximumActiveWorkers)''',
'''        var findings = new List<ManagerPlanFinding>();
        var (expectedRoutingIdentity, expectedRouting) = ParseRoutingExpectation(wire.ExpectedRoutingIdentity, findings);
        if (wire.Tasks.Count > WorkerSlotPolicy.MaximumActiveWorkers)''',
"routing parse setup")

manager = replace_once(manager,
'''            var dependencies = ParseIds(item.Dependencies, id, "DEPENDENCY_ID_INVALID", findings);
            var requiredPrevious = ParseIds(item.RequiredPreviousTasks, id, "PREVIOUS_TASK_ID_INVALID", findings);''',
'''            var dependencies = ParseOptionalTaskDependencies(item.Dependencies);
            var requiredPrevious = ParseIds(item.RequiredPreviousTasks, id, "PREVIOUS_TASK_ID_INVALID", findings);''',
"external dependency tolerance")

manager = replace_once(manager,
'''            if (!Enum.TryParse<ManagerExecutionMode>(item.RecommendedExecutionMode ?? "AutomaticStaged", true, out var executionMode))
            {
                executionMode = ManagerExecutionMode.AutomaticStaged;
                findings.Add(new("EXECUTION_MODE_INVALID", "RecommendedExecutionMode is invalid.", PlanFindingSeverity.Block, id));
            }

            tasks.Add(new(
                workerTask,
                item.EvidenceExpected ?? [],
                item.Priority,
                slot,
                item.Reason ?? string.Empty,
                item.KnownBlockers ?? [],
                requiredPrevious,
                executionMode,
                targetScope,
                item.TargetVariant,
                item.ExpectedHead,
                item.RelatedPullRequest,
                item.ExpectedPullRequestState,
                item.TargetBranch,
                item.FeatureExpansion));''',
'''            var executionMode = ParseExecutionMode(item.RecommendedExecutionMode, id, findings);
            var priority = ParsePriority(item.Priority, id, findings);
            var relatedPullRequests = ParseRelatedPullRequests(item.RelatedPullRequest, id, findings);
            var relatedPullRequest = relatedPullRequests.Count == 0 ? (int?)null : relatedPullRequests[0];

            var proposal = new ManagerTaskProposal(
                workerTask,
                item.EvidenceExpected ?? [],
                priority,
                slot,
                item.Reason ?? string.Empty,
                item.KnownBlockers ?? [],
                requiredPrevious,
                executionMode,
                targetScope,
                item.TargetVariant,
                item.ExpectedHead,
                relatedPullRequest,
                item.ExpectedPullRequestState,
                item.TargetBranch,
                item.FeatureExpansion)
            {
                RelatedPullRequests = relatedPullRequests
            };
            tasks.Add(proposal);''',
"wire task normalization")

manager = replace_once(manager,
'''        var plan = new StructuredManagerPlan(
            new ManagerEstimate(Math.Clamp(wire.ManagerEstimate, 0m, 100m)),
            tasks,
            wire.ExpectedHead,
            wire.ExpectedRoutingIdentity,
            wire.ProjectDecision,
            wire.KnownBlockers ?? []);

        return new(findings.All(x => x.Severity != PlanFindingSeverity.Block), plan, findings);
    }

    private static IReadOnlySet<TaskId> ParseIds(''',
'''        var plan = new StructuredManagerPlan(
            new ManagerEstimate(Math.Clamp(wire.ManagerEstimate, 0m, 100m)),
            tasks,
            wire.ExpectedHead,
            expectedRoutingIdentity,
            wire.ProjectDecision,
            wire.KnownBlockers ?? [])
        {
            ExpectedRouting = expectedRouting
        };

        return new(findings.All(x => x.Severity != PlanFindingSeverity.Block), plan, findings);
    }

    private static (string? LegacyIdentity, ManagerRoutingExpectation? Structured) ParseRoutingExpectation(
        JsonElement value,
        List<ManagerPlanFinding> findings)
    {
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return (null, null);
        if (value.ValueKind == JsonValueKind.String)
            return (value.GetString(), null);
        if (value.ValueKind != JsonValueKind.Object)
        {
            findings.Add(new("ROUTING_EXPECTATION_INVALID", "ExpectedRoutingIdentity must be a string or structured routing object.", PlanFindingSeverity.Block));
            return (null, null);
        }

        var structured = new ManagerRoutingExpectation(
            ReadString(value, "ProjectId"),
            ReadString(value, "DisplayName"),
            ReadString(value, "PccSourceSha"),
            ReadString(value, "Repository"),
            ReadString(value, "CanonicalTask"),
            ReadString(value, "TargetScope"),
            ReadString(value, "TargetVariant"),
            ReadString(value, "ImplementationRoot"),
            ReadString(value, "DefaultBranch"),
            ReadString(value, "DefaultHead"),
            ReadString(value, "ConvergenceBranch"),
            ReadString(value, "ConvergenceHead"));
        if (new[] { structured.ProjectId, structured.Repository, structured.TargetScope, structured.TargetVariant, structured.PccSourceSha }
            .All(string.IsNullOrWhiteSpace))
            findings.Add(new("ROUTING_EXPECTATION_INVALID", "Structured ExpectedRoutingIdentity contains no verifiable routing fields.", PlanFindingSeverity.Block));
        return (null, structured);
    }

    private static string? ReadString(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : property.Value.ToString();
        return null;
    }

    private static int ParsePriority(JsonElement value, TaskId taskId, List<ManagerPlanFinding> findings)
    {
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var numeric)) return numeric;
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()?.Trim();
            if (int.TryParse(text, out numeric)) return numeric;
            if (!string.IsNullOrWhiteSpace(text) && text.Length > 1 && (text[0] == 'P' || text[0] == 'p') && int.TryParse(text[1..], out numeric)) return numeric;
        }
        findings.Add(new("PRIORITY_INVALID", "Priority must be an integer or P0/P1/... value.", PlanFindingSeverity.Block, taskId));
        return int.MaxValue;
    }

    private static IReadOnlyList<int> ParseRelatedPullRequests(JsonElement value, TaskId taskId, List<ManagerPlanFinding> findings)
    {
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return [];
        var result = new List<int>();
        IEnumerable<JsonElement> values = value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToArray() : [value];
        foreach (var item in values)
        {
            int number;
            if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out number) && number > 0)
            {
                result.Add(number);
                continue;
            }
            if (item.ValueKind == JsonValueKind.String)
            {
                var text = item.GetString()?.Trim().TrimStart('#');
                if (int.TryParse(text, out number) && number > 0)
                {
                    result.Add(number);
                    continue;
                }
            }
            findings.Add(new("RELATED_PR_INVALID", "RelatedPullRequest must be a PR number or array of PR numbers.", PlanFindingSeverity.Block, taskId));
        }
        return result.Distinct().ToArray();
    }

    private static ManagerExecutionMode ParseExecutionMode(string? value, TaskId taskId, List<ManagerPlanFinding> findings)
    {
        if (string.IsNullOrWhiteSpace(value)) return ManagerExecutionMode.AutomaticStaged;
        if (Enum.TryParse<ManagerExecutionMode>(value, true, out var direct)) return direct;
        var normalized = value.Replace('-', '_').Replace(' ', '_').ToUpperInvariant();
        if (normalized.Contains("SEQUENTIAL", StringComparison.Ordinal)) return ManagerExecutionMode.Sequential;
        if (normalized.Contains("MANUAL", StringComparison.Ordinal)) return ManagerExecutionMode.Manual;
        if (normalized.Contains("AUTONOMOUS", StringComparison.Ordinal) ||
            normalized.Contains("AUTOMATIC", StringComparison.Ordinal) ||
            normalized.Contains("INTEGRATION", StringComparison.Ordinal) ||
            normalized.Contains("CONVERGENCE", StringComparison.Ordinal) ||
            normalized.Contains("RECONCILIATION", StringComparison.Ordinal) ||
            normalized.Contains("READ_ONLY", StringComparison.Ordinal))
            return ManagerExecutionMode.AutomaticStaged;
        findings.Add(new("EXECUTION_MODE_INVALID", $"RecommendedExecutionMode '{value}' is invalid.", PlanFindingSeverity.Block, taskId));
        return ManagerExecutionMode.AutomaticStaged;
    }

    private static IReadOnlySet<TaskId> ParseOptionalTaskDependencies(IReadOnlyList<string>? values)
    {
        var result = new HashSet<TaskId>();
        foreach (var value in values ?? [])
            if (TryTaskId(value, out var id)) result.Add(id);
        return result;
    }

    private static IReadOnlySet<TaskId> ParseIds(''',
"parser helpers")

manager = replace_once(manager,
'''        public string? ExpectedHead { get; set; }
        public string? ExpectedRoutingIdentity { get; set; }
        public string? ProjectDecision { get; set; }''',
'''        public string? ExpectedHead { get; set; }
        public JsonElement ExpectedRoutingIdentity { get; set; }
        public string? ProjectDecision { get; set; }''',
"wire routing json element")

manager = replace_once(manager,
'''        public List<string>? EvidenceExpected { get; set; }
        public int Priority { get; set; }
        public int? SuggestedWorkerSlot { get; set; }''',
'''        public List<string>? EvidenceExpected { get; set; }
        public JsonElement Priority { get; set; }
        public int? SuggestedWorkerSlot { get; set; }''',
"wire priority json element")

manager = replace_once(manager,
'''        public string? TargetVariant { get; set; }
        public string? ExpectedHead { get; set; }
        public int? RelatedPullRequest { get; set; }
        public string? ExpectedPullRequestState { get; set; }''',
'''        public string? TargetVariant { get; set; }
        public string? ExpectedHead { get; set; }
        public JsonElement RelatedPullRequest { get; set; }
        public string? ExpectedPullRequestState { get; set; }''',
"wire related PR json element")

manager = replace_once(manager,
'''        if (!string.IsNullOrWhiteSpace(plan.ExpectedHead) &&
            !string.Equals(plan.ExpectedHead, baseline.DefaultHeadSha, StringComparison.OrdinalIgnoreCase))
            findings.Add(new("STALE_HEAD", $"Manager expected HEAD {plan.ExpectedHead} but live HEAD is {baseline.DefaultHeadSha}.", PlanFindingSeverity.Block));

        if (!string.IsNullOrWhiteSpace(plan.ExpectedRoutingIdentity) &&
            !string.Equals(plan.ExpectedRoutingIdentity, routing.RoutingIdentity, StringComparison.Ordinal))
            findings.Add(new("ROUTING_CHANGED", "Manager plan was built against a different PCC routing identity.", PlanFindingSeverity.Block));''',
'''        if (!string.IsNullOrWhiteSpace(plan.ExpectedHead) &&
            !string.Equals(plan.ExpectedHead, baseline.DefaultHeadSha, StringComparison.OrdinalIgnoreCase) &&
            !baseline.RelevantPullRequests.Any(pr => string.Equals(pr.HeadSha, plan.ExpectedHead, StringComparison.OrdinalIgnoreCase)))
            findings.Add(new("STALE_HEAD", $"Manager expected HEAD {plan.ExpectedHead} but it is not the live default head or a live relevant PR head.", PlanFindingSeverity.Block));

        if (plan.ExpectedRouting is not null)
            ValidateStructuredRoutingExpectation(plan.ExpectedRouting, routing, baseline, findings);
        else if (!string.IsNullOrWhiteSpace(plan.ExpectedRoutingIdentity) &&
                 !string.Equals(plan.ExpectedRoutingIdentity, routing.RoutingIdentity, StringComparison.Ordinal))
            findings.Add(new("ROUTING_CHANGED", "Manager plan was built against a different PCC routing identity.", PlanFindingSeverity.Block));''',
"top-level live evidence validation")

start = manager.index("    private static void ValidatePullRequestAssumption(")
end = manager.index("\n}\n\npublic sealed record RuntimeHealthSnapshot", start)
manager = manager[:start] + '''    private static void ValidateStructuredRoutingExpectation(
        ManagerRoutingExpectation expected,
        ProjectRoutingSnapshot routing,
        ProjectBaselineSnapshot baseline,
        List<ManagerPlanFinding> findings)
    {
        void Mismatch(string field, string? wanted, string? actual)
        {
            if (!string.IsNullOrWhiteSpace(wanted) && !string.Equals(wanted, actual, StringComparison.OrdinalIgnoreCase))
                findings.Add(new("ROUTING_CHANGED", $"ExpectedRoutingIdentity.{field}='{wanted}' conflicts with live '{actual}'.", PlanFindingSeverity.Block));
        }

        Mismatch("ProjectId", expected.ProjectId, routing.ProjectControlId);
        Mismatch("DisplayName", expected.DisplayName, routing.DisplayName);
        Mismatch("PccSourceSha", expected.PccSourceSha, routing.Provenance.SourceSha);
        Mismatch("Repository", expected.Repository, routing.Repository);
        Mismatch("TargetVariant", expected.TargetVariant, routing.VariantId);
        Mismatch("ImplementationRoot", expected.ImplementationRoot?.TrimEnd('/'), routing.ImplementationLocation?.TrimEnd('/'));
        Mismatch("DefaultBranch", expected.DefaultBranch, baseline.DefaultBranch);
        Mismatch("DefaultHead", expected.DefaultHead, baseline.DefaultHeadSha);

        if (!string.IsNullOrWhiteSpace(expected.TargetScope))
        {
            if (!Enum.TryParse<ProjectScopeKind>(expected.TargetScope, true, out var scope) || scope != routing.Scope)
                findings.Add(new("ROUTING_CHANGED", $"ExpectedRoutingIdentity.TargetScope='{expected.TargetScope}' conflicts with live '{routing.Scope}'.", PlanFindingSeverity.Block));
        }
        if (!string.IsNullOrWhiteSpace(expected.CanonicalTask) &&
            !baseline.CanonicalTasks.Any(x => string.Equals(x.TaskId, expected.CanonicalTask, StringComparison.OrdinalIgnoreCase)))
            findings.Add(new("ROUTING_CHANGED", $"ExpectedRoutingIdentity.CanonicalTask='{expected.CanonicalTask}' is not present in live canonical tasks.", PlanFindingSeverity.Block));
        if (!string.IsNullOrWhiteSpace(expected.ConvergenceBranch) &&
            !baseline.RelevantPullRequests.Any(x => string.Equals(x.HeadBranch, expected.ConvergenceBranch, StringComparison.OrdinalIgnoreCase)))
            findings.Add(new("ROUTING_CHANGED", $"ExpectedRoutingIdentity.ConvergenceBranch='{expected.ConvergenceBranch}' is not a live relevant PR branch.", PlanFindingSeverity.Block));
        if (!string.IsNullOrWhiteSpace(expected.ConvergenceHead) &&
            !string.Equals(expected.ConvergenceHead, baseline.DefaultHeadSha, StringComparison.OrdinalIgnoreCase) &&
            !baseline.RelevantPullRequests.Any(x => string.Equals(x.HeadSha, expected.ConvergenceHead, StringComparison.OrdinalIgnoreCase)))
            findings.Add(new("ROUTING_CHANGED", $"ExpectedRoutingIdentity.ConvergenceHead='{expected.ConvergenceHead}' is not live evidence.", PlanFindingSeverity.Block));
    }

    private static void ValidatePullRequestAssumption(
        ManagerTaskProposal proposal,
        ProjectBaselineSnapshot baseline,
        List<ManagerPlanFinding> findings)
    {
        var numbers = proposal.RelatedPullRequests.Count > 0
            ? proposal.RelatedPullRequests
            : proposal.RelatedPullRequest is not null ? [proposal.RelatedPullRequest.Value] : [];
        foreach (var number in numbers)
        {
            var pr = baseline.RelevantPullRequests.FirstOrDefault(x => x.Number == number);
            if (pr is null)
            {
                findings.Add(new("PR_ASSUMPTION_NOT_FOUND", $"Referenced PR #{number} is not present in live relevant evidence.", PlanFindingSeverity.Block, proposal.Task.Id));
                continue;
            }
            ValidateExpectedPullRequestState(proposal, pr, findings);
        }
    }

    private static void ValidateExpectedPullRequestState(
        ManagerTaskProposal proposal,
        GitHubPullRequestSnapshot pr,
        List<ManagerPlanFinding> findings)
    {
        if (string.IsNullOrWhiteSpace(proposal.ExpectedPullRequestState)) return;
        var expected = proposal.ExpectedPullRequestState.Trim().Replace('-', '_').Replace(' ', '_').ToUpperInvariant();
        var expectsOpen = expected == "OPEN" || expected.StartsWith("OPEN_", StringComparison.Ordinal);
        var expectsClosed = expected == "CLOSED" || expected.StartsWith("CLOSED_", StringComparison.Ordinal);
        var expectsUnmerged = expected.Contains("UNMERGED", StringComparison.Ordinal) || expected.Contains("NO_MERGE", StringComparison.Ordinal);
        var expectsMerged = !expectsUnmerged && (expected == "MERGED" || expected.StartsWith("MERGED_", StringComparison.Ordinal));

        if (expectsOpen && !string.Equals(pr.State, "open", StringComparison.OrdinalIgnoreCase) ||
            expectsClosed && !string.Equals(pr.State, "closed", StringComparison.OrdinalIgnoreCase) ||
            expectsUnmerged && pr.Merged ||
            expectsMerged && !pr.Merged)
            findings.Add(new(
                pr.Merged ? "PR_ALREADY_MERGED" : "PR_STATE_CHANGED",
                $"Manager expected PR #{pr.Number} semantic state {proposal.ExpectedPullRequestState}; live state is {pr.State}, merged={pr.Merged}.",
                PlanFindingSeverity.Block,
                proposal.Task.Id));
    }

    private static void ValidateBranchAssumption(
        ManagerTaskProposal proposal,
        ProjectBaselineSnapshot baseline,
        List<ManagerPlanFinding> findings)
    {
        if (string.IsNullOrWhiteSpace(proposal.TargetBranch)) return;
        if (!IsValidGitBranchName(proposal.TargetBranch))
        {
            findings.Add(new("TASK_BRANCH_INVALID", $"Target branch '{proposal.TargetBranch}' is not a valid Git branch name.", PlanFindingSeverity.Block, proposal.Task.Id));
            return;
        }
        var known = baseline.CanonicalTasks
            .Select(x => x.CanonicalBranch)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Concat(baseline.RelevantPullRequests.Select(x => x.HeadBranch))
            .Any(x => string.Equals(x, proposal.TargetBranch, StringComparison.OrdinalIgnoreCase));
        if (!known)
            findings.Add(new("TASK_BRANCH_NEW", $"Target branch '{proposal.TargetBranch}' is a new proposed branch and will require normal creation controls.", PlanFindingSeverity.Info, proposal.Task.Id));
    }

    private static bool IsValidGitBranchName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith('/') || value.EndsWith('/') || value.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)) return false;
        if (value.Contains("..", StringComparison.Ordinal) || value.Any(char.IsWhiteSpace)) return false;
        return value.IndexOfAny(['\\\\', '~', '^', ':', '?', '*', '[']) < 0;
    }
''' + manager[end:]

manager_path.write_text(manager, encoding="utf-8")

gateway = gateway_path.read_text(encoding="utf-8")
old_prompt = '''    private string BuildManagerPrompt(ProjectRun run, ProjectBaselineSnapshot baseline) =>
        $"PROJECT_ID: {_projectControlId}\\nDISPLAY_NAME: {_projectDisplay}\\nREPOSITORY: {_projectRepository}\\nPROJECT_RUN: {run.Id}\\nPCC_SOURCE_SHA: {baseline.PccSourceSha}\\nDEFAULT_BRANCH: {baseline.DefaultBranch}\\nDEFAULT_HEAD: {baseline.DefaultHeadSha}\\nVERIFIED_COMPLETION: {run.VerifiedCompletion.Percent}\\nMANAGER_ESTIMATE: {run.ManagerEstimate.Percent}\\nACTIVE_WORKERS: 0/5\\nAUTOPILOT: {_autopilot}\\n\\nReturn one JSON object only with ManagerEstimate, ExpectedHead, ExpectedRoutingIdentity, ProjectDecision, KnownBlockers, and Tasks (0..5). Each task requires TaskId GUID, Objective, Repository, Paths, Components, ExclusiveResources, Dependencies, AcceptanceCriteria, EvidenceExpected, Priority, SuggestedWorkerSlot (1..5), Reason, KnownBlockers, RequiredPreviousTasks, RecommendedExecutionMode, TargetScope, TargetVariant, ExpectedHead, RelatedPullRequest, ExpectedPullRequestState, TargetBranch, FeatureExpansion.";'''
new_prompt = '''    private string BuildManagerPrompt(ProjectRun run, ProjectBaselineSnapshot baseline) =>
        $"PROJECT_ID: {_projectControlId}\\nDISPLAY_NAME: {_projectDisplay}\\nREPOSITORY: {_projectRepository}\\nPROJECT_RUN: {run.Id}\\nPCC_SOURCE_SHA: {baseline.PccSourceSha}\\nROUTING_IDENTITY: {baseline.RoutingIdentity}\\nDEFAULT_BRANCH: {baseline.DefaultBranch}\\nDEFAULT_HEAD: {baseline.DefaultHeadSha}\\nVERIFIED_COMPLETION: {run.VerifiedCompletion.Percent}\\nMANAGER_ESTIMATE: {run.ManagerEstimate.Percent}\\nACTIVE_WORKERS: 0/5\\nAUTOPILOT: {_autopilot}\\n\\nReturn one JSON object only; do not use markdown fences. Required top-level fields: ManagerEstimate, ExpectedHead, ExpectedRoutingIdentity, ProjectDecision, KnownBlockers, Tasks (0..5). Prefer ExpectedRoutingIdentity as the exact ROUTING_IDENTITY string above; a structured object is accepted only when its supplied fields match fresh live routing. Each task requires TaskId GUID, Objective, Repository, Paths, Components, ExclusiveResources, Dependencies, AcceptanceCriteria, EvidenceExpected, Priority, SuggestedWorkerSlot (1..5), Reason, KnownBlockers, RequiredPreviousTasks, RecommendedExecutionMode, TargetScope, TargetVariant, ExpectedHead, RelatedPullRequest, ExpectedPullRequestState, TargetBranch, FeatureExpansion. Priority may be integer or P0/P1/... with P0 highest. Dependencies and RequiredPreviousTasks should contain intra-wave TaskId GUIDs only; external PR/head prerequisites belong in KnownBlockers. RelatedPullRequest may be one integer or an integer array. Prefer RecommendedExecutionMode values AutomaticStaged, Sequential, or Manual. TargetBranch may propose a new valid Git branch but never implies merge authorization.";'''
gateway = replace_once(gateway, old_prompt, new_prompt, "manager prompt contract")
gateway_path.write_text(gateway, encoding="utf-8")

tests = tests_path.read_text(encoding="utf-8")
anchor = '''    [Fact]
    public void Valid_worker_handoff_passes_quality_gate()'''
insert = '''    [Fact]
    public void Live_manager_schema_accepts_structured_routing_priority_pr_arrays_and_external_dependency_context()
    {
        var routing = Routing();
        var baseline = Baseline(routing);
        var json = JsonSerializer.Serialize(new
        {
            ManagerEstimate = 40,
            ExpectedHead = "pr-head",
            ExpectedRoutingIdentity = new
            {
                ProjectId = routing.ProjectControlId,
                DisplayName = routing.DisplayName,
                PccSourceSha = routing.Provenance.SourceSha,
                Repository = routing.Repository,
                CanonicalTask = "PCCEXECUTIVE-T0001",
                TargetScope = routing.Scope.ToString(),
                TargetVariant = routing.VariantId,
                ImplementationRoot = routing.ImplementationLocation,
                DefaultBranch = baseline.DefaultBranch,
                DefaultHead = baseline.DefaultHeadSha,
                ConvergenceBranch = "worker/test",
                ConvergenceHead = "pr-head"
            },
            ProjectDecision = "RECONCILE_AND_CONVERGE",
            KnownBlockers = Array.Empty<string>(),
            Tasks = new[]
            {
                new
                {
                    TaskId = TaskId.New().ToString(),
                    Objective = "integrate green stack",
                    Repository = routing.Repository,
                    Paths = new[] { "src/a" },
                    Components = Array.Empty<string>(),
                    ExclusiveResources = Array.Empty<string>(),
                    Dependencies = new[] { "PR #4 exact green input" },
                    AcceptanceCriteria = new[] { "exact-head green" },
                    EvidenceExpected = new[] { "tests" },
                    Priority = "P0",
                    SuggestedWorkerSlot = 1,
                    Reason = "needed",
                    KnownBlockers = Array.Empty<string>(),
                    RequiredPreviousTasks = Array.Empty<string>(),
                    RecommendedExecutionMode = "AUTONOMOUS_EXACT_HEAD_INTEGRATION",
                    TargetScope = routing.Scope.ToString(),
                    TargetVariant = routing.VariantId,
                    ExpectedHead = "pr-head",
                    RelatedPullRequest = new[] { 4 },
                    ExpectedPullRequestState = "OPEN_UNMERGED_GREEN_INPUTS",
                    TargetBranch = "manager/new-convergence",
                    FeatureExpansion = false
                }
            }
        });

        var parsed = new StructuredManagerPlanParser().Parse(json);

        Assert.True(parsed.IsValid, string.Join("; ", parsed.Findings.Select(x => $"{x.Code}:{x.Message}")));
        var proposal = Assert.Single(parsed.Plan!.Tasks);
        Assert.Equal(0, proposal.Priority);
        Assert.Empty(proposal.Task.Dependencies);
        Assert.Equal(new[] { 4 }, proposal.RelatedPullRequests);
        Assert.Equal(ManagerExecutionMode.AutomaticStaged, proposal.RecommendedExecutionMode);
        var validation = new ManagerWaveValidator().Validate(parsed.Plan, routing, baseline, new CompletedIndex(), ProjectCompletionMode.Active);
        Assert.True(validation.IsValid, string.Join("; ", validation.Findings.Select(x => $"{x.Code}:{x.Message}")));
        Assert.Contains(validation.Findings, x => x.Code == "TASK_BRANCH_NEW" && x.Severity == PlanFindingSeverity.Info);
    }

    [Fact]
    public void Structured_routing_expectation_still_fails_closed_on_live_mismatch()
    {
        var routing = Routing();
        var baseline = Baseline(routing);
        var json = JsonSerializer.Serialize(new
        {
            ManagerEstimate = 10,
            ExpectedHead = baseline.DefaultHeadSha,
            ExpectedRoutingIdentity = new
            {
                ProjectId = routing.ProjectControlId,
                Repository = "owner/wrong",
                PccSourceSha = routing.Provenance.SourceSha,
                TargetScope = routing.Scope.ToString()
            },
            Tasks = Array.Empty<object>()
        });

        var parsed = new StructuredManagerPlanParser().Parse(json);
        Assert.True(parsed.IsValid);
        var validation = new ManagerWaveValidator().Validate(parsed.Plan!, routing, baseline, new CompletedIndex(), ProjectCompletionMode.Active);
        Assert.False(validation.IsValid);
        Assert.Contains(validation.Findings, x => x.Code == "ROUTING_CHANGED");
    }

'''
tests = replace_once(tests, anchor, insert + anchor, "schema regression tests")
tests_path.write_text(tests, encoding="utf-8")

print("Manager plan schema recovery patch applied.")
