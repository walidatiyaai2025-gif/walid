using System.Collections.ObjectModel;

namespace PCCExecutive.Browser;

public enum BrowserVisibility { Hidden, Visible }
public enum BrowserSessionState { Creating, Ready, Hidden, Visible, Active, Degraded, Recovering, Archived, Killed, FailedRequiresAttention }
public enum InputState { Ready, Disabled, Unknown }
public enum GenerationState { Idle, Generating, Complete, Unknown }
public enum AuthState { Authenticated, LoginRequired, Challenge, Unknown }
public enum ConversationMatch { Match, Mismatch, Unknown }
public enum PageHealth { Healthy, Slow, TempError, RateLimited, Offline, Unknown }
public enum ResponseCompleteness { None, Complete, Partial, Unknown }
public enum ChatGptResilienceState { Ready, Sending, Generating, Slow, Throttled, RateLimited, TempError, PartialResponse, SessionExpired, LoginRequired, Offline, Stuck, Recovering, Paused, Failed, Done }
public enum FaultScope { None, PerSession, Global }
public enum DispatchMode { Manual, Assisted, AutomaticStaged }
public enum DispatchState { Prepared, Submitting, Submitted, SubmittedUnknown, Acknowledged, Generating, ResponseComplete, SafeRetry, Failed }
public enum BrowserDispatchOutcome { NotSent, Submitted, SubmittedUnknown, DuplicateBlocked }
public enum ConversationLifecycleState { Active, RolloverPending, Candidate, Archived, FailedCandidate }
public enum ConversationHealthState { Fresh, Growing, RolloverSoon, Rotate }

public sealed record BrowserRuntimeRecord
{
    public required string RuntimeId { get; init; }
    public required string ProjectRunId { get; init; }
    public required string LogicalAgentId { get; init; }
    public string? WorkerSlotId { get; init; }
    public string? TaskId { get; init; }
    public int? ProcessId { get; init; }
    public string? ProcessStartIdentity { get; init; }
    public string? ContextIdentity { get; init; }
    public required string ProfilePath { get; init; }
    public bool CreatedByPcc { get; init; }
    public bool AdoptedExplicitly { get; init; }
    public string? ConversationIdentity { get; init; }
    public string? ProviderConversationIdentity { get; init; }
    public BrowserVisibility Visibility { get; init; } = BrowserVisibility.Hidden;
    public BrowserSessionState State { get; init; } = BrowserSessionState.Creating;
    public DateTimeOffset LastHeartbeatAt { get; init; }
    public DateTimeOffset LastActivityAt { get; init; }
    public bool IsArchived { get; init; }
    public required string OwnershipNonce { get; init; }
}

public sealed record BrowserSessionRequest(
    string ProjectRunId,
    string LogicalAgentId,
    string? WorkerSlotId = null,
    string? TaskId = null,
    string? ConversationIdentity = null,
    string? ProviderConversationIdentity = null,
    BrowserVisibility DefaultVisibility = BrowserVisibility.Hidden,
    string? RuntimeId = null);

public sealed record OwnershipMarker(
    string RuntimeId,
    int ProcessId,
    string ProcessStartIdentity,
    string ContextIdentity,
    string ProfilePath,
    bool CreatedByPcc,
    bool AdoptedExplicitly,
    string OwnershipNonce);

public sealed record OwnershipProof(bool IsProven, string RuntimeId, string Reason)
{
    public static OwnershipProof Denied(string runtimeId, string reason) => new(false, runtimeId, reason);
    public static OwnershipProof Proven(string runtimeId) => new(true, runtimeId, "PCC_OWNERSHIP_PROVEN");
}

public sealed record SessionActionResult(bool Succeeded, string RuntimeId, string Reason, BrowserRuntimeRecord? Runtime = null);
public sealed record KillAllResult(IReadOnlyList<string> KilledRuntimeIds, IReadOnlyDictionary<string, string> SkippedRuntimeReasons);
public sealed record BrowserRuntimeTelemetry(
    string RuntimeId,
    bool ProcessAlive,
    int OwnedProcessCount,
    long WorkingSetBytes,
    TimeSpan CpuTime,
    DateTimeOffset LastHeartbeatAt,
    bool IsIdle,
    bool IsArchived);

public sealed record ResourceGovernorSnapshot(
    int ActiveOwnedRuntimeCount,
    long WorkingSetBytes,
    TimeSpan CpuTime,
    DateTimeOffset CapturedAt,
    IReadOnlyList<BrowserRuntimeTelemetry> Runtimes);

