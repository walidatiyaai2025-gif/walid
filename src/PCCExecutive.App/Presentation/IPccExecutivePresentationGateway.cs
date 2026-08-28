namespace PCCExecutive.App.Presentation;

public enum UiAction
{
    Refresh,
    ResolveProject,
    SelectProject,
    ConnectChrome,
    RetryHealth,
    DisconnectChrome,
    PauseAi,
    ResumeAi,
    RequestManagerPlan,
    StartDispatch,
    PauseDispatch,
    OpenSession,
    BringSessionToFront,
    HideSession,
    RestartSession,
    KillSession,
    KillAllPccSessions,
    ReconcileWave,
    RunVerification,
    InspectLoopGuard,
    ReplanLoop,
    ResumeLoopOnce,
    StopLoop,
    OpenAttentionLocation,
    InstallUpdateAndRestart,
    SaveSettings,
    CheckForUpdates
}

public interface IPccExecutivePresentationGateway
{
    RuntimeSnapshot Snapshot { get; }
    event EventHandler<RuntimeSnapshot>? SnapshotChanged;

    bool CanExecute(UiAction action, string? targetId = null);
    string? DisabledReason(UiAction action, string? targetId = null) =>
        CanExecute(action, targetId) ? null : "Control is unavailable in the current runtime state.";
    Task ExecuteAsync(UiAction action, string? targetId = null, CancellationToken cancellationToken = default);
}
