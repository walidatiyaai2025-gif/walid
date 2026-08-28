using PCCExecutive.Application;
using PCCExecutive.Domain;

namespace PCCExecutive.App.Presentation;

// App-local durable observation includes its capture timestamp. The canonical
// domain LoopSnapshot intentionally does not; adapt without mutating domain contracts.
internal sealed record LoopSnapshot(
    WaveId WaveId,
    IReadOnlySet<string> TaskFingerprints,
    IReadOnlySet<string> BlockerFingerprints,
    IReadOnlySet<string> SourceEvidenceFingerprints,
    IReadOnlySet<string> FailedCheckFingerprints,
    IReadOnlySet<string> ManagerReassignmentFingerprints,
    VerifiedCompletion VerifiedCompletion,
    DateTimeOffset CapturedAt);

internal static class LoopGuardCompatibilityExtensions
{
    internal static LoopAssessment Analyze(
        this LoopGuardService service,
        IReadOnlyList<LoopSnapshot> snapshots,
        int repetitionThreshold,
        decimal negligibleProgress)
    {
        var canonical = snapshots.Select(x => new PCCExecutive.Domain.LoopSnapshot(
            x.WaveId,
            x.TaskFingerprints,
            x.BlockerFingerprints,
            x.SourceEvidenceFingerprints,
            x.FailedCheckFingerprints,
            x.ManagerReassignmentFingerprints,
            x.VerifiedCompletion)).ToArray();
        return service.Analyze(canonical, repetitionThreshold, negligibleProgress);
    }
}
