namespace PCCExecutive.App.Presentation;

public sealed record ControlDescriptor(
    string Screen,
    string Control,
    string Command,
    string BackendService,
    string StateSource,
    string EnabledRule,
    string ErrorPath,
    ControlClassification CurrentStatus,
    bool P0 = false);

/// <summary>
/// Machine-checkable census for every visible operational action in the Wave 2 UI.
/// "WiredReal" may still be disabled at runtime when the accepted dependency is absent;
/// in that case CanExecute/DisabledReason expose the exact reason instead of faking success.
/// </summary>
public static class ControlCensus
{
    public static IReadOnlyList<ControlDescriptor> All { get; } =
    [
        C("Shell", "Navigation", "NavigateCommand", "WPF navigation", "SelectedScreen", "Always", "Local UI error", ControlClassification.WiredReal),
        C("Chrome Connection", "Connect / Recover Chrome", nameof(UiAction.ConnectChrome), "Worker 3 BrowserSessionController", "Browser runtime registry", "Browser runtime composed", "Inline UI error", ControlClassification.WiredReal, true),
        C("Chrome Connection", "Open Browser", nameof(UiAction.OpenSession), "Worker 3 BrowserSessionController.OpenAsync", "Manager runtime identity", "Manager runtime exists", "Inline UI error", ControlClassification.WiredReal, true),
        C("Chrome Connection", "Bring to Front", nameof(UiAction.BringSessionToFront), "Worker 3 BrowserSessionController.BringToFrontAsync", "Manager runtime identity", "Manager runtime exists", "Inline UI error", ControlClassification.WiredReal, true),
        C("Chrome Connection", "Retry Health", nameof(UiAction.RetryHealth), "Worker 3 semantic adapter refresh", "ChatGPT semantic snapshot", "Runtime binding composed", "Inline health state", ControlClassification.WiredReal, true),
        C("Project Selection", "Resolve Project", nameof(UiAction.ResolveProject), "PR #7 IProjectControlResolver", "Live PCC routing", "Non-empty project/alias", "ProjectResolutionStatus", ControlClassification.WiredReal, true),
        C("Project Selection", "Open Project", nameof(UiAction.SelectProject), "PR #7 IProjectControlResolver + baseline builder", "Live PCC/GitHub baseline", "Routed project identity", "ProjectResolutionStatus", ControlClassification.WiredReal, true),
        C("Manager Workspace", "Open Manager Chat", nameof(UiAction.OpenSession), "Worker 3 BrowserSessionController.OpenAsync", "Manager runtime identity", "Manager runtime exists", "Inline UI error", ControlClassification.WiredReal, true),
        C("Manager Workspace", "Pause AI", nameof(UiAction.PauseAi), "Worker 1 orchestration command service", "ProjectRun state", "Not yet composed", "Disabled reason", ControlClassification.DisabledWithReason, true),
        C("Manager Workspace", "Resume", nameof(UiAction.ResumeAi), "Worker 1 orchestration command service", "ProjectRun state", "Not yet composed", "Disabled reason", ControlClassification.DisabledWithReason, true),
        C("Manager Workspace", "Request Plan", nameof(UiAction.RequestManagerPlan), "Worker 1 Manager coordinator", "Manager session/wave state", "Not yet composed", "Disabled reason", ControlClassification.DisabledWithReason),
        C("Workers Dispatch", "Start / Resume Dispatch", nameof(UiAction.StartDispatch), "Worker 1 orchestration/dispatch service", "Dispatch scheduler state", "Not yet composed", "Disabled reason", ControlClassification.DisabledWithReason, true),
        C("Workers Dispatch", "Pause Dispatch", nameof(UiAction.PauseDispatch), "Worker 1 orchestration/dispatch service", "Dispatch scheduler state", "Not yet composed", "Disabled reason", ControlClassification.DisabledWithReason, true),
        C("Worker Chat", "Open Chat", nameof(UiAction.OpenSession), "Worker 3 BrowserSessionController.OpenAsync", "Worker runtime identity", "Runtime exists", "Inline UI error", ControlClassification.WiredReal, true),
        C("Worker Chat", "Bring to Front", nameof(UiAction.BringSessionToFront), "Worker 3 BrowserSessionController.BringToFrontAsync", "Worker runtime identity", "Runtime exists", "Inline UI error", ControlClassification.WiredReal),
        C("Worker Chat", "Hide", nameof(UiAction.HideSession), "Worker 3 BrowserSessionController.HideAsync", "Worker runtime identity", "Runtime exists", "Inline UI error", ControlClassification.WiredReal),
        C("Worker Chat", "Restart Session", nameof(UiAction.RestartSession), "Worker 3 BrowserSessionController.RestartAsync", "Worker runtime ownership", "Runtime exists", "Inline UI error", ControlClassification.WiredReal, true),
        C("Worker Chat", "Kill Session", nameof(UiAction.KillSession), "Worker 3 ownership proof + KillAsync", "Positive PCC ownership proof", "CanKill=true", "Confirmation + inline error", ControlClassification.WiredReal, true),
        C("Wave Summary", "Reconcile & Send to Manager", nameof(UiAction.ReconcileWave), "Worker 1 reconciliation service", "Wave/hand-off evidence", "Not yet composed", "Disabled reason", ControlClassification.DisabledWithReason, true),
        C("Task Board", "Worker/Wave/Priority/Blocker filters", "Local filter properties", "Presentation filtering", "Canonical task snapshot", "Always", "No backend mutation", ControlClassification.WiredReal),
        C("Evidence & Verification", "Run Verification", nameof(UiAction.RunVerification), "Completion-gate/evidence service", "Persisted completion gates", "Not yet composed", "Disabled reason", ControlClassification.DisabledWithReason, true),
        C("Loop Guard", "Inspect", nameof(UiAction.InspectLoopGuard), "Worker 1 LoopGuard service", "Loop snapshots", "Not yet composed", "Disabled reason", ControlClassification.DisabledWithReason),
        C("Loop Guard", "Replan", nameof(UiAction.ReplanLoop), "Worker 1 orchestration/LoopGuard service", "Loop decision", "Not yet composed", "Disabled reason", ControlClassification.DisabledWithReason, true),
        C("Loop Guard", "Resume Once", nameof(UiAction.ResumeLoopOnce), "Worker 1 orchestration/LoopGuard service", "Loop decision", "Not yet composed", "Disabled reason", ControlClassification.DisabledWithReason, true),
        C("Loop Guard", "Stop", nameof(UiAction.StopLoop), "Worker 1 orchestration/LoopGuard service", "Loop decision", "Not yet composed", "Disabled reason", ControlClassification.DisabledWithReason, true),
        C("ChatGPT Health & Recovery", "Retry Health", nameof(UiAction.RetryHealth), "Worker 3 semantic adapter refresh", "Semantic health snapshot", "Runtime binding composed", "Inline state/Attention", ControlClassification.WiredReal, true),
        C("Session Monitor", "Open", nameof(UiAction.OpenSession), "Worker 3 BrowserSessionController.OpenAsync", "Runtime registry", "Runtime exists", "Inline UI error", ControlClassification.WiredReal),
        C("Session Monitor", "Front", nameof(UiAction.BringSessionToFront), "Worker 3 BrowserSessionController.BringToFrontAsync", "Runtime registry", "Runtime exists", "Inline UI error", ControlClassification.WiredReal),
        C("Session Monitor", "Hide", nameof(UiAction.HideSession), "Worker 3 BrowserSessionController.HideAsync", "Runtime registry", "Runtime exists", "Inline UI error", ControlClassification.WiredReal),
        C("Session Monitor", "Restart", nameof(UiAction.RestartSession), "Worker 3 BrowserSessionController.RestartAsync", "Runtime registry/ownership", "Runtime exists", "Inline UI error", ControlClassification.WiredReal, true),
        C("Session Monitor", "Kill", nameof(UiAction.KillSession), "Worker 3 IOwnershipProofService + KillAsync", "Positive PCC ownership proof", "CanKill=true only", "Confirmation + inline error", ControlClassification.WiredReal, true),
        C("Session Monitor", "Kill All PCC Sessions", nameof(UiAction.KillAllPccSessions), "Worker 3 KillAllPccSessionsAsync", "Positive ownership proofs", "At least one proven owned runtime", "Confirmation; unproven sessions skipped", ControlClassification.WiredReal, true),
        C("Settings", "Provider controls", "SelectedProviderMode", "Worker 2 durable settings service", "Persisted settings", "Read-only until persistence composed", "Disabled reason", ControlClassification.DisabledWithReason),
        C("Settings", "Save Settings", nameof(UiAction.SaveSettings), "Worker 2 durable settings repository", "Persisted settings", "Worker 2 not integrated", "Disabled reason", ControlClassification.DisabledWithReason, true),
        C("Update Center", "Check for Updates", nameof(UiAction.CheckForUpdates), "Configured update manifest source", "PCC_EXECUTIVE_UPDATE_MANIFEST", "Manifest source exists", "Disabled/manifest-invalid state", ControlClassification.WiredReal),
        C("Update Center", "Install Update & Restart", nameof(UiAction.InstallUpdateAndRestart), "Worker 5 staged-install execution contract", "Verified package/backup/migration readiness", "Not composed", "Disabled reason", ControlClassification.DisabledWithReason, true),
        C("Attention Center", "Open Exact Place", nameof(UiAction.OpenAttentionLocation), "Worker 3 session foreground action", "Active attention runtime identity", "Attention item still active", "Inline UI error", ControlClassification.WiredReal, true)
    ];

    public static IReadOnlyList<ControlDescriptor> UnresolvedP0 =>
        All.Where(x => x.P0 && x.CurrentStatus is not (ControlClassification.WiredReal or ControlClassification.DisabledWithReason)).ToArray();

    private static ControlDescriptor C(
        string screen, string control, string command, string backend, string state, string enabled,
        string error, ControlClassification status, bool p0 = false) =>
        new(screen, control, command, backend, state, enabled, error, status, p0);
}
