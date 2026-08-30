using System.Text.Json;
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
