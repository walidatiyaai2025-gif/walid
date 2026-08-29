using PCCExecutive.Application;
using PCCExecutive.Domain;
using Xunit;

namespace PCCExecutive.Application.Tests;

public sealed class ManagerOrchestrationCompilationGuards
{
    [Fact]
    public void Public_orchestration_contracts_are_constructible_without_runtime_implementations()
    {
        var task = new WorkerTask(
            TaskId.New(),
            "objective",
            TaskScope.Create("owner/repo", ["src/a"]),
            new HashSet<TaskId>(),
            ["criterion"],
            TaskState.Proposed,
            "fingerprint");
        var proposal = new ManagerTaskProposal(
            task,
            ["head"],
            1,
            new WorkerSlotId(1),
            "reason",
            [],
            new HashSet<TaskId>(),
            ManagerExecutionMode.AutomaticStaged,
            ProjectScopeKind.Project,
            null,
            null,
            null,
            null,
            null,
            false);

        var plan = new StructuredManagerPlan(new ManagerEstimate(1), [proposal], null, null, null, []);
        var health = new RuntimeHealthSnapshot(false, true, TimeSpan.FromSeconds(10), null);
        var batch = new SafeDispatchPlanner().Schedule(plan, new Dictionary<TaskId, TaskState>(), new HashSet<WorkerSlotId>(), health);

        Assert.Single(batch.Assignments);
    }
}