public sealed record SemanticDetection<T>(T State, double Confidence, IReadOnlyList<string> Evidence, string AdapterVersion)
    where T : struct, Enum
{
    public static SemanticDetection<T> Create(T state, double confidence, string adapterVersion, params string[] evidence) =>
        new(state, confidence, new ReadOnlyCollection<string>(evidence), adapterVersion);
}

public sealed record ChatGptSemanticSnapshot(
    SemanticDetection<InputState> Input,
    SemanticDetection<GenerationState> Generation,
    SemanticDetection<AuthState> Auth,
    SemanticDetection<ConversationMatch> Conversation,
    SemanticDetection<PageHealth> Health,
    ResponseCompleteness ResponseCompleteness,
    int AssistantMessageCount,
    string? CapturedResponseText,
    DateTimeOffset CapturedAt,
    string AdapterVersion);

public sealed record BrowserDispatchExpectation(
    string ProjectRunId,
    string LogicalAgentId,
    string TaskId,
    string ConversationIdentity,
    string ProviderConversationIdentity,
    string? WorkerSlotId = null);

public sealed record WrongChatDecision(bool MaySend, string Reason, IReadOnlyList<string> Evidence);
public sealed record AdapterSubmissionResult(bool Triggered, bool ProvenSubmitted, bool SubmittedUnknown, string Reason, IReadOnlyList<string> Evidence);

public sealed record BrowserDispatchRequest(
    string DispatchId,
    string ProjectRunId,
    string LogicalAgentId,
    string TaskId,
    string ConversationIdentity,
    string ProviderConversationIdentity,
    string Prompt,
    string? ContentHash = null,
    string? WorkerSlotId = null);

public sealed record BrowserDispatchResult(
    string DispatchId,
    BrowserDispatchOutcome Outcome,
    DispatchState State,
    string Reason,
    IReadOnlyList<string> Evidence);

public sealed record DispatchLedgerEntry(
    string DispatchId,
    string ContentHash,
    DispatchState State,
    DateTimeOffset UpdatedAt,
    string? ReconciliationEvidence = null);

public enum DispatchReservationStatus { New, RetryAllowed, DuplicateBlocked, ContentConflict }
public sealed record DispatchReservation(DispatchReservationStatus Status, DispatchLedgerEntry Entry, string Reason);

public sealed record DispatchSchedulerOptions(
    DispatchMode Mode = DispatchMode.AutomaticStaged,
    TimeSpan? BaseInterval = null,
    bool AdaptivePacing = true,
    int MaximumWorkers = 5)
{
    public TimeSpan EffectiveBaseInterval => BaseInterval ?? TimeSpan.FromSeconds(10);

    public void Validate()
    {
        if (MaximumWorkers is < 1 or > 5)
            throw new ArgumentOutOfRangeException(nameof(MaximumWorkers), "MaximumWorkers must be between 1 and 5.");
        if (EffectiveBaseInterval < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(BaseInterval));
    }
}

public sealed record DispatchTimingDecision(bool MayDispatch, DateTimeOffset? NextEligibleAt, string Reason);
public sealed record ResilienceDecision(ChatGptResilienceState State, FaultScope Scope, bool PauseUnsafeNewSends, bool RequiresHumanAction, string Reason);
public sealed record GlobalSendGateSnapshot(bool IsPaused, string? Reason, DateTimeOffset? PausedAt, DateTimeOffset? ResumeNotBefore);

