namespace PCCExecutive.App.Presentation;

public enum UiAction
{
    Refresh,
    ConnectChrome,
    SelectProject,
    PauseAi,
    ResumeAi,
    StartManager,
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
    OpenAttentionLocation,
    InstallUpdateAndRestart,
    SaveSettings,
    CheckForUpdates,
    OpenConversationHistory
}

public interface IPccExecutivePresentationGateway
{
    RuntimeSnapshot Snapshot { get; }
    event EventHandler<RuntimeSnapshot>? SnapshotChanged;

    bool CanExecute(UiAction action, string? targetId = null);
    Task ExecuteAsync(UiAction action, string? targetId = null, CancellationToken cancellationToken = default);
}
