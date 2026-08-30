using System.Text;
using System.Text.Json;
using PCCExecutive.Domain;

namespace PCCExecutive.Application;

public static class ManagerPlanningPromptBuilder
{
    public const int MaximumFormatRepairAttempts = 1;

    public static string Build(
        string projectControlId,
        string displayName,
        string repository,
        ProjectRun run,
        ProjectBaselineSnapshot baseline,
        string autopilotState)
    {
        var liveEvidence = JsonSerializer.Serialize(new
        {
            baseline.ProjectControlId,
            baseline.DisplayName,
            baseline.Repository,
            baseline.ProjectModel,
            baseline.Scope,
            baseline.VariantId,
            baseline.ImplementationLocation,
            baseline.PccSourceSha,
            baseline.RoutingIdentity,
            baseline.DefaultBranch,
            baseline.DefaultHeadSha,
            baseline.CanonicalTasks,
            baseline.RelevantPullRequests,
            baseline.Checks,
            baseline.DesiredState,
            baseline.LatestRelease,
            baseline.KnownBlockers,
            baseline.CapturedAt,
            baseline.Freshness
        });

        var text = new StringBuilder();
        text.AppendLine($"PROJECT_ID: {projectControlId}");
        text.AppendLine($"DISPLAY_NAME: {displayName}");
        text.AppendLine($"REPOSITORY: {repository}");
        text.AppendLine($"PROJECT_RUN: {run.Id}");
        text.AppendLine($"PCC_SOURCE_SHA: {baseline.PccSourceSha}");
        text.AppendLine($"ROUTING_IDENTITY: {baseline.RoutingIdentity}");
        text.AppendLine($"DEFAULT_BRANCH: {baseline.DefaultBranch}");
        text.AppendLine($"DEFAULT_HEAD: {baseline.DefaultHeadSha}");
        text.AppendLine($"VERIFIED_COMPLETION: {run.VerifiedCompletion.Percent}");
        text.AppendLine($"MANAGER_ESTIMATE: {run.ManagerEstimate.Percent}");
        text.AppendLine("ACTIVE_WORKERS: 0/5");
        text.AppendLine($"AUTOPILOT: {autopilotState}");
        text.AppendLine();
        text.AppendLine("LIVE_BASELINE_JSON:");
        text.AppendLine(liveEvidence);
        text.AppendLine();
        text.AppendLine("Act as the PCC Manager. Derive the next evidence-backed Wave from LIVE_BASELINE_JSON. Do not ask the operator for information already present in that snapshot and do not invent repository state.");
        text.AppendLine(OutputContract());
        return text.ToString().TrimEnd();
    }

    public static string BuildFormatRepair(
        string rejectedResponseHash,
        IReadOnlyList<ManagerPlanFinding> findings,
        ProjectBaselineSnapshot baseline)
    {
        var findingText = findings.Count == 0
            ? "MANAGER_PLAN_NOT_STRUCTURED"
            : string.Join("; ", findings.Select(x => $"{x.Code}:{x.Message}"));
        var livePullRequests = JsonSerializer.Serialize(baseline.RelevantPullRequests.Select(pr => new
        {
            pr.Number,
            pr.Title,
            pr.State,
            pr.Merged,
            pr.HeadBranch,
            pr.HeadSha,
            pr.BaseBranch,
            pr.BaseSha
        }));
        var liveEvidenceDrift = findings.Any(ManagerLiveEvidenceRecoveryPolicy.IsRecoverableFinding);

        var text = new StringBuilder();
        text.AppendLine(liveEvidenceDrift ? "PCC_MANAGER_LIVE_EVIDENCE_REPAIR" : "PCC_MANAGER_RESPONSE_FORMAT_REPAIR");
        text.AppendLine($"REJECTED_RESPONSE_SHA256: {rejectedResponseHash}");
        text.AppendLine($"EXPECTED_HEAD: {baseline.DefaultHeadSha}");
        text.AppendLine($"EXPECTED_ROUTING_IDENTITY: {baseline.RoutingIdentity}");
        text.AppendLine($"VALIDATION_FINDINGS: {findingText}");
        text.AppendLine($"LIVE_RELEVANT_PULL_REQUESTS_JSON: {livePullRequests}");
        text.AppendLine();
        if (liveEvidenceDrift)
        {
            text.AppendLine("Your previous structured plan conflicts with fresh PCC/GitHub evidence. Re-derive every affected task from the live evidence above instead of repeating the stale PR assumption. A PR number that is absent from LIVE_RELEVANT_PULL_REQUESTS_JSON must not be referenced again. If a referenced PR changed state or merged, use the live state. If no task remains safe after removing the contradicted assumption, return ProjectDecision BLOCKED with concrete KnownBlockers and an empty Tasks array. Do not explain, apologize, quote the previous response, or use markdown fences.");
        }
        else
        {
            text.AppendLine("Your previous response cannot be consumed by PCC. Re-emit the same intended plan in the required machine-readable contract. Do not explain, apologize, quote the previous response, or use markdown fences.");
        }
        text.AppendLine(OutputContract());
        return text.ToString().TrimEnd();
    }

    public static bool CanSubmitOrReconcileFormatRepair(int attemptsUsed, string? lastRejectedResponseHash, string currentRejectedResponseHash)
    {
        if (attemptsUsed < 0) throw new ArgumentOutOfRangeException(nameof(attemptsUsed));
        if (string.IsNullOrWhiteSpace(currentRejectedResponseHash)) throw new ArgumentException("Rejected response hash is required.", nameof(currentRejectedResponseHash));
        if (attemptsUsed == 0) return true;
        return attemptsUsed <= MaximumFormatRepairAttempts && StringComparer.Ordinal.Equals(lastRejectedResponseHash, currentRejectedResponseHash);
    }

    private static string OutputContract()
    {
        return "Return exactly one JSON object and nothing else. Required top-level fields: ManagerEstimate, ExpectedHead, ExpectedRoutingIdentity, ProjectDecision, KnownBlockers, Tasks (0..5). " +
               "ExpectedHead must match DEFAULT_HEAD/EXPECTED_HEAD. ExpectedRoutingIdentity should be the exact ROUTING_IDENTITY/EXPECTED_ROUTING_IDENTITY string supplied by PCC. " +
               "Each task requires TaskId (a non-empty GUID), Objective, Repository, Paths, Components, ExclusiveResources, Dependencies, AcceptanceCriteria, EvidenceExpected, Priority, SuggestedWorkerSlot (1..5), Reason, KnownBlockers, RequiredPreviousTasks, RecommendedExecutionMode, TargetScope, TargetVariant, ExpectedHead, RelatedPullRequest, ExpectedPullRequestState, TargetBranch, FeatureExpansion. " +
               "TargetScope must be exactly Project, Core, or Variant. Put work-area labels such as UI, Runtime, Browser, Installer, Release, Desktop, or App in Components or Paths, never TargetScope. " +
               "Priority may be an integer or P0/P1/... with P0 highest. Dependencies and RequiredPreviousTasks may contain intra-wave TaskId GUIDs only. RelatedPullRequest may be one integer, an integer array, or null. RecommendedExecutionMode should be AutomaticStaged, Sequential, or Manual. " +
               "If work can continue, ProjectDecision must be CONTINUE and Tasks must contain at least one evidence-backed task. If fresh evidence proves no task can safely run, ProjectDecision must be BLOCKED, KnownBlockers must contain the concrete blocker(s), and Tasks must be an empty array. If terminal closure is justified, use ProjectDecision CLOSE with an empty Tasks array.";
    }
}
