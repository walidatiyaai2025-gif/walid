using PCCExecutive.Application;
using PCCExecutive.Domain;
using Xunit;

namespace PCCExecutive.Application.Tests;

public sealed class ManagerPlanningPromptBuilderTests
{
    [Fact]
    public void Planning_prompt_contains_live_canonical_evidence_and_machine_contract()
    {
        var run = new ProjectRun(ProjectRunId.New(), ProjectId.New(), ProjectRunState.ManagerPlanning, DateTimeOffset.UtcNow, new ManagerEstimate(12), new VerifiedCompletion(7), ProjectCompletionMode.Active);
        var task = new CanonicalTaskSnapshot(
            "PCC-T0001", "GPTDESKTOP", "REQ-1", "Close manager runtime", "READY", "P0",
            "worker/manager-runtime", "main", "abc", "def", "0.1.0",
            ["src/PCCExecutive.App"], [], ["Manager must continue automatically"], [], ["CI green"]);
        var pr = new GitHubPullRequestSnapshot("owner/repo", 42, "Runtime closure", "open", false, "worker/manager-runtime", "def", "main", "abc", ["src/PCCExecutive.App/Program.cs"], DateTimeOffset.UtcNow, null);
        var baseline = new ProjectBaselineSnapshot(
            "GPTDESKTOP", "GPT Desktop", "owner/repo", ProjectModel.Standalone, ProjectScopeKind.Project, null, null,
            "pccsha", "GPTDESKTOP|owner/repo|Standalone|Project|||READY|READY", "main", "abc",
            [task], [pr], null, null, null, [], DateTimeOffset.UtcNow, EvidenceFreshness.Current);

        var prompt = ManagerPlanningPromptBuilder.Build("GPTDESKTOP", "GPT Desktop", "owner/repo", run, baseline, "PLANNING");

        Assert.Contains("LIVE_BASELINE_JSON", prompt, StringComparison.Ordinal);
        Assert.Contains("Close manager runtime", prompt, StringComparison.Ordinal);
        Assert.Contains("worker/manager-runtime", prompt, StringComparison.Ordinal);
        Assert.Contains("Return exactly one JSON object and nothing else", prompt, StringComparison.Ordinal);
        Assert.Contains("Do not ask the operator", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_repair_prompt_is_deterministic_and_demands_json_only()
    {
        var baseline = new ProjectBaselineSnapshot(
            "GPTDESKTOP", "GPT Desktop", "owner/repo", ProjectModel.Standalone, ProjectScopeKind.Project, null, null,
            "pccsha", "routing-id", "main", "abc", [], [], null, null, null, [], DateTimeOffset.UtcNow, EvidenceFreshness.Current);
        var findings = new[] { new ManagerPlanFinding("MANAGER_PLAN_NOT_STRUCTURED", "No JSON object.", PlanFindingSeverity.Block) };

        var first = ManagerPlanningPromptBuilder.BuildFormatRepair("deadbeef", findings, baseline);
        var second = ManagerPlanningPromptBuilder.BuildFormatRepair("deadbeef", findings, baseline);

        Assert.Equal(first, second);
        Assert.Contains("PCC_MANAGER_RESPONSE_FORMAT_REPAIR", first, StringComparison.Ordinal);
        Assert.Contains("REJECTED_RESPONSE_SHA256: deadbeef", first, StringComparison.Ordinal);
        Assert.Contains("Return exactly one JSON object and nothing else", first, StringComparison.Ordinal);
        Assert.Contains("Do not explain", first, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_repair_policy_allows_one_physical_attempt_and_same_response_reconciliation_only()
    {
        Assert.True(ManagerPlanningPromptBuilder.CanSubmitOrReconcileFormatRepair(0, null, "hash-a"));
        Assert.True(ManagerPlanningPromptBuilder.CanSubmitOrReconcileFormatRepair(1, "hash-a", "hash-a"));
        Assert.False(ManagerPlanningPromptBuilder.CanSubmitOrReconcileFormatRepair(1, "hash-a", "hash-b"));
        Assert.False(ManagerPlanningPromptBuilder.CanSubmitOrReconcileFormatRepair(2, "hash-a", "hash-a"));
    }
}
