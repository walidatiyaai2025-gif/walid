namespace PCCExecutive.Domain;

public interface IStableId
{
    Guid Value { get; }
}

public static class StableId
{
    public static Guid Require(Guid value, string name) =>
        value == Guid.Empty ? throw new ArgumentException($"{name} cannot be empty.", name) : value;
}

public readonly record struct ProjectId : IStableId
{
    public ProjectId(Guid value) => Value = StableId.Require(value, nameof(ProjectId));
    public Guid Value { get; }
    public static ProjectId New() => new(Guid.NewGuid());
    public static ProjectId Parse(string value) => new(Guid.Parse(value));
    public override string ToString() => Value.ToString("N");
}
public readonly record struct ProjectRunId : IStableId
{
    public ProjectRunId(Guid value) => Value = StableId.Require(value, nameof(ProjectRunId));
    public Guid Value { get; }
    public static ProjectRunId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}
public readonly record struct WaveId : IStableId
{
    public WaveId(Guid value) => Value = StableId.Require(value, nameof(WaveId));
    public Guid Value { get; }
    public static WaveId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}
public readonly record struct TaskId : IStableId
{
    public TaskId(Guid value) => Value = StableId.Require(value, nameof(TaskId));
    public Guid Value { get; }
    public static TaskId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}
public readonly record struct LogicalAgentId : IStableId
{
    public LogicalAgentId(Guid value) => Value = StableId.Require(value, nameof(LogicalAgentId));
    public Guid Value { get; }
    public static LogicalAgentId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}
public readonly record struct ConversationId : IStableId
{
    public ConversationId(Guid value) => Value = StableId.Require(value, nameof(ConversationId));
    public Guid Value { get; }
    public static ConversationId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}
public readonly record struct DispatchId : IStableId
{
    public DispatchId(Guid value) => Value = StableId.Require(value, nameof(DispatchId));
    public Guid Value { get; }
    public static DispatchId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}
public readonly record struct CheckpointId : IStableId
{
    public CheckpointId(Guid value) => Value = StableId.Require(value, nameof(CheckpointId));
    public Guid Value { get; }
    public static CheckpointId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}
public readonly record struct EvidenceId : IStableId
{
    public EvidenceId(Guid value) => Value = StableId.Require(value, nameof(EvidenceId));
    public Guid Value { get; }
    public static EvidenceId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}
public readonly record struct AttentionRequestId : IStableId
{
    public AttentionRequestId(Guid value) => Value = StableId.Require(value, nameof(AttentionRequestId));
    public Guid Value { get; }
    public static AttentionRequestId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}

public readonly record struct WorkerSlotId
{
    public const int MaxValue = 5;
    public WorkerSlotId(int value)
    {
        if (value is < 1 or > MaxValue)
            throw new ArgumentOutOfRangeException(nameof(value), value, "Worker slot must be between 1 and 5.");
        Value = value;
    }
    public int Value { get; }
    public override string ToString() => $"worker-{Value}";
}

public enum ProjectRunState { Idle, Initializing, ManagerPlanning, WaveReady, Dispatching, WaveRunning, Reconciling, ManagerReview, ClosureMode, VerifiedComplete, BlockedExternal, StalledAutoStopped, StoppedByOperator }
public enum WaveState { Planned, Validating, Ready, Dispatching, Running, Reconciling, Completed, Blocked, Failed }
public enum TaskState { Proposed, Ready, Assigned, Dispatched, Running, HandoffReceived, Validating, Completed, Failed, Blocked, Cancelled }
public enum DispatchState { PREPARED, SUBMITTED, SUBMITTED_UNKNOWN, ACKNOWLEDGED, GENERATING, COMPLETED, FAILED }
public enum LogicalSessionState { Created, Ready, Active, Degraded, Recovering, Paused, Archived, FailedRequiresAttention }
public enum ConversationState { Fresh, Active, Growing, RolloverSoon, Checkpointing, Rotating, Archived, Failed }
public enum AttentionState { Open, InProgress, Resolved, Dismissed }
public enum AgentRole { Manager, Worker }
public enum AgentProviderKind { BrowserChat, OpenAiApi }
public enum GateState { NotApplicable, Unknown, Pending, Partial, Pass, Fail, BlockedExternal }
public enum ProjectCompletionMode { Active, ClosureMode, VerifiedComplete, Blocked }
public enum LoopGuardLevel { Normal, Watch, Stagnating, LoopDetected, AutoStopped }
public enum LoopSignalType { RepeatedTaskFingerprint, RepeatedBlocker, UnchangedSourceOrEvidence, NegligibleProgress, RepeatedFailedCheck, RepeatedManagerReassignment }

