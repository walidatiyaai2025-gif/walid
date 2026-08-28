namespace PCCExecutive.Application;

public sealed record GuidedRuntimeState(
    bool GatewayBound,
    bool BrowserProviderSelected,
    BrowserRecoveryState BrowserState,
    bool ProjectResolved,
    bool ProjectIdentityKnown,
    bool ProjectRunValid,
    bool ManagerRuntimeAvailable,
    bool ManagerPlanningValid,
    bool DispatchReady,
    bool GlobalSafetyBlocked = false);

public sealed record GuidedExecutionEvaluation(
    IReadOnlyDictionary<GuidedStepId, PrerequisiteEvaluation> Steps,
    GuidedNextAction NextAction)
{
    public PrerequisiteEvaluation this[GuidedStepId step] => Steps[step];
}

public sealed class GuidedExecutionEvaluator
{
    public GuidedExecutionEvaluation Evaluate(GuidedRuntimeState runtime)
    {
        var chrome = EvaluateChrome(runtime);
        var project = EvaluateProject(runtime, chrome);
        var manager = EvaluateManager(runtime, chrome, project);
        var orchestration = EvaluateOrchestration(runtime, manager);

        var steps = new Dictionary<GuidedStepId, PrerequisiteEvaluation>
        {
            [GuidedStepId.Chrome] = chrome,
            [GuidedStepId.Project] = project,
            [GuidedStepId.Manager] = manager,
            [GuidedStepId.Orchestration] = orchestration,
        };

        return new(steps, CalculateNextAction(steps));
    }

    private static PrerequisiteEvaluation EvaluateChrome(GuidedRuntimeState runtime)
    {
        if (!runtime.GatewayBound)
            return Step(GuidedStepId.Chrome, false, GuidedStepState.Blocked, "RUNTIME_NOT_BOUND", "Runtime state is not available.", GuidedStepId.Chrome, "Refresh Runtime");
        if (!runtime.BrowserProviderSelected)
            return Step(GuidedStepId.Chrome, false, GuidedStepState.Current, "BROWSER_PROVIDER_REQUIRED", "Browser-first execution must be selected.", GuidedStepId.Chrome, "Use ChatGPT Web / Chrome");

        return runtime.BrowserState switch
        {
            BrowserRecoveryState.Ready or BrowserRecoveryState.ReplacedPccRuntime =>
                Step(GuidedStepId.Chrome, true, GuidedStepState.Completed, "CHROME_READY", "PCC-owned Chrome readiness is proven."),
            BrowserRecoveryState.RecoveringRuntime or BrowserRecoveryState.DegradedEndpointStale =>
                Step(GuidedStepId.Chrome, false, GuidedStepState.Recovering, "CHROME_RECOVERING", "PCC Executive is recovering its managed Chrome runtime.", GuidedStepId.Chrome, automaticallyRecoverable: true),
            BrowserRecoveryState.LoginRequired =>
                Step(GuidedStepId.Chrome, false, GuidedStepState.AttentionRequired, "CHROME_LOGIN_REQUIRED", "ChatGPT sign-in requires operator action.", GuidedStepId.Chrome, "Complete ChatGPT Sign-in"),
            BrowserRecoveryState.OwnershipUncertain =>
                Step(GuidedStepId.Chrome, false, GuidedStepState.Blocked, "CHROME_OWNERSHIP_UNCERTAIN", "Chrome ownership cannot be proven, so PCC cannot control the runtime safely.", GuidedStepId.Chrome, "Review Browser Ownership"),
            BrowserRecoveryState.RecoveryFailed =>
                Step(GuidedStepId.Chrome, false, GuidedStepState.Failed, "CHROME_RECOVERY_FAILED", "The managed Chrome runtime could not be recovered.", GuidedStepId.Chrome, "Connect / Recover Chrome"),
            _ => Step(GuidedStepId.Chrome, false, GuidedStepState.Current, "CHROME_NOT_READY", "Chrome readiness has not been proven.", GuidedStepId.Chrome, "Connect / Recover Chrome"),
        };
    }

    private static PrerequisiteEvaluation EvaluateProject(GuidedRuntimeState runtime, PrerequisiteEvaluation chrome)
    {
        var complete = runtime.ProjectResolved && runtime.ProjectIdentityKnown && runtime.ProjectRunValid;
        if (complete)
            return Step(GuidedStepId.Project, true, GuidedStepState.Completed, "PROJECT_READY", "Canonical PCC project identity and run state are valid.");
        if (!chrome.Satisfied)
            return Step(GuidedStepId.Project, false, GuidedStepState.Pending, "PROJECT_AWAITS_CHROME", "Project selection follows proven Chrome readiness.", GuidedStepId.Chrome, chrome.RequiredControl, chrome.AutomaticallyRecoverable);
        return Step(GuidedStepId.Project, false, GuidedStepState.Current, "PROJECT_REQUIRED", "A live PCC-routed project with a valid run is required.", GuidedStepId.Project, "Open Project");
    }

