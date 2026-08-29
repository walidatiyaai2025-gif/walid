from pathlib import Path

manager_path = Path('src/PCCExecutive.Application/ManagerOrchestration.cs')
gateway_path = Path('src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs')
test_path = Path('tests/PCCExecutive.Application.Tests/ManagerTargetScopeRecoveryTests.cs')

manager = manager_path.read_text(encoding='utf-8')
gateway = gateway_path.read_text(encoding='utf-8')

old_scope = '''            if (!Enum.TryParse<ProjectScopeKind>(item.TargetScope ?? "Project", true, out var targetScope))
            {
                targetScope = ProjectScopeKind.Project;
                findings.Add(new("TARGET_SCOPE_INVALID", "TargetScope must be Project, Core, or Variant.", PlanFindingSeverity.Block, id));
            }
'''
new_scope = '''            var targetScope = ParseTargetScope(item.TargetScope, item.TargetVariant, id, findings);
'''
if old_scope not in manager:
    raise SystemExit('TargetScope parser anchor not found')
manager = manager.replace(old_scope, new_scope, 1)

helper_anchor = '    private static ManagerExecutionMode ParseExecutionMode(string? value, TaskId taskId, List<ManagerPlanFinding> findings)\n'
if helper_anchor not in manager:
    raise SystemExit('ParseExecutionMode anchor not found')
helper = '''    private static ProjectScopeKind ParseTargetScope(
        string? value,
        string? targetVariant,
        TaskId taskId,
        List<ManagerPlanFinding> findings)
    {
        if (string.IsNullOrWhiteSpace(value)) return ProjectScopeKind.Project;
        var text = value.Trim();
        if (Enum.TryParse<ProjectScopeKind>(text, true, out var direct)) return direct;

        var normalized = new string(text.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        if (normalized.Contains("VARIANT", StringComparison.Ordinal)) return ProjectScopeKind.Variant;
        if (normalized.Contains("CORE", StringComparison.Ordinal)) return ProjectScopeKind.Core;
        if (normalized.Contains("PROJECT", StringComparison.Ordinal)) return ProjectScopeKind.Project;

        if (!string.IsNullOrWhiteSpace(targetVariant))
        {
            findings.Add(new(
                "TARGET_SCOPE_NORMALIZED",
                $"TargetScope '{value}' is not canonical; TargetVariant is present so PCC normalized it to Variant. Live routing validation remains authoritative.",
                PlanFindingSeverity.Info,
                taskId));
            return ProjectScopeKind.Variant;
        }

        findings.Add(new(
            "TARGET_SCOPE_NORMALIZED",
            $"TargetScope '{value}' is a work-area label rather than a canonical project scope; PCC normalized it to Project. Live routing validation remains authoritative.",
            PlanFindingSeverity.Info,
            taskId));
        return ProjectScopeKind.Project;
    }

'''
manager = manager.replace(helper_anchor, helper + helper_anchor, 1)

prompt_anchor = 'RecommendedExecutionMode, TargetScope, TargetVariant, ExpectedHead, RelatedPullRequest, ExpectedPullRequestState, TargetBranch, FeatureExpansion. Priority may be integer or P0/P1/... with P0 highest.'
prompt_replacement = 'RecommendedExecutionMode, TargetScope, TargetVariant, ExpectedHead, RelatedPullRequest, ExpectedPullRequestState, TargetBranch, FeatureExpansion. TargetScope MUST be exactly Project, Core, or Variant; never put component/work-area labels such as UI, Runtime, Browser, Installer, Release, Desktop, or App in TargetScope. Put those labels in Components or Paths instead. Priority may be integer or P0/P1/... with P0 highest.'
if prompt_anchor not in gateway:
    raise SystemExit('Manager prompt TargetScope anchor not found')
gateway = gateway.replace(prompt_anchor, prompt_replacement, 1)

manager_path.write_text(manager, encoding='utf-8')
gateway_path.write_text(gateway, encoding='utf-8')