public sealed class IllegalStateTransitionException<TState> : InvalidOperationException where TState : struct, Enum
{
    public IllegalStateTransitionException(TState from, TState to) : base($"Illegal {typeof(TState).Name} transition: {from} -> {to}.") { From = from; To = to; }
    public TState From { get; }
    public TState To { get; }
}

public abstract class StateMachine<TState> where TState : struct, Enum
{
    protected abstract IReadOnlyDictionary<TState, IReadOnlySet<TState>> Transitions { get; }
    public bool CanTransition(TState from, TState to) => EqualityComparer<TState>.Default.Equals(from, to) || (Transitions.TryGetValue(from, out var allowed) && allowed.Contains(to));
    public TState Transition(TState from, TState to)
    {
        if (!CanTransition(from, to)) throw new IllegalStateTransitionException<TState>(from, to);
        return to;
    }
    protected static IReadOnlyDictionary<TState, IReadOnlySet<TState>> Map(params (TState From, TState[] To)[] items) => items.ToDictionary(x => x.From, x => (IReadOnlySet<TState>)x.To.ToHashSet());
}

public sealed class ProjectRunStateMachine : StateMachine<ProjectRunState>
{
    protected override IReadOnlyDictionary<ProjectRunState, IReadOnlySet<ProjectRunState>> Transitions { get; } = Map(
        (ProjectRunState.Idle, [ProjectRunState.Initializing]),
        (ProjectRunState.Initializing, [ProjectRunState.ManagerPlanning, ProjectRunState.BlockedExternal, ProjectRunState.StoppedByOperator]),
        (ProjectRunState.ManagerPlanning, [ProjectRunState.WaveReady, ProjectRunState.ClosureMode, ProjectRunState.BlockedExternal, ProjectRunState.StalledAutoStopped, ProjectRunState.StoppedByOperator]),
        (ProjectRunState.WaveReady, [ProjectRunState.Dispatching, ProjectRunState.ManagerReview, ProjectRunState.StoppedByOperator]),
        (ProjectRunState.Dispatching, [ProjectRunState.WaveRunning, ProjectRunState.Reconciling, ProjectRunState.BlockedExternal, ProjectRunState.StoppedByOperator]),
        (ProjectRunState.WaveRunning, [ProjectRunState.Reconciling, ProjectRunState.BlockedExternal, ProjectRunState.StalledAutoStopped, ProjectRunState.StoppedByOperator]),
        (ProjectRunState.Reconciling, [ProjectRunState.ManagerReview, ProjectRunState.ClosureMode, ProjectRunState.BlockedExternal, ProjectRunState.StalledAutoStopped]),
        (ProjectRunState.ManagerReview, [ProjectRunState.ManagerPlanning, ProjectRunState.WaveReady, ProjectRunState.ClosureMode, ProjectRunState.BlockedExternal, ProjectRunState.StalledAutoStopped, ProjectRunState.StoppedByOperator]),
        (ProjectRunState.ClosureMode, [ProjectRunState.ManagerPlanning, ProjectRunState.WaveReady, ProjectRunState.VerifiedComplete, ProjectRunState.BlockedExternal, ProjectRunState.StalledAutoStopped, ProjectRunState.StoppedByOperator]));
}
public sealed class WaveStateMachine : StateMachine<WaveState>
{
    protected override IReadOnlyDictionary<WaveState, IReadOnlySet<WaveState>> Transitions { get; } = Map(
        (WaveState.Planned, [WaveState.Validating]), (WaveState.Validating, [WaveState.Ready, WaveState.Blocked, WaveState.Failed]),
        (WaveState.Ready, [WaveState.Dispatching, WaveState.Completed]), (WaveState.Dispatching, [WaveState.Running, WaveState.Reconciling, WaveState.Failed]),
        (WaveState.Running, [WaveState.Reconciling, WaveState.Blocked, WaveState.Failed]), (WaveState.Reconciling, [WaveState.Completed, WaveState.Blocked, WaveState.Failed]));
}
public sealed class TaskStateMachine : StateMachine<TaskState>
{
    protected override IReadOnlyDictionary<TaskState, IReadOnlySet<TaskState>> Transitions { get; } = Map(
        (TaskState.Proposed, [TaskState.Ready, TaskState.Blocked, TaskState.Cancelled]), (TaskState.Ready, [TaskState.Assigned, TaskState.Cancelled]),
        (TaskState.Assigned, [TaskState.Dispatched, TaskState.Blocked, TaskState.Cancelled]), (TaskState.Dispatched, [TaskState.Running, TaskState.Failed, TaskState.Blocked]),
        (TaskState.Running, [TaskState.HandoffReceived, TaskState.Failed, TaskState.Blocked]), (TaskState.HandoffReceived, [TaskState.Validating, TaskState.Failed]),
        (TaskState.Validating, [TaskState.Completed, TaskState.Failed, TaskState.Blocked]));
}
public sealed class DispatchStateMachine : StateMachine<DispatchState>
{
    protected override IReadOnlyDictionary<DispatchState, IReadOnlySet<DispatchState>> Transitions { get; } = Map(
        (DispatchState.PREPARED, [DispatchState.SUBMITTED, DispatchState.SUBMITTED_UNKNOWN, DispatchState.FAILED]),
        (DispatchState.SUBMITTED, [DispatchState.ACKNOWLEDGED, DispatchState.SUBMITTED_UNKNOWN, DispatchState.FAILED]),
        (DispatchState.SUBMITTED_UNKNOWN, [DispatchState.ACKNOWLEDGED, DispatchState.FAILED]),
        (DispatchState.ACKNOWLEDGED, [DispatchState.GENERATING, DispatchState.COMPLETED, DispatchState.FAILED]),
        (DispatchState.GENERATING, [DispatchState.COMPLETED, DispatchState.FAILED]));
    public bool RequiresReconciliation(DispatchState state) => state == DispatchState.SUBMITTED_UNKNOWN;
}
public sealed class LogicalSessionStateMachine : StateMachine<LogicalSessionState>
{
    protected override IReadOnlyDictionary<LogicalSessionState, IReadOnlySet<LogicalSessionState>> Transitions { get; } = Map(
        (LogicalSessionState.Created, [LogicalSessionState.Ready, LogicalSessionState.FailedRequiresAttention]),
        (LogicalSessionState.Ready, [LogicalSessionState.Active, LogicalSessionState.Paused, LogicalSessionState.Archived]),
        (LogicalSessionState.Active, [LogicalSessionState.Degraded, LogicalSessionState.Paused, LogicalSessionState.Archived]),
        (LogicalSessionState.Degraded, [LogicalSessionState.Recovering, LogicalSessionState.FailedRequiresAttention]),
        (LogicalSessionState.Recovering, [LogicalSessionState.Ready, LogicalSessionState.Active, LogicalSessionState.FailedRequiresAttention]),
        (LogicalSessionState.Paused, [LogicalSessionState.Ready, LogicalSessionState.Active, LogicalSessionState.Archived]));
}
public sealed class ConversationStateMachine : StateMachine<ConversationState>
{
    protected override IReadOnlyDictionary<ConversationState, IReadOnlySet<ConversationState>> Transitions { get; } = Map(
        (ConversationState.Fresh, [ConversationState.Active, ConversationState.Failed]),
        (ConversationState.Active, [ConversationState.Growing, ConversationState.RolloverSoon, ConversationState.Checkpointing, ConversationState.Archived, ConversationState.Failed]),
        (ConversationState.Growing, [ConversationState.Active, ConversationState.RolloverSoon, ConversationState.Checkpointing, ConversationState.Failed]),
        (ConversationState.RolloverSoon, [ConversationState.Checkpointing, ConversationState.Active, ConversationState.Failed]),
        (ConversationState.Checkpointing, [ConversationState.Rotating, ConversationState.Active, ConversationState.Failed]),
        (ConversationState.Rotating, [ConversationState.Archived, ConversationState.Active, ConversationState.Failed]));
}
public sealed class AttentionStateMachine : StateMachine<AttentionState>
{
    protected override IReadOnlyDictionary<AttentionState, IReadOnlySet<AttentionState>> Transitions { get; } = Map(
        (AttentionState.Open, [AttentionState.InProgress, AttentionState.Resolved, AttentionState.Dismissed]),
        (AttentionState.InProgress, [AttentionState.Open, AttentionState.Resolved, AttentionState.Dismissed]));
}