public sealed record ConversationRecord
{
    public required string ConversationId { get; init; }
    public required string LogicalAgentId { get; init; }
    public required string ProjectRunId { get; init; }
    public required int Sequence { get; init; }
    public required string UrlOrProviderIdentity { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? RetiredAt { get; init; }
    public string? PredecessorConversationId { get; init; }
    public string? SuccessorConversationId { get; init; }
    public string? RolloverReason { get; init; }
    public ConversationLifecycleState State { get; init; } = ConversationLifecycleState.Active;
}

public sealed record ConversationHealthObservation(int MessageCount, long CapturedCharacterCount, TimeSpan Age, int SlowOrStuckEvents);
public sealed record ConversationHealthAssessment(ConversationHealthState State, bool IsHeuristic, string Reason);
public sealed record ConversationCreationResult(string ConversationId, string UrlOrProviderIdentity);
public sealed record ContinuationValidationResult(bool IsValid, string Reason);
public sealed record ConversationRolloverResult(bool Succeeded, ConversationRecord ActiveConversation, ConversationRecord? RetiredConversation, string Reason);

public interface IBrowserRuntimeRegistry
{
    Task UpsertAsync(BrowserRuntimeRecord runtime, CancellationToken cancellationToken = default);
    Task<BrowserRuntimeRecord?> GetAsync(string runtimeId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BrowserRuntimeRecord>> ListAsync(CancellationToken cancellationToken = default);
}

public interface IOwnershipMarkerStore
{
    Task WriteAsync(OwnershipMarker marker, CancellationToken cancellationToken = default);
    Task<OwnershipMarker?> ReadAsync(string profilePath, CancellationToken cancellationToken = default);
}

public interface IProcessInspector
{
    bool IsAlive(int processId);
    string? GetStartIdentity(int processId);
}

public interface IOwnershipProofService
{
    Task<OwnershipProof> ProveAsync(BrowserRuntimeRecord runtime, CancellationToken cancellationToken = default);
}

public interface IBrowserRuntimeHost
{
    Task<BrowserRuntimeRecord> LaunchAsync(BrowserSessionRequest request, CancellationToken cancellationToken = default);
    Task<bool> RecoverAsync(BrowserRuntimeRecord runtime, CancellationToken cancellationToken = default);
    Task SetVisibilityAsync(BrowserRuntimeRecord runtime, BrowserVisibility visibility, bool bringToFront, CancellationToken cancellationToken = default);
    Task KillAsync(BrowserRuntimeRecord runtime, OwnershipProof proof, CancellationToken cancellationToken = default);
    Task<BrowserRuntimeTelemetry> GetTelemetryAsync(BrowserRuntimeRecord runtime, CancellationToken cancellationToken = default);
}

public interface IChatGptBrowserAdapter
{
    string AdapterVersion { get; }
    Task<ChatGptSemanticSnapshot> InspectAsync(BrowserRuntimeRecord runtime, BrowserDispatchExpectation expectation, CancellationToken cancellationToken = default);
    Task<AdapterSubmissionResult> SubmitAsync(BrowserRuntimeRecord runtime, BrowserDispatchExpectation expectation, string prompt, CancellationToken cancellationToken = default);
    Task<string?> GetCurrentConversationIdentityAsync(BrowserRuntimeRecord runtime, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
}

public sealed record PreEnterAuthorizationDecision(bool Authorized, string Reason, IReadOnlyList<string> Evidence);

public interface IPhysicalSubmitAuthorizationAdapter : IChatGptBrowserAdapter
{
    Task<AdapterSubmissionResult> SubmitAuthorizedAsync(
        BrowserRuntimeRecord runtime,
        BrowserDispatchExpectation expectation,
        string prompt,
        Func<CancellationToken, Task<PreEnterAuthorizationDecision>> authorizeBeforeEnter,
        CancellationToken cancellationToken = default);
}

public interface IDispatchLedger
{
    Task<DispatchReservation> ReserveAsync(string dispatchId, string contentHash, CancellationToken cancellationToken = default);
    Task UpdateAsync(string dispatchId, DispatchState state, string? reconciliationEvidence = null, CancellationToken cancellationToken = default);
    Task<DispatchLedgerEntry?> GetAsync(string dispatchId, CancellationToken cancellationToken = default);
}

public interface IConversationCheckpointPort
{
    Task<string> CreateCheckpointAsync(ConversationRecord activeConversation, CancellationToken cancellationToken = default);
}

public interface IConversationCreator
{
    Task<ConversationCreationResult> CreateAsync(ConversationRecord predecessor, CancellationToken cancellationToken = default);
}

public interface IContinuationSender
{
    Task<bool> SendContinuationAsync(ConversationRecord candidate, string checkpointId, string continuationPacket, CancellationToken cancellationToken = default);
}

public interface IContinuationValidator
{
    Task<ContinuationValidationResult> ValidateAsync(ConversationRecord candidate, CancellationToken cancellationToken = default);
}

public interface IConversationLifecycleStore
{
    Task SaveCandidateAsync(ConversationRecord candidate, string checkpointId, CancellationToken cancellationToken = default);
    Task CommitRolloverAsync(ConversationRecord predecessorArchived, ConversationRecord successorActive, string checkpointId, CancellationToken cancellationToken = default);
    Task RecordFailedRolloverAsync(ConversationRecord predecessorStillActive, ConversationRecord? failedCandidate, string reason, CancellationToken cancellationToken = default);
}
