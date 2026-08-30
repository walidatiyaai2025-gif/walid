using System;
using System.IO;
using Xunit;

namespace PCCExecutive.App.Tests;

public sealed class AutopilotLivelockRecoveryContractTests
{
    private static string Source()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PCCExecutive.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory!.FullName, "src", "PCCExecutive.App", "Presentation", "IntegratedPresentationGateway.cs"));
    }

    [Fact]
    public void Restart_uses_actionable_autopilot_vocabulary()
    {
        var source = Source();
        Assert.DoesNotContain("_autopilot = recovered.Phase.ToString().ToUpperInvariant();", source, StringComparison.Ordinal);
        Assert.Contains("MapRecoveredPhaseToAutopilot(recovered.Phase, recovered.CurrentWave)", source, StringComparison.Ordinal);
        Assert.Contains("OrchestrationPhase.ManagerPlanning => \"PLANNING\"", source, StringComparison.Ordinal);
        Assert.Contains("OrchestrationPhase.WaveValidation => wave?.State == WaveState.Ready ? \"READY_TO_DISPATCH\"", source, StringComparison.Ordinal);
        Assert.Contains("ProjectRunState.StalledAutoStopped => RecoverStalledManagerResponseState()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Same_unaccepted_response_is_not_counted_as_three_manager_waves()
    {
        var source = Source();
        var validation = source.IndexOf("if (!validation.IsValid)", StringComparison.Ordinal);
        var enqueue = source.IndexOf("_recentPlanFingerprints.Enqueue(planFingerprint);", validation, StringComparison.Ordinal);
        Assert.True(validation >= 0);
        Assert.True(enqueue > validation);
        Assert.Contains("identical accepted task fingerprint across three Manager waves", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Legacy_false_stall_recovers_by_reparsing_existing_response()
    {
        var source = Source();
        Assert.Contains("legacyUnacceptedResponseSelfStall", source, StringComparison.Ordinal);
        Assert.Contains("_recentPlanFingerprints.Clear();", source, StringComparison.Ordinal);
        Assert.Contains("no duplicate Manager prompt will be sent", source, StringComparison.Ordinal);
        Assert.Contains("NormalizeRecoveredAutopilotState();", source, StringComparison.Ordinal);
    }
}

