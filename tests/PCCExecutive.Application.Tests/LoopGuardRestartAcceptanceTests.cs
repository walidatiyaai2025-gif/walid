using PCCExecutive.Application;
using PCCExecutive.Domain;
using Xunit;

namespace PCCExecutive.Application.Tests;

public sealed class LoopGuardRestartAcceptanceTests
{
    [Fact]
    public void Repeated_plan_blocker_and_evidence_fingerprints_reach_finite_auto_stop_threshold()
    {
        var observations = Enumerable.Range(0, 3)
            .Select(i => Observation(i, task: "plan:abc", blocker: "blocker:auth", evidence: "evidence:unchanged"))
            .ToArray();

        var result = new StagnationEngine().Analyze(observations);

        Assert.True(result.IsStagnating);
        Assert.Equal(StagnationAction.STALLED_AUTO_STOPPED, result.Action);
        Assert.Contains(result.Signals, x => x.Type == LoopSignalType.RepeatedTaskFingerprint && x.Fingerprint == "plan:abc");
        Assert.Contains(result.Signals, x => x.Type == LoopSignalType.RepeatedBlocker && x.Fingerprint == "blocker:auth");
        Assert.Contains(result.Signals, x => x.Type == LoopSignalType.UnchangedSourceOrEvidence && x.Fingerprint == "evidence:unchanged");
    }

    [Fact]
    public void Restarted_history_keeps_prior_repetition_in_the_three_observation_window()
    {
        var beforeRestart = new[]
        {
            Observation(0, "plan:same", "blocker:same", "evidence:same"),
            Observation(1, "plan:same", "blocker:same", "evidence:same")
        };
        var restoredHistory = beforeRestart.ToList();
        restoredHistory.Add(Observation(2, "plan:same", "blocker:same", "evidence:same"));

        var result = new StagnationEngine().Analyze(restoredHistory);

        Assert.Equal(StagnationAction.STALLED_AUTO_STOPPED, result.Action);
        Assert.Equal(0m, result.VerifiedCompletionDelta);
    }

    [Fact]
    public void Fresh_verified_progress_breaks_the_stagnation_loop()
    {
        var observations = new[]
        {
            Observation(0, "plan:same", "blocker:same", "evidence:same", verified: 20m),
            Observation(1, "plan:same", "blocker:same", "evidence:same", verified: 20m),
            Observation(2, "plan:same", "blocker:same", "evidence:same", verified: 21m)
        };

        var result = new StagnationEngine().Analyze(observations);

        Assert.False(result.IsStagnating);
        Assert.Equal(StagnationAction.CONTINUE, result.Action);
    }

    private static StagnationObservation Observation(
        int offset,
        string task,
        string blocker,
        string evidence,
        decimal verified = 20m) => new(
            DateTimeOffset.UtcNow.AddMinutes(offset),
            "head:same",
            new HashSet<string>(StringComparer.Ordinal) { task },
            new HashSet<string>(StringComparer.Ordinal) { blocker },
            new HashSet<string>(StringComparer.Ordinal) { "failed-check:same" },
            new HashSet<string>(StringComparer.Ordinal) { "pr:open" },
            new HashSet<string>(StringComparer.Ordinal) { evidence },
            new HashSet<string>(StringComparer.Ordinal) { "manager:retry" },
            new ManagerEstimate(50m),
            new VerifiedCompletion(verified));
}