public sealed record ProjectRun(ProjectRunId Id, ProjectId ProjectId, ProjectRunState State, DateTimeOffset CreatedAt, ManagerEstimate ManagerEstimate, VerifiedCompletion VerifiedCompletion, ProjectCompletionMode CompletionMode);
public sealed record Wave(WaveId Id, ProjectRunId ProjectRunId, int Sequence, WaveState State, IReadOnlyList<TaskId> TaskIds, DateTimeOffset CreatedAt);
public sealed record TaskScope(string Repository, IReadOnlySet<string> Paths, IReadOnlySet<string> Components, IReadOnlySet<string> ExclusiveResources)
{
    public static TaskScope Create(string repository, IEnumerable<string>? paths = null, IEnumerable<string>? components = null, IEnumerable<string>? exclusiveResources = null) => new(repository.Trim(), Normalize(paths), Normalize(components), Normalize(exclusiveResources));
    private static IReadOnlySet<string> Normalize(IEnumerable<string>? values) => new HashSet<string>((values ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim().Replace('\\', '/').TrimEnd('/')), StringComparer.OrdinalIgnoreCase);
}
public sealed record WorkerTask(TaskId Id, string Objective, TaskScope Scope, IReadOnlySet<TaskId> Dependencies, IReadOnlyList<string> AcceptanceCriteria, TaskState State, string Fingerprint);
public sealed record WorkerSlot(WorkerSlotId Id, LogicalAgentId? LogicalAgentId, TaskId? CurrentTaskId, bool IsActive);
public sealed record LogicalAgentSession(LogicalAgentId Id, ProjectRunId ProjectRunId, AgentRole Role, WorkerSlotId? WorkerSlotId, TaskId? CurrentTaskId, ConversationId? CurrentConversationId, LogicalSessionState State);
public sealed record Conversation(ConversationId Id, LogicalAgentId LogicalAgentId, int Sequence, AgentProviderKind Provider, string? ProviderIdentity, string? Url, ConversationState State, DateTimeOffset CreatedAt, DateTimeOffset? RetiredAt, ConversationId? PredecessorId, ConversationId? SuccessorId, double HealthScore, long EstimatedGrowth, CheckpointId? CheckpointId, string? RolloverReason);
public sealed record Dispatch(DispatchId Id, ProjectRunId ProjectRunId, WaveId WaveId, TaskId TaskId, LogicalAgentId LogicalAgentId, ConversationId ConversationId, string ContentHash, DateTimeOffset PreparedAt, DispatchState State, DateTimeOffset? SubmittedAt, DateTimeOffset? AcknowledgedAt, DateTimeOffset? CompletedAt, DispatchId? RetryOfDispatchId, string? ReconciliationEvidence, WorkerSlotId? WorkerSlotId = null, string? ProviderConversationId = null);
public sealed record WorkerHandoff(TaskId TaskId, string Status, string? Head, IReadOnlyList<string> Changed, IReadOnlyList<string> Validation, string? Blocker, string NextAction, DateTimeOffset ReceivedAt);
public sealed record ManagerDecision(WaveId WaveId, string Decision, ManagerEstimate Estimate, IReadOnlyList<string> Blockers, DateTimeOffset CreatedAt);
public sealed record EvidenceRecord(EvidenceId Id, ProjectRunId ProjectRunId, TaskId? TaskId, string Kind, string Source, string Fingerprint, string? ExactHead, DateTimeOffset CapturedAt);
public sealed record CompletionGate(string Name, bool Mandatory, decimal Weight, GateState State, string? Evidence);
public sealed record Blocker(string Fingerprint, string Code, string Description, bool External, DateTimeOffset FirstSeenAt, DateTimeOffset LastSeenAt);
public sealed record RecoveryEvent(string Kind, string Reason, LogicalAgentId? LogicalAgentId, ConversationId? ConversationId, DispatchId? DispatchId, DateTimeOffset OccurredAt);
public sealed record AttentionRequest(AttentionRequestId Id, ProjectRunId ProjectRunId, AttentionState State, string Category, string Reason, string RequiredAction, string? OpenTarget, bool RequiresIrreversibleApproval, DateTimeOffset CreatedAt);
public readonly record struct ManagerEstimate { public ManagerEstimate(decimal percent) { if (percent is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(percent)); Percent = percent; } public decimal Percent { get; } }
public readonly record struct VerifiedCompletion { public VerifiedCompletion(decimal percent) { if (percent is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(percent)); Percent = percent; } public decimal Percent { get; } }
public sealed record WavePlan(WaveId WaveId, ManagerEstimate ManagerEstimate, IReadOnlyList<WorkerTask> Tasks, IReadOnlyList<Blocker> Blockers);
public sealed record LoopSnapshot(WaveId WaveId, IReadOnlySet<string> TaskFingerprints, IReadOnlySet<string> BlockerFingerprints, IReadOnlySet<string> SourceEvidenceFingerprints, IReadOnlySet<string> FailedCheckFingerprints, IReadOnlySet<string> ManagerReassignmentFingerprints, VerifiedCompletion VerifiedCompletion);
public sealed record LoopSignal(LoopSignalType Type, string Fingerprint, int Repetitions);
public sealed record LoopAssessment(LoopGuardLevel Level, IReadOnlyList<LoopSignal> Signals);
