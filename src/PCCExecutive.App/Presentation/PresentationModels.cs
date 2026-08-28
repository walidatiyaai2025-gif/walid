using System.Collections.ObjectModel;

namespace PCCExecutive.App.Presentation;

public enum ScreenId
{
    ChromeConnection,
    ProjectSelection,
    Dashboard,
    ManagerWorkspace,
    WorkersDispatch,
    WorkerChat,
    WaveSummary,
    TaskBoard,
    EvidenceVerification,
    LoopGuard,
    ChatGptHealth,
    SessionMonitor,
    Settings,
    UpdateCenter,
    AttentionCenter
}

public enum HealthState
{
    Unknown,
    Healthy,
    Slow,
    Throttled,
    RateLimited,
    Cooldown,
    TemporaryError,
    PartialResponse,
    LoginRequired,
    Challenge,
    Offline,
    Stuck,
    Recovering,
    AdapterUncertain
}

public enum SessionVisibility { Hidden, Visible, Unknown }
public enum ProviderMode { BrowserWeb, OpenAiApi, Hybrid }
public enum DispatchMode { Manual, Assisted, AutomaticStaged }
public enum CompletionMode { Unknown, Running, ClosureMode, Verified, Blocked }

public sealed record NavigationItem(ScreenId Id, string Label, string Glyph);

public sealed record ProjectSummary(
    string Id,
    string DisplayName,
    string Repository,
    int? VerifiedCompletion,
    string State,
    string? CurrentWave,
    DateTimeOffset? LastActivity);

public sealed record SessionSummary(
    string RuntimeId,
    string LogicalName,
    string Role,
    string State,
    SessionVisibility Visibility,
    string ConversationOrTask,
    DateTimeOffset? LastActivity,
    bool IsPccOwned,
    int? ProcessId,
    HealthState Health);

public sealed record WorkerSummary(
    string Id,
    string LogicalName,
    string Role,
    string State,
    int? Progress,
    string CurrentTask,
    HealthState Health,
    string? LatestHandoff);

public sealed record TaskSummary(
    string Id,
    string Title,
    string State,
    string Priority,
    string? Owner,
    bool EvidenceVerified);

public sealed record EvidenceGateSummary(
    string Name,
    string State,
    int? Score,
    string Evidence);

public sealed record AttentionSummary(
    string Id,
    string WhatHappened,
    string WhyActionRequired,
    string ActionLabel,
    string ExactLocation,
    string Severity);

public sealed record RecoveryEventSummary(
    DateTimeOffset At,
    string State,
    string Description,
    bool Automatic);

public sealed record UpdateSummary(
    string CurrentVersion,
    string? NewVersion,
    string PackageVerification,
    string BackupState,
    string MigrationState,
    string RollbackState,
    bool InstallReady);

public sealed record DispatchSettingsSummary(
    DispatchMode Mode,
    int BaseIntervalSeconds,
    bool AdaptivePacing,
    int MaxWorkers,
    bool AutoPauseOnLimit,
    bool AutoResume,
    bool DuplicateSendProtection)
{
    public static DispatchSettingsSummary ProductDefaults { get; } =
        new(DispatchMode.AutomaticStaged, 10, true, 5, true, true, true);
}

