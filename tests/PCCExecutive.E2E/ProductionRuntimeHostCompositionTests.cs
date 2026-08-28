using PCCExecutive.App.Presentation;
using Xunit;

namespace PCCExecutive.E2E;

public sealed class ProductionRuntimeHostCompositionTests
{
    [Fact]
    public async Task Final_32_stage_gate_executes_real_production_PccExecutiveRuntimeHost_composition()
    {
        await using var host = PccExecutiveRuntimeHost.Create();
        var snapshot = await host.SnapshotAsync();
        Assert.NotNull(snapshot);
        Assert.NotNull(snapshot.Project);
        Assert.NotNull(snapshot.Autopilot);
    }
}