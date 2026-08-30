using PCCExecutive.Application;

namespace PCCExecutive.App.Presentation;

public sealed class SnapshotRuntimeInspectorStateSource(Func<RuntimeSnapshot> snapshot) : IRuntimeInspectorStateSource
{
    public Task<RuntimeInspectorState> CaptureAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var current = snapshot();
        var prerequisites = new[]
        {
            new RuntimePrerequisiteEvidence(GuidedStepId.Chrome, "PCC-owned Chrome runtime ready", current.Sessions.Any(s => s.IsPccOwned), "CHROME_RUNTIME", true, false),
            new RuntimePrerequisiteEvidence(GuidedStepId.Project, "Canonical project selected", current.HasActiveRun, "PROJECT_BOUND", false, true),
            new RuntimePrerequisiteEvidence(GuidedStepId.Manager, "Manager runtime exists", current.HasManagerRuntime, "MANAGER_RUNTIME", true, false),
            new RuntimePrerequisiteEvidence(GuidedStepId.Orchestration, "Manager and run ready", current.HasActiveRun && current.HasManagerRuntime, "ORCHESTRATION_READY", true, false)
        };
        var browserEvidence = current.Sessions.Where(s => s.IsPccOwned).Select(s => new BrowserRuntimeEvidence(
            "Selected PCC profile source", s.RuntimeId, s.Role, s.ProcessId, s.Health.ToString(),
            "PCC ownership asserted by canonical session registry", true)).ToArray();
        var nextStep = !current.Sessions.Any(s => s.IsPccOwned) ? GuidedStepId.Chrome : !current.HasActiveRun ? GuidedStepId.Project : !current.HasManagerRuntime ? GuidedStepId.Manager : GuidedStepId.Orchestration;
        var next = new GuidedNextAction(nextStep, GuidedActionKind.Navigate, "LIVE_SNAPSHOT", current.OperatorMessage);
        return Task.FromResult(new RuntimeInspectorState(
            current.HasActiveRun ? current.Projects.FirstOrDefault()?.Id : null,
            current.ProviderMode.ToString(), current.GlobalHealth.ToString(),
            current.HasManagerRuntime ? "READY" : "NOT READY", $"{current.ActiveWorkers} active",
            current.Sessions.Count(s => s.IsPccOwned), current.AutopilotState, next, prerequisites, browserEvidence));
    }
}

public sealed class RuntimeInspectorServices
{
    public RuntimeInspectorServices(IRuntimeDiagnosticCollector collector, IRuntimeInspectorStateSource stateSource, Func<string, string?, int, CancellationToken, Task<string>> exportJson)
    { Collector = collector; StateSource = stateSource; ExportJson = exportJson; }
    public IRuntimeDiagnosticCollector Collector { get; }
    public IRuntimeInspectorStateSource StateSource { get; }
    public Func<string, string?, int, CancellationToken, Task<string>> ExportJson { get; }
}
