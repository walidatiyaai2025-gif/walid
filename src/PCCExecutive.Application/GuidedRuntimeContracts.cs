namespace PCCExecutive.Application;

public enum GuidedStepId
{
    Chrome = 1,
    Project = 2,
    Manager = 4,
    Orchestration = 5,
}

public enum GuidedStepState
{
    Pending,
    Current,
    Completed,
    Blocked,
    Failed,
    Recovering,
    AttentionRequired,
}

public enum GuidedActionKind
{
    None,
    Automatic,
    Navigate,
    InvokeControl,
    HumanAttention,
}

public sealed record PrerequisiteEvaluation(
    GuidedStepId Step,
    bool Satisfied,
    GuidedStepState State,
    string ReasonCode,
    string Reason,
    GuidedStepId? RequiredStep = null,
    string? RequiredControl = null,
    bool AutomaticallyRecoverable = false);

public sealed record GuidedNextAction(
    GuidedStepId Step,
    GuidedActionKind Kind,
    string ReasonCode,
    string Instruction,
    string? Control = null);

public sealed record NavigationGuardResult(
    bool Allowed,
    GuidedStepId AttemptedStep,
    PrerequisiteEvaluation? MissingPrerequisite,
    GuidedNextAction NextAction);

public enum BrowserRecoveryState
{
    Unknown,
    Ready,
    DegradedEndpointStale,
    RecoveringRuntime,
    ReplacedPccRuntime,
    LoginRequired,
    OwnershipUncertain,
    RecoveryFailed,
}

public enum RuntimeDiagnosticKind
{
    UserAction,
    Navigation,
    Command,
    GuardDecision,
    StateTransition,
    BrowserHealth,
    Recovery,
    Attention,
    Exception,
}

public sealed record RuntimeDiagnosticEvent(
    Guid Id,
    Guid CorrelationId,
    DateTimeOffset Timestamp,
    RuntimeDiagnosticKind Kind,
    string ReasonCode,
    string Summary,
    string? Screen = null,
    string? Control = null,
    string? Command = null,
    string? Target = null,
    bool? Allowed = null,
    string? BeforeState = null,
    string? AfterState = null,
    string? ProjectRunId = null,
    string? RuntimeId = null);