test_path.write_text(r'''using System.Text.Json;
using PCCExecutive.Application;
using Xunit;

namespace PCCExecutive.Application.Tests;

public sealed class ManagerTargetScopeRecoveryTests
{
    [Theory]
    [InlineData("UI")]
    [InlineData("Runtime")]
    [InlineData("Browser")]
    [InlineData("Desktop")]
    [InlineData("Release")]
    public void Work_area_labels_normalize_to_project_without_rejecting_the_manager_plan(string label)
    {
        var json = JsonSerializer.Serialize(new
        {
            ManagerEstimate = 25,
            Tasks = new[]
            {
                new
                {
                    TaskId = Guid.NewGuid().ToString(),
                    Objective = "close current product work",
                    Repository = "owner/repo",
                    Paths = new[] { "src" },
                    Components = new[] { label },
                    ExclusiveResources = Array.Empty<string>(),
                    Dependencies = Array.Empty<string>(),
                    AcceptanceCriteria = new[] { "green" },
                    EvidenceExpected = new[] { "tests" },
                    Priority = "P0",
                    SuggestedWorkerSlot = 1,
                    Reason = "required",
                    KnownBlockers = Array.Empty<string>(),
                    RequiredPreviousTasks = Array.Empty<string>(),
                    RecommendedExecutionMode = "AutomaticStaged",
                    TargetScope = label,
                    TargetVariant = (string?)null,
                    ExpectedHead = (string?)null,
                    RelatedPullRequest = (int?)null,
                    ExpectedPullRequestState = (string?)null,
                    TargetBranch = "worker/test",
                    FeatureExpansion = false
                }
            }
        });

        var parsed = new StructuredManagerPlanParser().Parse(json);

        Assert.True(parsed.IsValid, string.Join("; ", parsed.Findings.Select(x => $"{x.Code}:{x.Message}")));
        Assert.Equal("Project", parsed.Plan!.Tasks.Single().TargetScope.ToString());
        Assert.Contains(parsed.Findings, x => x.Code == "TARGET_SCOPE_NORMALIZED" && x.Severity == PlanFindingSeverity.Info);
    }

    [Fact]
    public void Noncanonical_scope_with_variant_identity_normalizes_to_variant_so_live_variant_validation_stays_enabled()
    {
        var json = JsonSerializer.Serialize(new
        {
            ManagerEstimate = 25,
            Tasks = new[]
            {
                new
                {
                    TaskId = Guid.NewGuid().ToString(),
                    Objective = "close variant work",
                    Repository = "owner/repo",
                    Paths = new[] { "src" },
                    Components = Array.Empty<string>(),
                    ExclusiveResources = Array.Empty<string>(),
                    Dependencies = Array.Empty<string>(),
                    AcceptanceCriteria = new[] { "green" },
                    EvidenceExpected = new[] { "tests" },
                    Priority = 0,
                    SuggestedWorkerSlot = 1,
                    Reason = "required",
                    KnownBlockers = Array.Empty<string>(),
                    RequiredPreviousTasks = Array.Empty<string>(),
                    RecommendedExecutionMode = "AutomaticStaged",
                    TargetScope = "LaravelApp",
                    TargetVariant = "LARAVEL_AIWMWEB",
                    ExpectedHead = (string?)null,
                    RelatedPullRequest = (int?)null,
                    ExpectedPullRequestState = (string?)null,
                    TargetBranch = "worker/test",
                    FeatureExpansion = false
                }
            }
        });

        var parsed = new StructuredManagerPlanParser().Parse(json);

        Assert.True(parsed.IsValid, string.Join("; ", parsed.Findings.Select(x => $"{x.Code}:{x.Message}")));
        Assert.Equal("Variant", parsed.Plan!.Tasks.Single().TargetScope.ToString());
        Assert.Contains(parsed.Findings, x => x.Code == "TARGET_SCOPE_NORMALIZED");
    }
}
''', encoding='utf-8')
