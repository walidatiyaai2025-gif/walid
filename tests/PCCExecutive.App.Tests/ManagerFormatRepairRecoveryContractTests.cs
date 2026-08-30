using System;
using System.IO;
using Xunit;

namespace PCCExecutive.App.Tests;

public sealed class ManagerFormatRepairRecoveryContractTests
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
    public void Manager_prompt_is_built_from_live_baseline_instead_of_metadata_only()
    {
        var source = Source();
        Assert.Contains("ManagerPlanningPromptBuilder.Build(", source, StringComparison.Ordinal);
        Assert.Contains("await ResetManagerFormatRepairStateAsync(run, cancellationToken)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_manager_response_gets_one_durable_repair_before_stall()
    {
        var source = Source();
        var parse = source.IndexOf("new StructuredManagerPlanParser().Parse(semantic.CapturedResponseText)", StringComparison.Ordinal);
        var repair = source.IndexOf("TryRepairManagerResponseFormatAsync", parse, StringComparison.Ordinal);
        var terminalReject = source.IndexOf("Manager response rejected after bounded automatic format repair", parse, StringComparison.Ordinal);
        Assert.True(parse >= 0);
        Assert.True(repair > parse);
        Assert.True(terminalReject > repair);
        Assert.Contains("CanSubmitOrReconcileFormatRepair", source, StringComparison.Ordinal);
        Assert.Contains("REPAIRING_MANAGER_FORMAT", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Structured_zero_task_blocker_becomes_explicit_external_block_not_parser_livelock()
    {
        var source = Source();
        Assert.Contains("ProjectDecision, \"BLOCKED\"", source, StringComparison.Ordinal);
        Assert.Contains("ProjectRunState.BlockedExternal", source, StringComparison.Ordinal);
        Assert.Contains("OrchestrationPhase.BlockedExternal", source, StringComparison.Ordinal);
    }
}