    private static PrerequisiteEvaluation EvaluateManager(GuidedRuntimeState runtime, PrerequisiteEvaluation chrome, PrerequisiteEvaluation project)
    {
        if (!chrome.Satisfied) return BlockedBy(GuidedStepId.Manager, chrome, "Manager requires a proven Chrome runtime.");
        if (!project.Satisfied) return BlockedBy(GuidedStepId.Manager, project, "Manager requires a canonical project and valid run.");
        if (runtime.ManagerRuntimeAvailable)
            return Step(GuidedStepId.Manager, true, GuidedStepState.Completed, "MANAGER_READY", "Manager logical runtime is available.");
        return Step(GuidedStepId.Manager, false, GuidedStepState.Current, "MANAGER_START_REQUIRED", "The Manager runtime can now be created or recovered safely.", GuidedStepId.Manager, "Start / Continue Manager");
    }

    private static PrerequisiteEvaluation EvaluateOrchestration(GuidedRuntimeState runtime, PrerequisiteEvaluation manager)
    {
        if (!manager.Satisfied) return BlockedBy(GuidedStepId.Orchestration, manager, "Orchestration requires the Manager runtime.");
        if (runtime.GlobalSafetyBlocked)
            return Step(GuidedStepId.Orchestration, false, GuidedStepState.Blocked, "GLOBAL_SAFETY_BLOCK", "A global runtime safety guard is active.", GuidedStepId.Chrome, "Review Runtime Health");
        if (runtime.ManagerPlanningValid && runtime.DispatchReady)
            return Step(GuidedStepId.Orchestration, true, GuidedStepState.Completed, "ORCHESTRATION_READY", "Manager planning and dispatch prerequisites are valid.");
        return Step(GuidedStepId.Orchestration, false, GuidedStepState.Current, "MANAGER_PLAN_REQUIRED", "A valid Manager plan is required before dispatch.", GuidedStepId.Manager, "Start / Continue Manager");
    }

    private static PrerequisiteEvaluation BlockedBy(GuidedStepId step, PrerequisiteEvaluation prerequisite, string why) =>
        Step(step, false, GuidedStepState.Blocked, $"{step.ToString().ToUpperInvariant()}_REQUIRES_{prerequisite.Step.ToString().ToUpperInvariant()}", why,
            prerequisite.RequiredStep ?? prerequisite.Step, prerequisite.RequiredControl, prerequisite.AutomaticallyRecoverable);

    private static PrerequisiteEvaluation Step(GuidedStepId step, bool satisfied, GuidedStepState state, string code, string reason,
        GuidedStepId? requiredStep = null, string? requiredControl = null, bool automaticallyRecoverable = false) =>
        new(step, satisfied, state, code, reason, requiredStep, requiredControl, automaticallyRecoverable);

    private static GuidedNextAction CalculateNextAction(IReadOnlyDictionary<GuidedStepId, PrerequisiteEvaluation> steps)
    {
        var pending = new[] { GuidedStepId.Chrome, GuidedStepId.Project, GuidedStepId.Manager, GuidedStepId.Orchestration }
            .Select(step => steps[step]).FirstOrDefault(result => !result.Satisfied);
        if (pending is null)
            return new(GuidedStepId.Orchestration, GuidedActionKind.None, "AUTOPILOT_ACTIVE", "05+ Orchestration — PCC Executive is running the project. No operator action is required.");
        if (pending.AutomaticallyRecoverable)
            return new(pending.RequiredStep ?? pending.Step, GuidedActionKind.Automatic, pending.ReasonCode,
                $"{NumberedName(pending.RequiredStep ?? pending.Step)} — automatic recovery is in progress. No operator action is required.");

        var kind = pending.State == GuidedStepState.AttentionRequired ? GuidedActionKind.HumanAttention : GuidedActionKind.InvokeControl;
        var target = pending.RequiredStep ?? pending.Step;
        var control = pending.RequiredControl ?? "Review Status";
        return new(target, kind, pending.ReasonCode, $"{NumberedName(target)} — {control}. {pending.Reason}", control);
    }

    public static string NumberedName(GuidedStepId step) => step switch
    {
        GuidedStepId.Chrome => "01 Chrome",
        GuidedStepId.Project => "02 Project",
        GuidedStepId.Manager => "04 Manager",
        GuidedStepId.Orchestration => "05+ Orchestration",
        _ => step.ToString(),
    };
}

public sealed class GuidedNavigationGuard(GuidedExecutionEvaluator evaluator)
{
    public NavigationGuardResult Evaluate(GuidedRuntimeState runtime, GuidedStepId attemptedStep)
    {
        var evaluation = evaluator.Evaluate(runtime);
        var target = evaluation[attemptedStep];
        if (target.Satisfied || target.State == GuidedStepState.Current)
            return new(true, attemptedStep, null, evaluation.NextAction);
        return new(false, attemptedStep, ResolveMissing(evaluation, target), evaluation.NextAction);
    }

    private static PrerequisiteEvaluation ResolveMissing(GuidedExecutionEvaluation evaluation, PrerequisiteEvaluation target) =>
        target.RequiredStep is { } required && evaluation.Steps.TryGetValue(required, out var missing) ? missing : target;
}