public sealed record RuntimeSnapshot(
    bool GatewayBound,
    bool HasActiveRun,
    string RuntimeStatus,
    HealthState GlobalHealth,
    string AutopilotState,
    string CurrentWave,
    int? VerifiedCompletion,
    int? ManagerEstimate,
    CompletionMode CompletionMode,
    int ActiveWorkers,
    int P0Count,
    int P1Count,
    int BlockerCount,
    string LoopGuardState,
    string LatestManagerHandoff,
    string CurrentExecutionFlow,
    bool ApiConfigured,
    ProviderMode ProviderMode,
    DispatchSettingsSummary DispatchSettings,
    UpdateSummary Update,
    IReadOnlyList<ProjectSummary> Projects,
    IReadOnlyList<SessionSummary> Sessions,
    IReadOnlyList<WorkerSummary> Workers,
    IReadOnlyList<TaskSummary> Tasks,
    IReadOnlyList<EvidenceGateSummary> EvidenceGates,
    IReadOnlyList<AttentionSummary> AttentionItems,
    IReadOnlyList<RecoveryEventSummary> RecoveryEvents)
{
    public int AttentionCount => AttentionItems.Count;
    public string AttentionCountText => GatewayBound ? AttentionCount.ToString() : "—";
    public string AttentionSummaryText => GatewayBound ? $"{AttentionCount} required" : "— unavailable";
    public string AttentionHeadline => !GatewayBound
        ? "Attention state unavailable until runtime binding"
        : AttentionCount == 0
            ? "0 — Nothing needs you"
            : $"{AttentionCount} required operator action{(AttentionCount == 1 ? string.Empty : "s")}";
    public string AttentionSubhead => !GatewayBound
        ? "No healthy-state claim is made until the canonical runtime supplies evidence."
        : AttentionCount == 0
            ? "Routine recovery remains automatic and unobtrusive."
            : "Each item below has one clear action and opens the exact location required.";

    public string ActiveWorkersText => GatewayBound ? ActiveWorkers.ToString() : "—";
    public string P0CountText => GatewayBound ? P0Count.ToString() : "—";
    public string P1CountText => GatewayBound ? P1Count.ToString() : "—";
    public string BlockerCountText => GatewayBound ? BlockerCount.ToString() : "—";
    public string VerifiedCompletionText => VerifiedCompletion is null ? "—" : $"{VerifiedCompletion}%";
    public string ManagerEstimateText => ManagerEstimate is null ? "—" : $"{ManagerEstimate}%";
    public string HealthText => GlobalHealth switch
    {
        HealthState.RateLimited => "RATE LIMITED",
        HealthState.TemporaryError => "TEMPORARY ERROR",
        HealthState.PartialResponse => "PARTIAL RESPONSE",
        HealthState.LoginRequired => "LOGIN REQUIRED",
        HealthState.AdapterUncertain => "ADAPTER UNCERTAIN",
        _ => GlobalHealth.ToString().ToUpperInvariant()
    };

    public string HealthAccent => GlobalHealth switch
    {
        HealthState.Healthy => "#6EE7B7",
        HealthState.Slow or HealthState.Throttled or HealthState.RateLimited or HealthState.Cooldown => "#FBBF24",
        HealthState.Recovering => "#8B5CF6",
        HealthState.Unknown => "#8FA3B8",
        _ => "#FB7185"
    };

    public string AttentionAccent => !GatewayBound ? "#8FA3B8" : AttentionCount == 0 ? "#6EE7B7" : "#FBBF24";

    public bool NoActionRequired => GatewayBound && AttentionCount == 0 &&
        GlobalHealth is HealthState.Healthy or HealthState.Slow or HealthState.Throttled or HealthState.RateLimited
            or HealthState.Cooldown or HealthState.TemporaryError or HealthState.PartialResponse or HealthState.Offline
            or HealthState.Stuck or HealthState.Recovering;

    public string OperatorMessage => !GatewayBound
        ? "Runtime contracts are not bound yet. Operational controls stay disabled."
        : AttentionCount > 0
            ? $"{AttentionCount} action{(AttentionCount == 1 ? string.Empty : "s")} require operator attention."
            : GlobalHealth switch
            {
                HealthState.Slow => "SLOW RESPONSE DETECTED · MONITORING ACTIVE · NO ACTION REQUIRED",
                HealthState.Throttled => "THROTTLING DETECTED · NEW SENDS PACED · NO ACTION REQUIRED",
                HealthState.RateLimited => "RATE LIMIT DETECTED · NEW SENDS PAUSED · AUTO RECOVERY ACTIVE · NO ACTION REQUIRED",
                HealthState.Cooldown => "COOLDOWN ACTIVE · AUTO RESUME ENABLED · NO ACTION REQUIRED",
                HealthState.TemporaryError => "TEMPORARY ERROR · AUTO RECOVERY ACTIVE · NO ACTION REQUIRED",
                HealthState.PartialResponse => "PARTIAL RESPONSE · RECONCILIATION ACTIVE · NO ACTION REQUIRED",
                HealthState.Offline => "OFFLINE · RECOVERY WATCH ACTIVE · NO ACTION REQUIRED",
                HealthState.Stuck => "STUCK SESSION DETECTED · RECOVERY ACTIVE · NO ACTION REQUIRED",
                HealthState.Recovering => "AUTO RECOVERY ACTIVE · NO ACTION REQUIRED",
                HealthState.Healthy => "AUTOPILOT · HEALTHY · NO ACTION REQUIRED",
                HealthState.LoginRequired => "LOGIN REQUIRED · OPEN ATTENTION CENTER",
                HealthState.Challenge => "ACCOUNT CHALLENGE · OPEN ATTENTION CENTER",
                HealthState.AdapterUncertain => "NEW SENDS PAUSED · ADAPTER STATE UNCERTAIN · SAFE-FAIL ACTIVE",
                _ => "Runtime state is being evaluated."
            };

    public static RuntimeSnapshot Unbound { get; } = new(
        GatewayBound: false,
        HasActiveRun: false,
        RuntimeStatus: "Integration pending",
        GlobalHealth: HealthState.Unknown,
        AutopilotState: "Unavailable until runtime binding",
        CurrentWave: "—",
        VerifiedCompletion: null,
        ManagerEstimate: null,
        CompletionMode: CompletionMode.Unknown,
        ActiveWorkers: 0,
        P0Count: 0,
        P1Count: 0,
        BlockerCount: 0,
        LoopGuardState: "No runtime evidence",
        LatestManagerHandoff: "No structured Manager handoff has been received.",
        CurrentExecutionFlow: "Waiting for Application / Browser runtime adapter",
        ApiConfigured: false,
        ProviderMode: ProviderMode.BrowserWeb,
        DispatchSettings: DispatchSettingsSummary.ProductDefaults,
        Update: new UpdateSummary("0.1.0", null, "Not checked", "Not started", "Not started", "Not needed", false),
        Projects: Array.Empty<ProjectSummary>(),
        Sessions: Array.Empty<SessionSummary>(),
        Workers: Array.Empty<WorkerSummary>(),
        Tasks: Array.Empty<TaskSummary>(),
        EvidenceGates: Array.Empty<EvidenceGateSummary>(),
        AttentionItems: Array.Empty<AttentionSummary>(),
        RecoveryEvents: Array.Empty<RecoveryEventSummary>());
}
