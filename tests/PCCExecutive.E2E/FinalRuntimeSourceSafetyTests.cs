using Xunit;

namespace PCCExecutive.E2E;

public sealed class FinalRuntimeSourceSafetyTests
{
    [Fact]
    public void Production_callers_use_canonical_dispatch_and_automatic_rollover()
    {
        var root = FindRepositoryRoot();
        var host = File.ReadAllText(Path.Combine(root, "src", "PCCExecutive.App", "Presentation", "IntegratedPresentationGateway.cs"));
        var workers = File.ReadAllText(Path.Combine(root, "src", "PCCExecutive.Application", "ManagerWorkerOrchestration.cs"));
        var rollover = File.ReadAllText(Path.Combine(root, "src", "PCCExecutive.App", "Presentation", "AutonomousConversationRolloverRuntime.cs"));
        Assert.DoesNotContain("DispatchId.New()", host, StringComparison.Ordinal);
        Assert.DoesNotContain("DispatchId.New()", workers, StringComparison.Ordinal);
        Assert.Contains("CanonicalDispatchReservationService", host, StringComparison.Ordinal);
        Assert.Contains("AutonomousConversationRolloverRuntime.Attach(gateway)", host, StringComparison.Ordinal);
        Assert.Contains("ConversationLifecycleManager", rollover, StringComparison.Ordinal);
        Assert.Contains("RepairInterruptedRolloversAsync", rollover, StringComparison.Ordinal);
        Assert.Contains("NormalizeActiveConversationTruthAsync", rollover, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "PCCExecutive.sln"))) return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Repository root not found.");
    }
}