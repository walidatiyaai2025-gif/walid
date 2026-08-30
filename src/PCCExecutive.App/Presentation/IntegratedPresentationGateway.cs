using PCCExecutive.Application;
using PCCExecutive.Browser;
using PCCExecutive.Domain;
using PCCExecutive.GitHub;
using PCCExecutive.Infrastructure;
using PCCExecutive.Pcc;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PCCExecutive.App.Presentation;

public sealed class PccExecutiveRuntimeHost : IPccExecutivePresentationGateway, IAsyncDisposable
{
    private readonly SqliteStateStore _store;
    private ProjectRunLock? _projectLock;
    private readonly IProjectControlResolver _pcc;
    private readonly IProjectBaselineBuilder _baseline;
    private readonly BrowserSessionController _sessions;
    private readonly IBrowserRuntimeRegistry _runtimeRegistry;
    private readonly IOwnershipProofService _ownership;
    private readonly INewSendPausePort _newSendPause;
    private readonly IAgentProvider _agentProvider;
    private readonly IChatGptBrowserAdapter _browserAdapter;
    private readonly GlobalBrowserSendGate _sendGate;
    private readonly CrashConsistentOrchestrationStore _orchestrationStore;
    private readonly ICanonicalDispatchReservationService _dispatchReservations;
    private readonly RuntimeRecoveryLeaseCoordinator _recoveryLeases = new();
    private readonly AutonomousNextActionRouter _nextActionRouter = new(new GuidedExecutionEvaluator());
    private AutonomousConversationRolloverRuntime? _rolloverRuntime;
    private readonly HttpClient _pccHttp;
    private readonly HttpClient _githubHttp;
    private readonly List<RecoveryEventSummary> _recovery = [];
    private ProjectRun? _run;
    private string? _projectControlId;
    private string _projectDisplay = "No project selected";
    private string _projectRepository = "Not resolved";
    private LogicalAgentId? _managerAgentId;
    private LogicalAgentId[] _workerAgentIds = [];
    private string _pccState = "NOT_CHECKED";
    private string _githubState = "NOT_CHECKED";
    private string _autopilot = "READY";
    private PccExecutiveSettings _settings;
    private string _latestManagerHandoff = "Select a project, connect Chrome, then start Manager.";
    private StructuredManagerPlan? _currentPlan;
    private Wave? _currentWave;
    private IReadOnlyDictionary<TaskId, WorkerSlotId> _assignments = new Dictionary<TaskId, WorkerSlotId>();
    private IReadOnlyList<WorkerTask> _runtimeTasks = [];
    private readonly Dictionary<string, (AttentionSummary Summary, string RuntimeId)> _attention = new(StringComparer.Ordinal);
    private ProjectBaselineSnapshot? _managerBaseline;
    private readonly CancellationTokenSource _autopilotCancellation = new();
    private readonly SemaphoreSlim _autopilotOperation = new(1, 1);
    private Task? _autopilotTask;
    private readonly Queue<string> _recentPlanFingerprints = new();
    private readonly Queue<decimal> _recentVerifiedCompletion = new();
    private string? _runtimeHealthFault;
    private string? _runtimeErrorFingerprint;
    private int _runtimeErrorCount;
    private IReadOnlyList<ConversationHistorySummary> _conversationHistory = [];
    private DateTimeOffset _nextExternalEvidenceRetryAt = DateTimeOffset.MinValue;
    private DateTimeOffset _nextChromeRecoveryRetryAt = DateTimeOffset.MinValue;

    private PccExecutiveRuntimeHost(
        SqliteStateStore store,
        ProjectRunLock? projectLock,
        IProjectControlResolver pcc,
        IProjectBaselineBuilder baseline,
        BrowserSessionController sessions,
        IBrowserRuntimeRegistry runtimeRegistry,
        IOwnershipProofService ownership,
        INewSendPausePort newSendPause,
        IAgentProvider agentProvider,
        IChatGptBrowserAdapter browserAdapter,
        GlobalBrowserSendGate sendGate,
        HttpClient pccHttp,
        HttpClient githubHttp,
        ProjectRun? run)
    {
        _store = store;
        _projectLock = projectLock;
        _pcc = pcc;
        _baseline = baseline;
        _sessions = sessions;
        _runtimeRegistry = runtimeRegistry;
        _ownership = ownership;
        _newSendPause = newSendPause;
        _agentProvider = agentProvider;
        _browserAdapter = browserAdapter;
        _sendGate = sendGate;
        _orchestrationStore = new CrashConsistentOrchestrationStore(store);
        _dispatchReservations = new CanonicalDispatchReservationService(store);
        _pccHttp = pccHttp;
        _githubHttp = githubHttp;
        _run = run;
        if (run is not null)
        {
            _managerAgentId = AgentId(run.Id, "manager");
            _workerAgentIds = Enumerable.Range(1, 5).Select(slot => AgentId(run.Id, $"worker:{slot}")).ToArray();
            var startupRecovery = new DurableStartupRecoveryService(store, _orchestrationStore);
            var startupKind = startupRecovery.BeginStartupAsync(run.Id).GetAwaiter().GetResult();
            var recovered = startupRecovery.ReconstructAsync(run.Id).GetAwaiter().GetResult();
            _recovery.Insert(0, new RecoveryEventSummary(DateTimeOffset.UtcNow, startupKind.ToString(), "Durable startup recovery and dispatch-fence reconciliation completed before AutoResume.", true));
            if (recovered is not null)
            {
                _run = recovered.ProjectRun;
                _currentWave = recovered.CurrentWave;
                _assignments = recovered.Assignments;
                _runtimeTasks = recovered.Tasks;
                _autopilot = MapRecoveredPhaseToAutopilot(recovered.Phase, recovered.CurrentWave);
            }
            var planCheckpoint = store.LoadCheckpointAsync($"manager-plan:{run.Id}").GetAwaiter().GetResult();
            if (planCheckpoint is not null)
            {
                var parsed = new StructuredManagerPlanParser().Parse(planCheckpoint.Payload);
                if (parsed.IsValid) _currentPlan = parsed.Plan;
            }
            var baselineCheckpoint = store.LoadCheckpointAsync($"manager-baseline:{run.Id}").GetAwaiter().GetResult();
            if (baselineCheckpoint is not null) _managerBaseline = JsonSerializer.Deserialize<ProjectBaselineSnapshot>(baselineCheckpoint.Payload);
        }
        _settings = store.LoadSettingsAsync().GetAwaiter().GetResult();
        if (run is not null)
        {
            var pause = store.LoadCheckpointAsync($"autopilot-pause:{run.Id}").GetAwaiter().GetResult();
            if (pause is not null && pause.Payload.Contains("\"paused\":true", StringComparison.Ordinal))
            {
                _autopilot = "PAUSED";
                _newSendPause.PauseNewSendsAsync("Restored persisted operator pause.").GetAwaiter().GetResult();
            }
            var healthCheckpoint = store.LoadCheckpointAsync($"runtime-health:{run.Id}").GetAwaiter().GetResult();
            if (healthCheckpoint is not null)
            {
                var health = JsonSerializer.Deserialize<DurableRuntimeHealth>(healthCheckpoint.Payload);
                if (health?.Active == true)
                {
                    _runtimeHealthFault = health.State;
                    var now = DateTimeOffset.UtcNow;
                    var cooldown = health.ResumeNotBefore is null ? (TimeSpan?)null : health.ResumeNotBefore <= now ? TimeSpan.Zero : health.ResumeNotBefore.Value - now;
                    _sendGate.Apply(new ResilienceDecision(ParseResilienceState(health.State), FaultScope.Global, true, health.RequiresHumanAction, health.Reason), now, cooldown);
                    if (_autopilot != "PAUSED") _autopilot = health.State;
                }
            }
            var loopCheckpoint = store.LoadCheckpointAsync($"loop-guard:{run.Id}").GetAwaiter().GetResult();
            if (loopCheckpoint is not null)
            {
                var loop = JsonSerializer.Deserialize<DurableLoopGuard>(loopCheckpoint.Payload);
                if (loop is not null)
                {
                    foreach (var fingerprint in loop.PlanFingerprints.TakeLast(3)) _recentPlanFingerprints.Enqueue(fingerprint);
                    foreach (var completion in loop.VerifiedCompletion.TakeLast(3)) _recentVerifiedCompletion.Enqueue(completion);
                    _runtimeErrorFingerprint = loop.RuntimeErrorFingerprint;
                    _runtimeErrorCount = loop.RuntimeErrorCount;
                    if (loop.AutoStopped)
                    {
                        var recoverablePrePlanRuntimeStall = _currentPlan is null && loop.RuntimeErrorCount >= 3 && _settings.AutoResume;
                        if (recoverablePrePlanRuntimeStall)
                        {
                            _run = _run is null ? null : _run with { State = ProjectRunState.ManagerPlanning };
                            _runtimeErrorFingerprint = null;
                            _runtimeErrorCount = 0;
                            var hasReceivedManagerResponseFailure =
                                loop.RuntimeErrorFingerprint?.Contains("Manager response rejected:", StringComparison.OrdinalIgnoreCase) == true ||
                                loop.RuntimeErrorFingerprint?.Contains("Manager wave rejected:", StringComparison.OrdinalIgnoreCase) == true ||
                                loop.RuntimeErrorFingerprint?.Contains("MANAGER_PLAN_", StringComparison.OrdinalIgnoreCase) == true;
                            var prePlanRecovery = hasReceivedManagerResponseFailure
                                ? PrePlanAutoRecoveryMode.ExistingManagerResponse
                                : PrePlanAutoRecoveryPolicy.Classify(loop.RuntimeErrorFingerprint);
                            _autopilot = prePlanRecovery == PrePlanAutoRecoveryMode.ExistingManagerResponse ? "PLANNING" : "RECOVERING";
                            _latestManagerHandoff = prePlanRecovery == PrePlanAutoRecoveryMode.ExistingManagerResponse
                                ? "RECOVERING_MANAGER_RESPONSE — reparsing the already-received Manager response with the current schema; no resend will occur."
                                : "RECOVERING_EVIDENCE — retrying the previous pre-plan infrastructure failure automatically.";
                            if (_run is not null) store.SaveProjectRunAsync(_run).GetAwaiter().GetResult();
                            store.SaveCheckpointAsync(new DurableCheckpoint($"loop-guard:{run.Id}", run.Id.ToString(), "loop-guard-v2", JsonSerializer.Serialize(new DurableLoopGuard(loop.PlanFingerprints, loop.VerifiedCompletion, null, 0, false)), DateTimeOffset.UtcNow)).GetAwaiter().GetResult();
                        }
                        else
                        {
                            _run = _run is null ? null : _run with { State = ProjectRunState.StalledAutoStopped };
                            _autopilot = "STALLED";
                        }
                    }
                }
            }
        }
        NormalizeRecoveredAutopilotState();
        Snapshot = BuildSnapshot(Array.Empty<BrowserRuntimeRecord>(), new HashSet<string>(StringComparer.Ordinal));
    }

    public RuntimeSnapshot Snapshot { get; private set; }
    public event EventHandler<RuntimeSnapshot>? SnapshotChanged;

    public static PccExecutiveRuntimeHost Create()
    {
        try
        {
            var store = new SqliteStateStore(SqliteStateStore.DefaultDatabasePath);
            store.InitializeAsync().GetAwaiter().GetResult();
            var settings = store.LoadSettingsAsync().GetAwaiter().GetResult();
            if (!string.Equals(settings.Provider, "BrowserChat", StringComparison.Ordinal))
                store.SaveSettingsAsync(settings with { Provider = "BrowserChat" }).GetAwaiter().GetResult();

            ProjectRun? run = null;
            ProjectRunLock? projectLock = null;
            SelectedProjectState? selected = null;
            var selectedCheckpoint = store.LoadCheckpointAsync("active-project").GetAwaiter().GetResult();
            if (selectedCheckpoint is not null)
            {
                selected = JsonSerializer.Deserialize<SelectedProjectState>(selectedCheckpoint.Payload);
                if (selected is not null)
                {
                    run = store.LoadProjectRunAsync(new ProjectRunId(selected.ProjectRunId)).GetAwaiter().GetResult();
                    if (run is not null)
                    {
                        projectLock = ProjectRunLock.TryAcquire(selected.ProjectIdentity);
                        if (!projectLock.IsOwned)
                            throw new InvalidOperationException($"PCC Executive is already controlling project '{selected.ProjectControlId}' on this machine.");
                    }
                }
            }

            var profileRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCC Executive", "browser-profiles");
            var markerStore = new FileOwnershipMarkerStore();
            var processInspector = new SystemProcessInspector();
            IOwnershipProofService ownership = new OwnershipProofService(profileRoot, markerStore, processInspector);
            var runtimeHost = new PlaywrightChromeRuntimeHost(profileRoot);
            IBrowserRuntimeRegistry registry = store;
            var diagnosticStore = new InMemoryRuntimeDiagnosticStore();
            IRuntimeDiagnosticCollector diagnostics = new RuntimeDiagnosticCollector(diagnosticStore, diagnosticStore);
            var controller = new BrowserSessionController(registry, runtimeHost, ownership, markerStore, processInspector, new BrowserRecoveryDiagnosticSink(diagnostics));

            var adapter = new PlaywrightChatGptBrowserAdapter(runtimeHost);
            var sendGate = new GlobalBrowserSendGate();
            var browserProvider = new PCCExecutive.Browser.BrowserChatProvider(registry, adapter, store, new WrongChatGuard(), sendGate, ownership);
            IAgentProvider agentProvider = new BrowserAgentProviderAdapter(registry, browserProvider, ownership);
            INewSendPausePort newSendPause = new BrowserNewSendPausePort(sendGate);

            var pccHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var githubHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var pcc = new PccProjectControlResolver(new GitHubPccDocumentSource(pccHttp));
            var github = new GitHubRestEvidenceClient(githubHttp);
            var baseline = new ProjectBaselineBuilder(pcc, github);

            var gateway = new PccExecutiveRuntimeHost(store, projectLock, pcc, baseline, controller, registry, ownership, newSendPause, agentProvider, adapter, sendGate, pccHttp, githubHttp, run);
            if (selected is not null && run is not null)
            {
                gateway._projectControlId = selected.ProjectControlId;
                gateway._projectDisplay = selected.DisplayName;
                gateway._projectRepository = selected.Repository;
                PersistLogicalAgents(store, run.Id, gateway._managerAgentId!.Value, gateway._workerAgentIds);
            }
            if (run is not null) gateway.RecoverStartupBrowserStateAsync().GetAwaiter().GetResult();
            gateway._rolloverRuntime = AutonomousConversationRolloverRuntime.Attach(gateway);
            gateway.RefreshLocalSnapshotAsync().GetAwaiter().GetResult();
            if (run is not null && gateway._settings.AutoResume && gateway._autopilot != "PAUSED" && gateway._autopilot != "RECOVERY_REQUIRED") gateway.EnsureAutopilotLoop();
            return gateway;
        }
        catch
        {
            throw;
        }
    }

    public bool CanExecute(UiAction action, string? targetId = null) => action switch
    {
        UiAction.SelectProject or UiAction.SaveSettings => true,
        UiAction.Refresh or UiAction.RunVerification => _run is not null,
        UiAction.ConnectChrome or UiAction.PauseAi or UiAction.ResumeAi => _run is not null,
        UiAction.StartManager => _run is not null && Snapshot.Sessions.Any(x => x.Role == "Manager" && x.IsPccOwned),
        UiAction.StartDispatch => _run is not null && _currentPlan is not null && _currentWave?.State == WaveState.Ready && _autopilot != "PAUSED",
        UiAction.PauseDispatch => _run is not null && _currentWave?.State is WaveState.Dispatching or WaveState.Running,
        UiAction.ReconcileWave => _run is not null && Snapshot.Sessions.Any(x => x.Role == "Manager" && x.IsPccOwned),
        UiAction.OpenAttentionLocation => !string.IsNullOrWhiteSpace(targetId) && _attention.TryGetValue(targetId, out var attention) && Snapshot.Sessions.Any(x => x.RuntimeId == attention.RuntimeId && x.IsPccOwned),
        UiAction.OpenConversationHistory => _run is not null && !string.IsNullOrWhiteSpace(targetId),
        UiAction.OpenSession or UiAction.BringSessionToFront or UiAction.HideSession or UiAction.RestartSession or UiAction.KillSession =>
            _run is not null && !string.IsNullOrWhiteSpace(targetId) && Snapshot.Sessions.Any(x => StringComparer.Ordinal.Equals(x.RuntimeId, targetId) && x.IsPccOwned),
        UiAction.KillAllPccSessions => _run is not null && Snapshot.Sessions.Any(x => x.IsPccOwned),
        _ => false
    };

    public async Task ExecuteAsync(UiAction action, string? targetId = null, CancellationToken cancellationToken = default)
    {
        switch (action)
        {
            case UiAction.Refresh:
                await RefreshExternalAsync(_projectControlId ?? throw new InvalidOperationException("Select a project before refreshing it."), cancellationToken).ConfigureAwait(false);
                break;
            case UiAction.SelectProject:
                await RefreshExternalAsync(targetId ?? throw new InvalidOperationException("A PCC project name or alias is required."), cancellationToken).ConfigureAwait(false);
                break;
            case UiAction.ConnectChrome:
                await ConnectManagerChromeAsync(cancellationToken).ConfigureAwait(false);
                break;
            case UiAction.PauseAi:
                var pausedRun = RequireActiveRun();
                await _newSendPause.PauseNewSendsAsync("Operator paused AI from PCC Executive.", cancellationToken).ConfigureAwait(false);
                await _store.SaveCheckpointAsync(new DurableCheckpoint($"autopilot-pause:{pausedRun.Id}", pausedRun.Id.ToString(), "autopilot-pause-v1", "{\"paused\":true}", DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
                _autopilot = "PAUSED";
                await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
                break;
            case UiAction.ResumeAi:
                var resumedRun = RequireActiveRun();
                if (_runtimeHealthFault is not null && !await TryResumeAfterFreshSemanticHealthAsync(cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException("Global Browser sends remain blocked because fresh semantic health is not proven safe.");
                await _newSendPause.ResumeNewSendsAsync("Operator resumed AI from PCC Executive.", cancellationToken).ConfigureAwait(false);
                await _store.SaveCheckpointAsync(new DurableCheckpoint($"autopilot-pause:{resumedRun.Id}", resumedRun.Id.ToString(), "autopilot-pause-v1", "{\"paused\":false}", DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
                await _store.SaveCheckpointAsync(new DurableCheckpoint($"dispatch-pause:{resumedRun.Id}", resumedRun.Id.ToString(), "dispatch-pause-v1", "{\"paused\":false}", DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
                _autopilot = "READY";
                await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
                break;
            case UiAction.StartManager:
                await StartManagerAsync(cancellationToken).ConfigureAwait(false);
                break;
            case UiAction.StartDispatch:
                await StartDispatchAsync(cancellationToken).ConfigureAwait(false);
                break;
            case UiAction.PauseDispatch:
                await PauseDispatchAsync(cancellationToken).ConfigureAwait(false);
                break;
            case UiAction.ReconcileWave:
                if (_currentWave?.State == WaveState.Running)
                    await ReconcileWorkerResponsesAsync(cancellationToken).ConfigureAwait(false);
                else
                    await ReconcileManagerResponseAsync(cancellationToken).ConfigureAwait(false);
                break;
            case UiAction.OpenAttentionLocation:
                if (string.IsNullOrWhiteSpace(targetId) || !_attention.TryGetValue(targetId, out var attention)) throw new InvalidOperationException("A valid Attention target is required.");
                await RunSessionActionAsync(attention.RuntimeId, id => _sessions.BringToFrontAsync(id, cancellationToken), cancellationToken).ConfigureAwait(false);
                break;
            case UiAction.OpenConversationHistory:
                await LoadConversationHistoryAsync(targetId, cancellationToken).ConfigureAwait(false);
                break;
            case UiAction.OpenSession:
                await RunSessionActionAsync(targetId, id => _sessions.OpenAsync(id, cancellationToken), cancellationToken).ConfigureAwait(false);
                break;
            case UiAction.BringSessionToFront:
                await RunSessionActionAsync(targetId, id => _sessions.BringToFrontAsync(id, cancellationToken), cancellationToken).ConfigureAwait(false);
                break;
            case UiAction.HideSession:
                await RunSessionActionAsync(targetId, id => _sessions.HideAsync(id, cancellationToken), cancellationToken).ConfigureAwait(false);
                break;
            case UiAction.RestartSession:
                await RunSessionActionAsync(targetId, id => _sessions.RestartAsync(id, cancellationToken), cancellationToken).ConfigureAwait(false);
                break;
            case UiAction.KillSession:
                await RunSessionActionAsync(targetId, id => _sessions.KillAsync(id, cancellationToken), cancellationToken).ConfigureAwait(false);
                break;
            case UiAction.KillAllPccSessions:
                RequireActiveRun();
                await _sessions.KillAllPccSessionsAsync(cancellationToken).ConfigureAwait(false);
                await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
                break;
            case UiAction.SaveSettings:
                _settings = ParseSettings(targetId, _settings);
                await _store.SaveSettingsAsync(_settings, cancellationToken).ConfigureAwait(false);
                await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
                break;
            case UiAction.RunVerification:
                await RunVerificationAsync(cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new InvalidOperationException($"UI action '{action}' is not bound to an integrated runtime operation.");
        }
    }

    private ProjectRun RequireActiveRun() =>
        _run ?? throw new InvalidOperationException("Select and resolve a project before using project runtime controls.");

    private async Task PersistGlobalHealthPauseAsync(ResilienceDecision resilience, string runtimeId, CancellationToken cancellationToken)
    {
        var run = RequireActiveRun();
        var cooldown = resilience.RequiresHumanAction ? (TimeSpan?)null : resilience.State == ChatGptResilienceState.RateLimited ? new ConservativeCooldownPolicy().GetCooldown(1) : TimeSpan.FromSeconds(30);
        _sendGate.Apply(resilience with { Scope = FaultScope.Global, PauseUnsafeNewSends = true }, DateTimeOffset.UtcNow, cooldown);
        _runtimeHealthFault = resilience.State.ToString().ToUpperInvariant();
        _autopilot = _runtimeHealthFault;
        var durable = new DurableRuntimeHealth(true, _runtimeHealthFault, resilience.Reason, _sendGate.Snapshot.ResumeNotBefore, resilience.RequiresHumanAction, runtimeId);
        await _store.SaveCheckpointAsync(new DurableCheckpoint($"runtime-health:{run.Id}", run.Id.ToString(), "runtime-health-v2", JsonSerializer.Serialize(durable), DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> TryResumeAfterFreshSemanticHealthAsync(CancellationToken cancellationToken)
    {
        var run = RequireActiveRun();
        if (_runtimeHealthFault is null) return true;
        var gate = _sendGate.Snapshot;
        if (gate.ResumeNotBefore is not null && gate.ResumeNotBefore > DateTimeOffset.UtcNow) return false;
        var runtimes = (await _runtimeRegistry.ListAsync(cancellationToken).ConfigureAwait(false))
            .Where(x => !x.IsArchived && x.State is not BrowserSessionState.Killed && StringComparer.Ordinal.Equals(x.ProjectRunId, run.Id.ToString()) && !string.IsNullOrWhiteSpace(x.TaskId) && !string.IsNullOrWhiteSpace(x.ConversationIdentity) && !string.IsNullOrWhiteSpace(x.ProviderConversationIdentity))
            .ToArray();
        if (runtimes.Length == 0) return false;
        foreach (var runtime in runtimes)
        {
            var expected = new BrowserDispatchExpectation(run.Id.ToString(), runtime.LogicalAgentId, runtime.TaskId!, runtime.ConversationIdentity!, runtime.ProviderConversationIdentity!, runtime.WorkerSlotId);
            var semantic = await _browserAdapter.InspectAsync(runtime, expected, cancellationToken).ConfigureAwait(false);
            if (semantic.Auth.State != AuthState.Authenticated || semantic.Health.State != PageHealth.Healthy) return false;
        }
        await _newSendPause.ResumeNewSendsAsync("Fresh semantic Browser health proved safe after durable global fault.", cancellationToken).ConfigureAwait(false);
        _runtimeHealthFault = null;
        await _store.SaveCheckpointAsync(new DurableCheckpoint($"runtime-health:{run.Id}", run.Id.ToString(), "runtime-health-v2", JsonSerializer.Serialize(new DurableRuntimeHealth(false, "READY", "Fresh semantic health proven.", null, false, null)), DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static string MapRecoveredPhaseToAutopilot(OrchestrationPhase phase, Wave? wave) => phase switch
    {
        OrchestrationPhase.Initializing => "READY",
        OrchestrationPhase.ManagerPlanning => "PLANNING",
        OrchestrationPhase.WaveValidation => wave?.State == WaveState.Ready ? "READY_TO_DISPATCH" : "PLANNING",
        OrchestrationPhase.Dispatching => wave?.State == WaveState.Running ? "WAITING_WORKERS" : wave?.State == WaveState.Ready ? "READY_TO_DISPATCH" : "RECOVERING",
        OrchestrationPhase.WaveRunning => "WAITING_WORKERS",
        OrchestrationPhase.Reconciling => "WAITING_WORKERS",
        OrchestrationPhase.ManagerReview => "MANAGER_REVIEW",
        OrchestrationPhase.ClosureMode => "CLOSURE_VERIFY",
        OrchestrationPhase.VerifiedComplete => "VERIFIED_COMPLETE",
        OrchestrationPhase.BlockedExternal => "RECOVERING",
        OrchestrationPhase.StalledAutoStopped => "STALLED",
        OrchestrationPhase.StoppedByOperator => "PAUSED",
        _ => "RECOVERING"
    };

    private void NormalizeRecoveredAutopilotState()
    {
        if (_run is null) return;

        _autopilot = _autopilot switch
        {
            "INITIALIZING" => MapRecoveredPhaseToAutopilot(OrchestrationPhase.Initializing, _currentWave),
            "MANAGERPLANNING" => MapRecoveredPhaseToAutopilot(OrchestrationPhase.ManagerPlanning, _currentWave),
            "WAVEVALIDATION" => MapRecoveredPhaseToAutopilot(OrchestrationPhase.WaveValidation, _currentWave),
            "DISPATCHING" => MapRecoveredPhaseToAutopilot(OrchestrationPhase.Dispatching, _currentWave),
            "WAVERUNNING" => MapRecoveredPhaseToAutopilot(OrchestrationPhase.WaveRunning, _currentWave),
            "RECONCILING" => MapRecoveredPhaseToAutopilot(OrchestrationPhase.Reconciling, _currentWave),
            "MANAGERREVIEW" => MapRecoveredPhaseToAutopilot(OrchestrationPhase.ManagerReview, _currentWave),
            "CLOSUREMODE" => MapRecoveredPhaseToAutopilot(OrchestrationPhase.ClosureMode, _currentWave),
            "VERIFIEDCOMPLETE" => MapRecoveredPhaseToAutopilot(OrchestrationPhase.VerifiedComplete, _currentWave),
            "BLOCKEDEXTERNAL" => MapRecoveredPhaseToAutopilot(OrchestrationPhase.BlockedExternal, _currentWave),
            "STALLEDAUTOSTOPPED" => MapRecoveredPhaseToAutopilot(OrchestrationPhase.StalledAutoStopped, _currentWave),
            "STOPPEDBYOPERATOR" => MapRecoveredPhaseToAutopilot(OrchestrationPhase.StoppedByOperator, _currentWave),
            _ => _autopilot
        };

        if (_autopilot == "READY")
        {
            _autopilot = _run.State switch
            {
                ProjectRunState.Initializing => "READY",
                ProjectRunState.ManagerPlanning => "PLANNING",
                ProjectRunState.WaveReady when _currentPlan is not null && _currentWave is { State: WaveState.Ready } => "READY_TO_DISPATCH",
                ProjectRunState.Dispatching when _currentWave is { State: WaveState.Running or WaveState.Dispatching } => "WAITING_WORKERS",
                ProjectRunState.WaveRunning => "WAITING_WORKERS",
                ProjectRunState.Reconciling => "WAITING_WORKERS",
                ProjectRunState.ManagerReview => "MANAGER_REVIEW",
                ProjectRunState.ClosureMode => "CLOSURE_VERIFY",
                ProjectRunState.VerifiedComplete => "VERIFIED_COMPLETE",
                ProjectRunState.BlockedExternal => "RECOVERING",
                ProjectRunState.StalledAutoStopped => RecoverStalledManagerResponseState(),
                ProjectRunState.StoppedByOperator => "PAUSED",
                _ => _autopilot
            };
        }

        var durablePlanWaveGap =
            (_currentPlan is not null && _currentWave is null) ||
            (_currentPlan is null && _currentWave is { State: WaveState.Ready });
        if (durablePlanWaveGap && (_autopilot is "READY" or "RECOVERING" or "READY_TO_DISPATCH"))
        {
            _autopilot = "PLANNING";
            _latestManagerHandoff = "RECOVERING_MANAGER_RESPONSE — durable Manager plan/wave state is incomplete; PCC is re-reading and revalidating the already-received Manager response without sending a duplicate prompt.";
        }
    }

    private string RecoverStalledManagerResponseState()
    {
        if (!_settings.AutoResume) return "STALLED";
        if (_currentPlan is not null && _currentWave is { State: WaveState.Ready }) return "READY_TO_DISPATCH";
        if (_currentPlan is not null && _currentWave is { State: WaveState.Running or WaveState.Dispatching }) return "WAITING_WORKERS";

        var managerResponseFailure =
            _runtimeErrorFingerprint?.Contains("Manager response rejected:", StringComparison.OrdinalIgnoreCase) == true ||
            _runtimeErrorFingerprint?.Contains("Manager wave rejected:", StringComparison.OrdinalIgnoreCase) == true ||
            _runtimeErrorFingerprint?.Contains("MANAGER_PLAN_", StringComparison.OrdinalIgnoreCase) == true;

        // Builds before this fix persisted the same unaccepted response three times and
        // then marked the run STALLED. No accepted plan checkpoint exists in that case.
        var legacyUnacceptedResponseSelfStall =
            _currentPlan is null &&
            _recentPlanFingerprints.Count >= 3 &&
            _recentPlanFingerprints.Distinct(StringComparer.Ordinal).Count() == 1;

        if (!managerResponseFailure && !legacyUnacceptedResponseSelfStall) return "STALLED";

        if (legacyUnacceptedResponseSelfStall)
            _recentPlanFingerprints.Clear();
        _run = _run is null ? null : _run with { State = ProjectRunState.ManagerPlanning };
        _runtimeErrorFingerprint = null;
        _runtimeErrorCount = 0;
        _latestManagerHandoff = "RECOVERING_MANAGER_RESPONSE — retrying the already-received Manager response after recovery; no duplicate Manager prompt will be sent.";
        return "PLANNING";
    }

    private async Task PersistLoopGuardAsync(bool autoStopped, CancellationToken cancellationToken)
    {
        if (_run is null) return;
        var state = new DurableLoopGuard(_recentPlanFingerprints.ToArray(), _recentVerifiedCompletion.ToArray(), _runtimeErrorFingerprint, _runtimeErrorCount, autoStopped);
        await _store.SaveCheckpointAsync(new DurableCheckpoint($"loop-guard:{_run.Id}", _run.Id.ToString(), "loop-guard-v2", JsonSerializer.Serialize(state), DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
    }

    private async Task RecordRuntimeLoopErrorAsync(InvalidOperationException error, CancellationToken cancellationToken)
    {
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{error.GetType().FullName}|{error.Message}"))).ToLowerInvariant();
        if (StringComparer.Ordinal.Equals(_runtimeErrorFingerprint, fingerprint)) _runtimeErrorCount++;
        else
        {
            _runtimeErrorFingerprint = fingerprint;
            _runtimeErrorCount = 1;
        }

        // Never let the autonomous loop fail silently. Surface the exact current failure on the
        // canonical snapshot immediately so the always-visible LIVE STATUS strip tells the owner
        // what PCC is retrying and why.
        _latestManagerHandoff = $"AUTOPILOT RETRY {_runtimeErrorCount}/3 — {error.Message}";
        _recovery.Insert(0, new RecoveryEventSummary(DateTimeOffset.UtcNow, "AUTOPILOT RETRY", error.Message, true));

        if (_runtimeErrorCount >= 3 && _run is not null)
        {
            _run = _run with { State = ProjectRunState.StalledAutoStopped };
            _autopilot = "STALLED";
            _latestManagerHandoff = $"STALLED_AUTO_STOPPED — repeated runtime error: {error.Message}";
            await _orchestrationStore.SaveAsync(new OrchestrationRecoverySnapshot(_run, _currentWave, _runtimeTasks, _assignments, [], null, OrchestrationPhase.StalledAutoStopped, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
            await PersistLoopGuardAsync(true, cancellationToken).ConfigureAwait(false);
            await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        await PersistLoopGuardAsync(false, cancellationToken).ConfigureAwait(false);
        await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task PersistAgentBindingAsync(LogicalAgentId agentId, WorkerSlotId? slot, TaskId? taskId, ConversationId conversationId, CancellationToken cancellationToken)
    {
        var run = RequireActiveRun();
        var role = slot is null ? AgentRole.Manager : AgentRole.Worker;
        var existing = await _store.LoadLogicalAgentAsync(agentId, cancellationToken).ConfigureAwait(false);
        var session = existing is null
            ? new LogicalAgentSession(agentId, run.Id, role, slot, taskId, conversationId, LogicalSessionState.Active)
            : existing with { WorkerSlotId = slot ?? existing.WorkerSlotId, CurrentTaskId = taskId ?? existing.CurrentTaskId, CurrentConversationId = conversationId, State = LogicalSessionState.Active };
        await _store.SaveLogicalAgentAsync(session, cancellationToken).ConfigureAwait(false);
    }

    private async Task RecoverStartupBrowserStateAsync(CancellationToken cancellationToken = default)
    {
        if (_run is null) return;
        var runId = _run.Id.ToString();
        var fingerprint = $"startup:{runId}";
        if (!_recoveryLeases.TryAcquire(runId, fingerprint, out var lease)) return;
        using (lease)
        {
            _autopilot = "RECOVERING";
            var result = await new BrowserStartupRecoveryCoordinator(_runtimeRegistry, _sessions)
                .ReconcileAsync(runId, cancellationToken).ConfigureAwait(false);
            foreach (var reconciliation in result.Reconciliations)
            {
                var browserState = reconciliation.Succeeded ? BrowserRecoveryState.Ready
                    : reconciliation.Reason.Contains("LOGIN", StringComparison.OrdinalIgnoreCase) ? BrowserRecoveryState.LoginRequired
                    : reconciliation.Reason.Contains("OWNERSHIP", StringComparison.OrdinalIgnoreCase) ? BrowserRecoveryState.OwnershipUncertain
                    : BrowserRecoveryState.RecoveryFailed;
                var routing = _nextActionRouter.Route(
                    new GuidedRuntimeState(true, true, browserState, _projectControlId is not null, true, true,
                        _managerAgentId is not null, _currentPlan is not null, _currentWave?.State == WaveState.Ready),
                    new RuntimeRecoveryObservation(reconciliation.RuntimeId, browserState, reconciliation.Reason, "01 Chrome",
                        RecoveryPolicyExhausted: !reconciliation.Succeeded, SafeToResume: reconciliation.Succeeded));
                if (routing.Attention is not null)
                    CaptureProviderAttention(routing.Attention.ReasonCode == "ACCOUNT_CHALLENGE" ? "CHALLENGE" : routing.Attention.ReasonCode,
                        reconciliation.RuntimeId, routing.Attention.ExactLocation);
                _recovery.Insert(0, new RecoveryEventSummary(DateTimeOffset.UtcNow,
                    reconciliation.Succeeded ? "RECOVERED" : "RECOVERY_REQUIRED",
                    $"{reconciliation.RuntimeId}: {reconciliation.Reason}", reconciliation.Succeeded));
            }

            var identityConverged = true;
            var runtimes = await _runtimeRegistry.ListAsync(cancellationToken).ConfigureAwait(false);
            var identityReconciler = new BrowserSessionReconciliationService();
            foreach (var agentId in new[] { _managerAgentId!.Value }.Concat(_workerAgentIds))
            {
                var session = await _store.LoadLogicalAgentAsync(agentId, cancellationToken).ConfigureAwait(false);
                if (session is null) continue;
                var runtime = runtimes
                    .Where(x => !x.IsArchived && StringComparer.Ordinal.Equals(x.ProjectRunId, runId) && StringComparer.Ordinal.Equals(x.LogicalAgentId, agentId.ToString()))
                    .OrderByDescending(x => x.LastActivityAt)
                    .FirstOrDefault();
                var identity = identityReconciler.Reconcile(session, runtime);
                var unsafeIdentity = identity.Outcome is BrowserReconciliationKind.IDENTITY_MISMATCH or BrowserReconciliationKind.UNKNOWN ||
                    (identity.Outcome == BrowserReconciliationKind.MISSING_RUNTIME && session.CurrentConversationId is not null);
                if (!unsafeIdentity) continue;
                identityConverged = false;
                _recovery.Insert(0, new RecoveryEventSummary(DateTimeOffset.UtcNow, "RECOVERY_REQUIRED", identity.Reason, false));
            }

            if (result.StartupMayContinue && identityConverged)
            {
                await _newSendPause.ResumeNewSendsAsync("STARTUP_BROWSER_RECONCILIATION:SAFE_AUTO_RESUME", cancellationToken).ConfigureAwait(false);
                _autopilot = _settings.AutoResume ? "READY" : "PAUSED";
                foreach (var id in _attention.Where(x => result.Reconciliations.Any(r => r.Succeeded && StringComparer.Ordinal.Equals(r.RuntimeId, x.Value.RuntimeId))).Select(x => x.Key).ToArray())
                    _attention.Remove(id);
            }
            else
            {
                var reason = identityConverged ? "RECOVERY_POLICY_UNRESOLVED" : "LOGICAL_IDENTITY_UNRESOLVED";
                await _newSendPause.PauseNewSendsAsync($"STARTUP_BROWSER_RECONCILIATION:{reason}", cancellationToken).ConfigureAwait(false);
                _autopilot = "RECOVERY_REQUIRED";
            }
        }
    }

    private static PccExecutiveSettings ParseSettings(string? target, PccExecutiveSettings current)
    {
        if (string.IsNullOrWhiteSpace(target)) throw new InvalidOperationException("Selected settings are required.");
        var values = target.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Split('=', 2))
            .Where(x => x.Length == 2)
            .ToDictionary(x => x[0], x => x[1], StringComparer.OrdinalIgnoreCase);
        var provider = values.GetValueOrDefault("provider") ?? throw new InvalidOperationException("Provider selection is required.");
        if (!StringComparer.Ordinal.Equals(provider, "BrowserWeb"))
            throw new InvalidOperationException("Only the Browser Web provider is currently configured.");
        var dispatch = values.GetValueOrDefault("dispatch") ?? throw new InvalidOperationException("Dispatch mode selection is required.");
        if (!Enum.TryParse<DispatchMode>(dispatch, out _)) throw new InvalidOperationException("Unsupported dispatch mode.");
        if (!int.TryParse(values.GetValueOrDefault("interval"), out var interval) || interval < 0 || interval > 3600)
            throw new InvalidOperationException("Base dispatch interval must be between 0 and 3600 seconds.");
        if (!int.TryParse(values.GetValueOrDefault("maxWorkers"), out var maxWorkers) || maxWorkers is < 1 or > 5)
            throw new InvalidOperationException("Max Workers must be between 1 and 5.");
        if (!bool.TryParse(values.GetValueOrDefault("adaptive"), out var adaptive))
            throw new InvalidOperationException("Adaptive pacing selection is invalid.");
        if (!bool.TryParse(values.GetValueOrDefault("autoResume"), out var autoResume))
            throw new InvalidOperationException("Auto Resume selection is invalid.");
        return current with { Provider = "BrowserChat", DispatchMode = dispatch, BaseDispatchIntervalSeconds = interval, MaxWorkers = maxWorkers, AdaptivePacing = adaptive, AutoResume = autoResume };
    }

    private static void PersistLogicalAgents(SqliteStateStore store, ProjectRunId runId, LogicalAgentId managerAgentId, IReadOnlyList<LogicalAgentId> workerAgentIds)
    {
        if (store.LoadLogicalAgentAsync(managerAgentId).GetAwaiter().GetResult() is null)
            store.SaveLogicalAgentAsync(new LogicalAgentSession(managerAgentId, runId, AgentRole.Manager, null, null, null, LogicalSessionState.Ready)).GetAwaiter().GetResult();
        for (var i = 0; i < workerAgentIds.Count; i++)
        {
            var workerId = workerAgentIds[i];
            if (store.LoadLogicalAgentAsync(workerId).GetAwaiter().GetResult() is null)
                store.SaveLogicalAgentAsync(new LogicalAgentSession(workerId, runId, AgentRole.Worker, new WorkerSlotId(i + 1), null, null, LogicalSessionState.Ready)).GetAwaiter().GetResult();
        }
    }

    private async Task ConnectManagerChromeAsync(CancellationToken cancellationToken)
    {
        var run = RequireActiveRun();
        var managerAgentId = _managerAgentId ?? throw new InvalidOperationException("Manager logical identity is not initialized.");
        try
        {
            var existing = (await _runtimeRegistry.ListAsync(cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(x => StringComparer.Ordinal.Equals(x.ProjectRunId, run.Id.ToString()) && StringComparer.Ordinal.Equals(x.LogicalAgentId, managerAgentId.ToString()) && !x.IsArchived && x.State is not BrowserSessionState.Killed);
            if (existing is null)
            {
                await _sessions.CreateAsync(new BrowserSessionRequest(run.Id.ToString(), managerAgentId.ToString(), DefaultVisibility: BrowserVisibility.Hidden), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var proof = await _ownership.ProveAsync(existing, cancellationToken).ConfigureAwait(false);
                if (!proof.IsProven || existing.State is BrowserSessionState.Creating or BrowserSessionState.Degraded or BrowserSessionState.Recovering or BrowserSessionState.FailedRequiresAttention)
                {
                    var recovered = await _sessions.RecoverOrphanAsync(existing.RuntimeId, cancellationToken).ConfigureAwait(false);
                    if (!recovered.Succeeded)
                        throw new InvalidOperationException($"Manager Chrome recovery failed: {recovered.Reason}.");
                }
            }
            _recovery.Insert(0, new RecoveryEventSummary(DateTimeOffset.UtcNow, "READY", "PCC-owned Manager Chrome runtime initialized/recovered; personal Chrome remains excluded.", true));
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException or TimeoutException)
        {
            _recovery.Insert(0, new RecoveryEventSummary(DateTimeOffset.UtcNow, "BROWSER BLOCKED", ex.Message, false));
        }
        await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (_settings.AutoResume) EnsureAutopilotLoop();
    }

    private async Task<bool> EnsureManagerChromeReadyAsync(CancellationToken cancellationToken)
    {
        if (DateTimeOffset.UtcNow < _nextChromeRecoveryRetryAt)
            return false;

        var run = RequireActiveRun();
        var managerAgentId = _managerAgentId ?? throw new InvalidOperationException("Manager logical identity is not initialized.");
        var runtime = (await _runtimeRegistry.ListAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(x => !x.IsArchived && x.State is not BrowserSessionState.Killed && StringComparer.Ordinal.Equals(x.ProjectRunId, run.Id.ToString()) && StringComparer.Ordinal.Equals(x.LogicalAgentId, managerAgentId.ToString()));

        var ownership = runtime is null ? null : await _ownership.ProveAsync(runtime, cancellationToken).ConfigureAwait(false);
        if (runtime is null || ownership is null || !ownership.IsProven || runtime.State is BrowserSessionState.Creating or BrowserSessionState.Degraded or BrowserSessionState.Recovering or BrowserSessionState.FailedRequiresAttention)
        {
            _autopilot = "RECOVERING";
            _latestManagerHandoff = runtime is null
                ? "RECOVERING_CHROME — no active PCC-owned Manager Chrome session exists. Connecting automatically before Manager planning."
                : $"RECOVERING_CHROME — Manager Chrome readiness is not proven ({ownership?.Reason ?? runtime.State.ToString()}). Recovering before Manager planning.";
            _recovery.Insert(0, new RecoveryEventSummary(DateTimeOffset.UtcNow, "RECOVERING_CHROME", _latestManagerHandoff, true));
            await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
            await ConnectManagerChromeAsync(cancellationToken).ConfigureAwait(false);

            runtime = (await _runtimeRegistry.ListAsync(cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(x => !x.IsArchived && x.State is not BrowserSessionState.Killed && StringComparer.Ordinal.Equals(x.ProjectRunId, run.Id.ToString()) && StringComparer.Ordinal.Equals(x.LogicalAgentId, managerAgentId.ToString()));
        }

        if (runtime is null)
        {
            _nextChromeRecoveryRetryAt = DateTimeOffset.UtcNow.AddSeconds(5);
            _autopilot = "RECOVERING";
            _latestManagerHandoff = "RECOVERING_CHROME — PCC-owned Manager Chrome session is still unavailable. Automatic retry in 5 seconds.";
            await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        ownership = await _ownership.ProveAsync(runtime, cancellationToken).ConfigureAwait(false);
        if (!ownership.IsProven)
        {
            _nextChromeRecoveryRetryAt = DateTimeOffset.UtcNow.AddSeconds(5);
            _autopilot = "RECOVERING";
            _latestManagerHandoff = $"RECOVERING_CHROME — ownership/readiness is still unproven ({ownership.Reason}). Automatic retry in 5 seconds.";
            _recovery.Insert(0, new RecoveryEventSummary(DateTimeOffset.UtcNow, "RECOVERING_CHROME", ownership.Reason, false));
            await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        _nextChromeRecoveryRetryAt = DateTimeOffset.MinValue;
        _latestManagerHandoff = "CHROME_READY — PCC-owned Manager Chrome session and ownership are proven. Continuing to Manager evidence/planning.";
        _recovery.Insert(0, new RecoveryEventSummary(DateTimeOffset.UtcNow, "CHROME_READY", runtime.RuntimeId, true));
        await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task StartManagerAsync(CancellationToken cancellationToken)
    {
        var run = RequireActiveRun();
        var managerAgentId = _managerAgentId ?? throw new InvalidOperationException("Manager logical identity is not initialized.");
        if (_autopilot == "PAUSED") throw new InvalidOperationException("Resume AI before starting Manager.");
        if (!await EnsureManagerChromeReadyAsync(cancellationToken).ConfigureAwait(false))
            return;
        var runtime = (await _runtimeRegistry.ListAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(x => !x.IsArchived && x.State is not BrowserSessionState.Killed && StringComparer.Ordinal.Equals(x.ProjectRunId, run.Id.ToString()) && StringComparer.Ordinal.Equals(x.LogicalAgentId, managerAgentId.ToString()))
            ?? throw new InvalidOperationException("Connect the PCC-owned Manager Chrome session first.");
        var ownership = await _ownership.ProveAsync(runtime, cancellationToken).ConfigureAwait(false);
        if (!ownership.IsProven) throw new InvalidOperationException($"Manager send refused: {ownership.Reason}.");

        var logicalConversation = runtime.ConversationIdentity;
        if (string.IsNullOrWhiteSpace(logicalConversation))
        {
            var createdConversation = new ConversationId(StableGuid($"conversation:{run.Id}:manager:1"));
            logicalConversation = createdConversation.ToString();
            var bound = await _sessions.BindDispatchTargetAsync(runtime.RuntimeId, $"manager-plan:{run.Id}", logicalConversation, "NEW", cancellationToken).ConfigureAwait(false);
            if (!bound.Succeeded) throw new InvalidOperationException($"Manager conversation binding failed: {bound.Reason}.");
            await _store.SaveBrowserConversationAsync(new ConversationRecord { ConversationId = logicalConversation, LogicalAgentId = managerAgentId.ToString(), ProjectRunId = run.Id.ToString(), Sequence = 1, UrlOrProviderIdentity = "NEW", CreatedAt = DateTimeOffset.UtcNow, State = ConversationLifecycleState.Active }, cancellationToken).ConfigureAwait(false);
        }
        var managerConversation = new ConversationId(Guid.Parse(logicalConversation));
        if (!StringComparer.Ordinal.Equals(logicalConversation, managerConversation.ToString()))
            throw new InvalidOperationException("NON_CANONICAL_LOGICAL_CONVERSATION_IDENTITY: Manager runtime conversation must equal ConversationId.ToString().");

        var baseline = await _baseline.BuildAsync(_projectControlId ?? throw new InvalidOperationException("Selected PCC project identity is unavailable."), cancellationToken).ConfigureAwait(false);
        if (!baseline.IsSuccess || baseline.Value is null)
        {
            var evidenceCode = baseline.ErrorCode ?? baseline.Status.ToString();
            if (baseline.Status is ExternalReadStatus.RateLimited or ExternalReadStatus.TemporaryFailure or ExternalReadStatus.Offline)
            {
                _autopilot = "RECOVERING";
                _nextExternalEvidenceRetryAt = DateTimeOffset.UtcNow.AddSeconds(30);
                _latestManagerHandoff = $"RECOVERING_EVIDENCE — fresh PCC/GitHub evidence is temporarily unavailable ({evidenceCode}). Automatic retry is scheduled.";
                _recovery.Insert(0, new RecoveryEventSummary(DateTimeOffset.UtcNow, "RECOVERING_EVIDENCE", evidenceCode, true));
                await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            throw new InvalidOperationException($"Manager start requires fresh PCC/GitHub evidence: {evidenceCode}.");
        }
        _nextExternalEvidenceRetryAt = DateTimeOffset.MinValue;
        if (run.State == ProjectRunState.StalledAutoStopped)
        {
            run = run with { State = ProjectRunState.ManagerPlanning };
            _run = run;
            _runtimeErrorFingerprint = null;
            _runtimeErrorCount = 0;
            await _store.SaveProjectRunAsync(run, cancellationToken).ConfigureAwait(false);
            await PersistLoopGuardAsync(false, cancellationToken).ConfigureAwait(false);
        }
        var prompt = BuildManagerPrompt(run, baseline.Value);
        _managerBaseline = baseline.Value;
        await ResetManagerFormatRepairStateAsync(run, cancellationToken).ConfigureAwait(false);
        await _store.SaveCheckpointAsync(new DurableCheckpoint($"manager-baseline:{run.Id}", run.Id.ToString(), "manager-baseline-v1", JsonSerializer.Serialize(baseline.Value), DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(prompt))).ToLowerInvariant();
        await PersistAgentBindingAsync(managerAgentId, null, null, managerConversation, cancellationToken).ConfigureAwait(false);
        await _orchestrationStore.SaveAsync(new OrchestrationRecoverySnapshot(run, _currentWave, _runtimeTasks, _assignments, [], null, OrchestrationPhase.ManagerPlanning, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
        runtime = await _runtimeRegistry.GetAsync(runtime.RuntimeId, cancellationToken).ConfigureAwait(false) ?? runtime;
        var managerTaskKey = runtime.TaskId ?? $"manager-plan:{run.Id}";
        var managerTaskId = CanonicalDispatchIdentity.StableTask(run.Id, managerTaskKey);
        var managerWaveId = CanonicalDispatchIdentity.StableWave(run.Id, managerTaskKey);
        var managerProviderConversation = runtime.ProviderConversationIdentity ?? "NEW";
        var managerCorrelation = new DurableDispatchCorrelation(run.Id, managerAgentId, null, managerTaskId, managerWaveId, managerConversation, managerProviderConversation, hash);
        var managerDispatch = await _dispatchReservations.ReserveOrRecoverAsync(managerCorrelation, cancellationToken).ConfigureAwait(false);
        var request = new AgentRequest(run.Id, managerAgentId, managerConversation, managerDispatch.Id, prompt, hash, null, null, null, managerProviderConversation);
        var result = await _agentProvider.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (result.Accepted)
        {
            var updatedRuntime = await _runtimeRegistry.GetAsync(runtime.RuntimeId, cancellationToken).ConfigureAwait(false);
            if (updatedRuntime?.ProviderConversationIdentity is { Length: > 0 } providerIdentity && !string.Equals(providerIdentity, "NEW", StringComparison.OrdinalIgnoreCase))
                await _store.SaveBrowserConversationAsync(new ConversationRecord { ConversationId = logicalConversation, LogicalAgentId = managerAgentId.ToString(), ProjectRunId = run.Id.ToString(), Sequence = 1, UrlOrProviderIdentity = providerIdentity, CreatedAt = DateTimeOffset.UtcNow, State = ConversationLifecycleState.Active }, cancellationToken).ConfigureAwait(false);
        }
        await _store.SaveCheckpointAsync(new DurableCheckpoint($"manager-dispatch:{result.DispatchId}", run.Id.ToString(), "manager-dispatch-v1", JsonSerializer.Serialize(new { request.DispatchId, request.ContentHash, result.Accepted, result.IsUncertain, result.ErrorCode, result.ProviderEvidence }), DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
        var postSendRuntime = await _runtimeRegistry.GetAsync(runtime.RuntimeId, cancellationToken).ConfigureAwait(false);
        var providerConversationPending = result.Accepted &&
            (string.IsNullOrWhiteSpace(postSendRuntime?.ProviderConversationIdentity) || string.Equals(postSendRuntime.ProviderConversationIdentity, "NEW", StringComparison.OrdinalIgnoreCase));
        _latestManagerHandoff = providerConversationPending
            ? $"RECONCILING_CONVERSATION — Manager request {result.DispatchId} is accepted. Waiting for ChatGPT to expose the stable conversation identity; no resend will occur."
            : result.IsUncertain
                ? $"SUBMITTED_UNKNOWN — Manager dispatch {result.DispatchId} requires reconciliation before retry."
                : result.Accepted
                    ? $"Manager request {result.DispatchId} submitted. Waiting for a complete structured response."
                    : $"Manager send stopped safely: {result.ErrorCode ?? result.ProviderEvidence ?? "unknown provider state"}.";
        _autopilot = providerConversationPending ? "RECONCILING_CONVERSATION" : result.Accepted ? "PLANNING" : result.IsUncertain ? "WAITING_FOR_EVIDENCE" : "READY";
        CaptureProviderAttention(result.ErrorCode, runtime.RuntimeId, "Manager ChatGPT session");
        if (result.Accepted && _settings.AutoResume) EnsureAutopilotLoop();
        await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
    }

    private string BuildManagerPrompt(ProjectRun run, ProjectBaselineSnapshot baseline) =>
        ManagerPlanningPromptBuilder.Build(_projectControlId ?? baseline.ProjectControlId, _projectDisplay, _projectRepository, run, baseline, _autopilot);

    private sealed record DurableManagerFormatRepair(string? RejectedResponseHash, int AttemptsUsed, string? RepairContentHash, DateTimeOffset? SubmittedAt);

    private static string ManagerFormatRepairCheckpointKey(ProjectRun run) => $"manager-format-repair:{run.Id}";

    private async Task<DurableManagerFormatRepair> LoadManagerFormatRepairStateAsync(ProjectRun run, CancellationToken cancellationToken)
    {
        var checkpoint = await _store.LoadCheckpointAsync(ManagerFormatRepairCheckpointKey(run), cancellationToken).ConfigureAwait(false);
        if (checkpoint is null || string.IsNullOrWhiteSpace(checkpoint.Payload))
            return new DurableManagerFormatRepair(null, 0, null, null);
        try
        {
            return JsonSerializer.Deserialize<DurableManagerFormatRepair>(checkpoint.Payload)
                ?? new DurableManagerFormatRepair(null, 0, null, null);
        }
        catch (JsonException)
        {
            return new DurableManagerFormatRepair(null, 0, null, null);
        }
    }

    private Task ResetManagerFormatRepairStateAsync(ProjectRun run, CancellationToken cancellationToken) =>
        _store.SaveCheckpointAsync(
            new DurableCheckpoint(
                ManagerFormatRepairCheckpointKey(run),
                run.Id.ToString(),
                "manager-format-repair-v1",
                JsonSerializer.Serialize(new DurableManagerFormatRepair(null, 0, null, null)),
                DateTimeOffset.UtcNow),
            cancellationToken);

    private async Task<bool> TryRepairManagerResponseFormatAsync(
        ProjectRun run,
        LogicalAgentId managerAgentId,
        BrowserRuntimeRecord runtime,
        ChatGptSemanticSnapshot semantic,
        ManagerPlanParseResult parsed,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(semantic.CapturedResponseText))
            return false;
        if (string.IsNullOrWhiteSpace(runtime.ConversationIdentity) || string.IsNullOrWhiteSpace(runtime.ProviderConversationIdentity))
            return false;

        var rejectedResponseHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(semantic.CapturedResponseText))).ToLowerInvariant();
        var repairState = await LoadManagerFormatRepairStateAsync(run, cancellationToken).ConfigureAwait(false);
        if (!ManagerPlanningPromptBuilder.CanSubmitOrReconcileFormatRepair(repairState.AttemptsUsed, repairState.RejectedResponseHash, rejectedResponseHash))
            return false;

        var baseline = _managerBaseline ?? throw new InvalidOperationException("Manager planning baseline is unavailable for structured-response repair.");
        var repairPrompt = ManagerPlanningPromptBuilder.BuildFormatRepair(rejectedResponseHash, parsed.Findings, baseline);
        var repairHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(repairPrompt))).ToLowerInvariant();
        var repairConversation = new ConversationId(Guid.Parse(runtime.ConversationIdentity));
        var repairTaskKey = $"{runtime.TaskId ?? $"manager-plan:{run.Id}"}:format-repair:{rejectedResponseHash}";
        var repairTaskId = CanonicalDispatchIdentity.StableTask(run.Id, repairTaskKey);
        var repairWaveId = CanonicalDispatchIdentity.StableWave(run.Id, repairTaskKey);
        var repairCorrelation = new DurableDispatchCorrelation(
            run.Id,
            managerAgentId,
            null,
            repairTaskId,
            repairWaveId,
            repairConversation,
            runtime.ProviderConversationIdentity,
            repairHash);
        var repairDispatch = await _dispatchReservations.ReserveOrRecoverAsync(repairCorrelation, cancellationToken).ConfigureAwait(false);
        var request = new AgentRequest(
            run.Id,
            managerAgentId,
            repairConversation,
            repairDispatch.Id,
            repairPrompt,
            repairHash,
            null,
            null,
            null,
            runtime.ProviderConversationIdentity);
        var result = await _agentProvider.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (repairState.AttemptsUsed == 0)
        {
            await _store.SaveCheckpointAsync(
                new DurableCheckpoint(
                    ManagerFormatRepairCheckpointKey(run),
                    run.Id.ToString(),
                    "manager-format-repair-v1",
                    JsonSerializer.Serialize(new DurableManagerFormatRepair(rejectedResponseHash, 1, repairHash, DateTimeOffset.UtcNow)),
                    DateTimeOffset.UtcNow),
                cancellationToken).ConfigureAwait(false);
        }

        CaptureProviderAttention(result.ErrorCode, runtime.RuntimeId, "Manager ChatGPT session");
        if (!result.Accepted && !result.IsUncertain)
            throw new InvalidOperationException($"Manager structured-response repair send stopped safely: {result.ErrorCode ?? result.ProviderEvidence ?? "unknown provider state"}.");

        _autopilot = "PLANNING";
        _latestManagerHandoff = result.IsUncertain
            ? $"REPAIRING_MANAGER_FORMAT — the bounded JSON-only correction dispatch {result.DispatchId} is uncertain; PCC is reconciling it safely and will not duplicate the physical send."
            : $"REPAIRING_MANAGER_FORMAT — Manager returned an unstructured response. PCC submitted one bounded JSON-only correction automatically ({result.DispatchId}) and is waiting for the corrected response.";
        _recovery.Insert(0, new RecoveryEventSummary(DateTimeOffset.UtcNow, "REPAIRING_MANAGER_FORMAT", $"rejected={rejectedResponseHash};repair={repairHash};accepted={result.Accepted};uncertain={result.IsUncertain}", true));
        await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (_settings.AutoResume) EnsureAutopilotLoop();
        return true;
    }

    private async Task ReconcileManagerResponseAsync(CancellationToken cancellationToken)
    {
        var run = RequireActiveRun();
        var managerAgentId = _managerAgentId ?? throw new InvalidOperationException("Manager logical identity is not initialized.");
        var runtime = (await _runtimeRegistry.ListAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(x => !x.IsArchived && x.State is not BrowserSessionState.Killed && StringComparer.Ordinal.Equals(x.ProjectRunId, run.Id.ToString()) && StringComparer.Ordinal.Equals(x.LogicalAgentId, managerAgentId.ToString()))
            ?? throw new InvalidOperationException("Manager Browser runtime is unavailable.");
        if (string.IsNullOrWhiteSpace(runtime.TaskId) || string.IsNullOrWhiteSpace(runtime.ConversationIdentity))
            throw new InvalidOperationException("Manager dispatch binding is incomplete before response reconciliation.");

        if (string.IsNullOrWhiteSpace(runtime.ProviderConversationIdentity) || string.Equals(runtime.ProviderConversationIdentity, "NEW", StringComparison.OrdinalIgnoreCase))
        {
            var proof = await _ownership.ProveAsync(runtime, cancellationToken).ConfigureAwait(false);
            if (!proof.IsProven)
                throw new InvalidOperationException($"Manager conversation reconciliation refused because PCC ownership is not proven: {proof.Reason}.");

            var managerIdentityFragments = new List<string>
            {
                $"PROJECT_RUN: {run.Id}",
                $"REPOSITORY: {_projectRepository}",
                "Return one JSON object only with ManagerEstimate"
            };
            if (_managerBaseline is not null && !string.IsNullOrWhiteSpace(_managerBaseline.PccSourceSha))
                managerIdentityFragments.Add($"PCC_SOURCE_SHA: {_managerBaseline.PccSourceSha}");

            var providerIdentity = _browserAdapter is IConversationIdentityEvidenceResolver resolver
                ? await resolver.ResolveConversationIdentityAsync(runtime, null, managerIdentityFragments, cancellationToken).ConfigureAwait(false)
                : await _browserAdapter.GetCurrentConversationIdentityAsync(runtime, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(providerIdentity) || string.Equals(providerIdentity, "NEW", StringComparison.OrdinalIgnoreCase))
            {
                _autopilot = "RECONCILING_CONVERSATION";
                _latestManagerHandoff = "RECONCILING_CONVERSATION — Manager submission is already accepted, but ChatGPT has not exposed a stable conversation identity yet. PCC is polling automatically; no resend and no Loop Guard error.";
                _recovery.Insert(0, new RecoveryEventSummary(DateTimeOffset.UtcNow, "RECONCILING_CONVERSATION", "Provider conversation identity is pending after accepted Manager submission.", true));
                await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            runtime = runtime with { ProviderConversationIdentity = providerIdentity, LastActivityAt = DateTimeOffset.UtcNow };
            await _runtimeRegistry.UpsertAsync(runtime, cancellationToken).ConfigureAwait(false);
            await _store.SaveBrowserConversationAsync(new ConversationRecord { ConversationId = runtime.ConversationIdentity!, LogicalAgentId = managerAgentId.ToString(), ProjectRunId = run.Id.ToString(), Sequence = 1, UrlOrProviderIdentity = providerIdentity, CreatedAt = DateTimeOffset.UtcNow, State = ConversationLifecycleState.Active }, cancellationToken).ConfigureAwait(false);
            _recovery.Insert(0, new RecoveryEventSummary(DateTimeOffset.UtcNow, "CONVERSATION_READY", $"Manager provider conversation identity proven: {providerIdentity}", true));
        }

        if (_autopilot == "RECONCILING_CONVERSATION")
            _autopilot = "PLANNING";
        var providerConversationIdentity = runtime.ProviderConversationIdentity!;
        var expected = new BrowserDispatchExpectation(run.Id.ToString(), managerAgentId.ToString(), runtime.TaskId, runtime.ConversationIdentity, providerConversationIdentity);
        var semantic = await _browserAdapter.InspectAsync(runtime, expected, cancellationToken).ConfigureAwait(false);
        var resilience = new ChatGptResilienceClassifier().Classify(semantic, DateTimeOffset.UtcNow - runtime.LastActivityAt);
        if (semantic.Auth.State is AuthState.LoginRequired or AuthState.Challenge)
        {
            CaptureProviderAttention(semantic.Auth.State == AuthState.Challenge ? "CHALLENGE" : "LOGIN_REQUIRED", runtime.RuntimeId, "Manager ChatGPT session");
            await PersistGlobalHealthPauseAsync(resilience, runtime.RuntimeId, cancellationToken).ConfigureAwait(false);
            await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        if (resilience.Scope == FaultScope.Global && resilience.PauseUnsafeNewSends)
        {
            await PersistGlobalHealthPauseAsync(resilience, runtime.RuntimeId, cancellationToken).ConfigureAwait(false);
            await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        if (semantic.Generation.State == GenerationState.Generating)
        {
            _latestManagerHandoff = "READING_MANAGER_RESPONSE — ChatGPT is still generating; PCC is polling automatically and no plan has been accepted yet.";
            await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        if (semantic.ResponseCompleteness != ResponseCompleteness.Complete || string.IsNullOrWhiteSpace(semantic.CapturedResponseText))
        {
            _latestManagerHandoff = $"READING_MANAGER_RESPONSE — response observed but completion is not yet proven. completeness={semantic.ResponseCompleteness}; generation={semantic.Generation.State}; assistantMessages={semantic.AssistantMessageCount}. Retrying automatically.";
            await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var parsed = new StructuredManagerPlanParser().Parse(semantic.CapturedResponseText);
        if (!parsed.IsValid || parsed.Plan is null)
        {
            if (await TryRepairManagerResponseFormatAsync(run, managerAgentId, runtime, semantic, parsed, cancellationToken).ConfigureAwait(false))
                return;
            throw new InvalidOperationException($"Manager response rejected after bounded automatic format repair: {string.Join("; ", parsed.Findings.Select(x => $"{x.Code}:{x.Message}"))}");
        }
        await ResetManagerFormatRepairStateAsync(run, cancellationToken).ConfigureAwait(false);
        var planFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", parsed.Plan.Tasks.Select(x => x.Task.Fingerprint))))).ToLowerInvariant();
        var routingResult = await _pcc.ResolveProjectAsync(_projectControlId!, cancellationToken).ConfigureAwait(false);
        var baselineResult = await _baseline.BuildAsync(_projectControlId!, cancellationToken).ConfigureAwait(false);
        if (!routingResult.IsSuccess || routingResult.Project is null || !baselineResult.IsSuccess || baselineResult.Value is null)
            throw new InvalidOperationException("Fresh PCC and GitHub evidence is required before accepting a Manager wave.");
        var validation = new ManagerWaveValidator().Validate(parsed.Plan, routingResult.Project, baselineResult.Value, EmptyCompletedTaskIndex.Instance, run.CompletionMode);
        if (!validation.IsValid)
            throw new InvalidOperationException($"Manager wave rejected: {string.Join("; ", validation.Findings.Select(x => $"{x.Code}:{x.Message}"))}");

        // Count repetition only after a fresh wave is accepted. Re-reading one already-
        // received response must never manufacture three Manager plans and self-stall.
        _recentPlanFingerprints.Enqueue(planFingerprint);
        while (_recentPlanFingerprints.Count > 3) _recentPlanFingerprints.Dequeue();
        if (_recentPlanFingerprints.Count == 3 && _recentPlanFingerprints.Distinct(StringComparer.Ordinal).Count() == 1)
        {
            _run = run with { State = ProjectRunState.StalledAutoStopped };
            _autopilot = "STALLED";
            _latestManagerHandoff = "STALLED_AUTO_STOPPED — Manager repeated the identical accepted task fingerprint across three Manager waves.";
            await PersistLoopGuardAsync(true, cancellationToken).ConfigureAwait(false);
            await _orchestrationStore.SaveAsync(new OrchestrationRecoverySnapshot(_run, _currentWave, _runtimeTasks, _assignments, [], null, OrchestrationPhase.StalledAutoStopped, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
            await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        await PersistLoopGuardAsync(false, cancellationToken).ConfigureAwait(false);
        if (parsed.Plan.Tasks.Count == 0)
        {
            if (string.Equals(parsed.Plan.ProjectDecision, "CLOSE", StringComparison.OrdinalIgnoreCase) && run.VerifiedCompletion.Percent >= 99m)
            {
                _run = run with { State = ProjectRunState.ClosureMode, ManagerEstimate = parsed.Plan.ManagerEstimate, VerifiedCompletion = new VerifiedCompletion(Math.Min(99m, run.VerifiedCompletion.Percent)), CompletionMode = ProjectCompletionMode.ClosureMode };
                _currentPlan = parsed.Plan;
                _currentWave = _currentWave is null ? null : _currentWave with { State = WaveState.Completed };
                await _store.SaveCheckpointAsync(new DurableCheckpoint($"final-verification-request:{run.Id}", run.Id.ToString(), "final-verification-request-v1", JsonSerializer.Serialize(new { RequestedAt = DateTimeOffset.UtcNow, ManagerEstimate = parsed.Plan.ManagerEstimate.Percent }), DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
                await _orchestrationStore.SaveAsync(new OrchestrationRecoverySnapshot(_run, _currentWave, _runtimeTasks, _assignments, [], null, OrchestrationPhase.ClosureMode, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
                _autopilot = "CLOSURE_VERIFY";
                _latestManagerHandoff = "Manager requested CLOSE. Terminal 100% remains blocked until an independent fresh PCC/GitHub evidence reconciliation passes.";
                await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            if (string.Equals(parsed.Plan.ProjectDecision, "BLOCKED", StringComparison.OrdinalIgnoreCase) && parsed.Plan.KnownBlockers.Count > 0)
            {
                _run = run with { State = ProjectRunState.BlockedExternal, ManagerEstimate = parsed.Plan.ManagerEstimate, CompletionMode = ProjectCompletionMode.Blocked };
                _currentPlan = parsed.Plan;
                _currentWave = _currentWave is null ? null : _currentWave with { State = WaveState.Blocked };
                await _store.SaveCheckpointAsync(new DurableCheckpoint($"manager-plan:{run.Id}", run.Id.ToString(), "structured-manager-plan-v1", semantic.CapturedResponseText, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
                await _orchestrationStore.SaveAsync(new OrchestrationRecoverySnapshot(_run, _currentWave, _runtimeTasks, _assignments, [], null, OrchestrationPhase.BlockedExternal, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
                _autopilot = "BLOCKED_EXTERNAL";
                _runtimeErrorFingerprint = null;
                _runtimeErrorCount = 0;
                await PersistLoopGuardAsync(false, cancellationToken).ConfigureAwait(false);
                _latestManagerHandoff = $"BLOCKED_EXTERNAL — Manager supplied a valid structured blocker response: {string.Join("; ", parsed.Plan.KnownBlockers)}";
                await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            throw new InvalidOperationException("A zero-task Manager response must request CLOSE with 99% evidence-backed completion or ProjectDecision BLOCKED with concrete KnownBlockers.");
        }

        var taskStates = parsed.Plan.Tasks.ToDictionary(x => x.Task.Id, x => x.Task.State);
        var batch = new SafeDispatchPlanner().Schedule(parsed.Plan, taskStates, new HashSet<WorkerSlotId>(), new RuntimeHealthSnapshot(false, _settings.AdaptivePacing, TimeSpan.FromSeconds(_settings.BaseDispatchIntervalSeconds), null));
        _assignments = batch.Assignments.ToDictionary(x => x.TaskId, x => x.SlotId);
        _currentPlan = parsed.Plan;
        _runtimeTasks = parsed.Plan.Tasks.Select(x => x.Task).ToArray();
        _currentWave = new Wave(WaveId.New(), run.Id, (_currentWave?.Sequence ?? 0) + 1, WaveState.Ready, parsed.Plan.Tasks.Select(x => x.Task.Id).ToArray(), DateTimeOffset.UtcNow);
        _run = run with { State = ProjectRunState.WaveReady, ManagerEstimate = parsed.Plan.ManagerEstimate };
        var snapshot = new OrchestrationRecoverySnapshot(_run, _currentWave, parsed.Plan.Tasks.Select(x => x.Task).ToArray(), _assignments, [], null, OrchestrationPhase.WaveValidation, DateTimeOffset.UtcNow);
        await _orchestrationStore.CreateWaveAsync(snapshot, cancellationToken: cancellationToken).ConfigureAwait(false);
        await _store.SaveCheckpointAsync(new DurableCheckpoint($"manager-plan:{run.Id}", run.Id.ToString(), "structured-manager-plan-v1", semantic.CapturedResponseText, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
        _autopilot = "READY_TO_DISPATCH";
        _runtimeErrorFingerprint = null;
        _runtimeErrorCount = 0;
        await PersistLoopGuardAsync(false, cancellationToken).ConfigureAwait(false);
        _latestManagerHandoff = $"Validated Wave {_currentWave.Id}: {parsed.Plan.Tasks.Count} task(s), {batch.Assignments.Count} ready, {batch.Deferred.Count} dependency/scope deferred.";
        await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task StartDispatchAsync(CancellationToken cancellationToken)
    {
        var run = RequireActiveRun();
        var plan = _currentPlan ?? throw new InvalidOperationException("A validated Manager plan is required before dispatch.");
        var wave = _currentWave is { State: WaveState.Ready } ready ? ready : throw new InvalidOperationException("Current Wave is not ready for dispatch.");
        var dispatchProposals = plan.Tasks.Where(x => _assignments.ContainsKey(x.Task.Id) && _runtimeTasks.FirstOrDefault(t => t.Id == x.Task.Id)?.State is not (TaskState.Dispatched or TaskState.Running or TaskState.Completed)).ToArray();
        if (dispatchProposals.Length == 0) throw new InvalidOperationException("No safely dispatchable tasks remain in the current Wave.");
        var bindings = new List<WorkerExecutionBinding>();
        foreach (var proposal in dispatchProposals)
        {
            if (!_assignments.TryGetValue(proposal.Task.Id, out var slot)) continue;
            var agentId = _workerAgentIds[slot.Value - 1];
            var runtime = (await _runtimeRegistry.ListAsync(cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(x => !x.IsArchived && x.State is not BrowserSessionState.Killed && StringComparer.Ordinal.Equals(x.ProjectRunId, run.Id.ToString()) && StringComparer.Ordinal.Equals(x.LogicalAgentId, agentId.ToString()));
            var conversationId = new ConversationId(StableGuid($"conversation:{run.Id}:worker:{slot.Value}:1"));
            if (runtime is null)
                runtime = await _sessions.CreateAsync(new BrowserSessionRequest(run.Id.ToString(), agentId.ToString(), slot.Value.ToString(), proposal.Task.Id.ToString(), conversationId.ToString(), "NEW", BrowserVisibility.Hidden), cancellationToken).ConfigureAwait(false);
            var ownership = await _ownership.ProveAsync(runtime, cancellationToken).ConfigureAwait(false);
            if (!ownership.IsProven) throw new InvalidOperationException($"Worker {slot.Value} send refused before binding: {ownership.Reason}.");
            if (!StringComparer.Ordinal.Equals(runtime.WorkerSlotId, slot.Value.ToString())) throw new InvalidOperationException($"Worker slot correlation failed before send for slot {slot.Value}.");
            if (!StringComparer.Ordinal.Equals(runtime.TaskId, proposal.Task.Id.ToString()) || !StringComparer.Ordinal.Equals(runtime.ConversationIdentity, conversationId.ToString()))
            {
                var bound = await _sessions.BindDispatchTargetAsync(runtime.RuntimeId, proposal.Task.Id.ToString(), conversationId.ToString(), runtime.ProviderConversationIdentity ?? "NEW", cancellationToken).ConfigureAwait(false);
                if (!bound.Succeeded) throw new InvalidOperationException($"Worker {slot.Value} dispatch binding failed: {bound.Reason}.");
                runtime = bound.Runtime ?? runtime;
            }
            await PersistAgentBindingAsync(agentId, slot, proposal.Task.Id, conversationId, cancellationToken).ConfigureAwait(false);
            await _store.SaveBrowserConversationAsync(new ConversationRecord { ConversationId = conversationId.ToString(), LogicalAgentId = agentId.ToString(), ProjectRunId = run.Id.ToString(), Sequence = 1, UrlOrProviderIdentity = runtime.ProviderConversationIdentity ?? "NEW", CreatedAt = DateTimeOffset.UtcNow, State = ConversationLifecycleState.Active }, cancellationToken).ConfigureAwait(false);
            bindings.Add(new WorkerExecutionBinding(slot, agentId, conversationId, runtime.ProviderConversationIdentity ?? "NEW"));
        }

        _currentWave = wave with { State = WaveState.Dispatching };
        _run = run with { State = ProjectRunState.Dispatching };
        _autopilot = "DISPATCHING";
        await _orchestrationStore.SaveAsync(new OrchestrationRecoverySnapshot(_run, _currentWave, plan.Tasks.Select(x => x.Task).ToArray(), _assignments, [], null, OrchestrationPhase.Dispatching, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
        var result = await new ManagerWorkerOrchestrator(_agentProvider, baseDispatchInterval: TimeSpan.FromSeconds(_settings.BaseDispatchIntervalSeconds), dispatchReservations: _dispatchReservations)
            .DispatchWaveAsync(run.Id, new WavePlan(wave.Id, plan.ManagerEstimate, dispatchProposals.Select(x => x.Task).ToArray(), []), bindings, EmptyCompletedTaskIndex.Instance, cancellationToken).ConfigureAwait(false);
        foreach (var dispatched in result.Dispatches.Where(x => x.Result.Accepted))
        {
            var runtime = (await _runtimeRegistry.ListAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(x => !x.IsArchived && StringComparer.Ordinal.Equals(x.LogicalAgentId, dispatched.Binding.LogicalAgentId.ToString()));
            if (runtime?.ProviderConversationIdentity is { Length: > 0 } providerIdentity)
                await _store.SaveBrowserConversationAsync(new ConversationRecord { ConversationId = dispatched.Binding.ConversationId.ToString(), LogicalAgentId = dispatched.Binding.LogicalAgentId.ToString(), ProjectRunId = run.Id.ToString(), Sequence = 1, UrlOrProviderIdentity = providerIdentity, CreatedAt = DateTimeOffset.UtcNow, State = ConversationLifecycleState.Active }, cancellationToken).ConfigureAwait(false);
        }
        var taskStates = result.Dispatches.ToDictionary(x => x.Task.Id, x => x.Result.Accepted ? TaskState.Dispatched : x.Result.IsUncertain ? TaskState.Dispatched : TaskState.Blocked);
        var tasks = plan.Tasks.Select(x => taskStates.TryGetValue(x.Task.Id, out var state) ? x.Task with { State = state } : x.Task).ToArray();
        _runtimeTasks = tasks;
        var allSubmitted = _assignments.Keys.All(id => _runtimeTasks.FirstOrDefault(t => t.Id == id)?.State == TaskState.Dispatched);
        _currentWave = _currentWave with { State = allSubmitted ? WaveState.Running : WaveState.Ready };
        _run = _run with { State = allSubmitted ? ProjectRunState.WaveRunning : ProjectRunState.WaveReady };
        _autopilot = result.HasUncertainDispatch ? "WAITING_FOR_EVIDENCE" : allSubmitted ? "WAITING_WORKERS" : "READY_TO_DISPATCH";
        foreach (var dispatch in result.Dispatches.Where(x => !x.Result.Accepted))
        {
            var runtime = (await _runtimeRegistry.ListAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(x => StringComparer.Ordinal.Equals(x.LogicalAgentId, dispatch.Binding.LogicalAgentId.ToString()) && !x.IsArchived);
            if (runtime is not null) CaptureProviderAttention(dispatch.Result.ErrorCode, runtime.RuntimeId, $"Worker {dispatch.Binding.SlotId} ChatGPT session");
        }
        await _orchestrationStore.SaveAsync(new OrchestrationRecoverySnapshot(_run, _currentWave, tasks, _assignments, [], null, allSubmitted ? OrchestrationPhase.WaveRunning : OrchestrationPhase.Dispatching, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
        await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task PauseDispatchAsync(CancellationToken cancellationToken)
    {
        var run = RequireActiveRun();
        await _newSendPause.PauseNewSendsAsync("Operator paused dispatch; canonical new-send gate is shared by Manager and Workers.", cancellationToken).ConfigureAwait(false);
        await _store.SaveCheckpointAsync(new DurableCheckpoint($"dispatch-pause:{run.Id}", run.Id.ToString(), "dispatch-pause-v1", "{\"paused\":true}", DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
        await _store.SaveCheckpointAsync(new DurableCheckpoint($"autopilot-pause:{run.Id}", run.Id.ToString(), "autopilot-pause-v1", "{\"paused\":true}", DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
        _autopilot = "PAUSED";
        await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ReconcileWorkerResponsesAsync(CancellationToken cancellationToken)
    {
        var run = RequireActiveRun();
        var plan = _currentPlan ?? throw new InvalidOperationException("Current Manager plan is unavailable.");
        var wave = _currentWave ?? throw new InvalidOperationException("Current Wave is unavailable.");
        var parsedHandoffs = new List<(ManagerTaskProposal Expected, WorkerSlotId Slot, HandoffAssessment Parsed)>();
        var runtimes = await _runtimeRegistry.ListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var proposal in plan.Tasks.Where(x => _assignments.ContainsKey(x.Task.Id)))
        {
            var slot = _assignments[proposal.Task.Id];
            var agentId = _workerAgentIds[slot.Value - 1];
            var runtime = runtimes.FirstOrDefault(x => !x.IsArchived && x.State is not BrowserSessionState.Killed && StringComparer.Ordinal.Equals(x.LogicalAgentId, agentId.ToString()));
            if (runtime is null || string.IsNullOrWhiteSpace(runtime.ConversationIdentity) || string.IsNullOrWhiteSpace(runtime.ProviderConversationIdentity) || string.Equals(runtime.ProviderConversationIdentity, "NEW", StringComparison.OrdinalIgnoreCase))
                continue;
            var expected = new BrowserDispatchExpectation(run.Id.ToString(), agentId.ToString(), proposal.Task.Id.ToString(), runtime.ConversationIdentity, runtime.ProviderConversationIdentity, slot.Value.ToString());
            var semantic = await _browserAdapter.InspectAsync(runtime, expected, cancellationToken).ConfigureAwait(false);
            var resilience = new ChatGptResilienceClassifier().Classify(semantic, DateTimeOffset.UtcNow - runtime.LastActivityAt);
            if (semantic.Auth.State is AuthState.LoginRequired or AuthState.Challenge)
            {
                CaptureProviderAttention(semantic.Auth.State == AuthState.Challenge ? "CHALLENGE" : "LOGIN_REQUIRED", runtime.RuntimeId, $"Worker {slot.Value} ChatGPT session");
                await PersistGlobalHealthPauseAsync(resilience, runtime.RuntimeId, cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (resilience.Scope == FaultScope.Global && resilience.PauseUnsafeNewSends)
            {
                await PersistGlobalHealthPauseAsync(resilience, runtime.RuntimeId, cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (semantic.Generation.State == GenerationState.Generating || semantic.ResponseCompleteness != ResponseCompleteness.Complete || string.IsNullOrWhiteSpace(semantic.CapturedResponseText))
                continue;
            parsedHandoffs.Add((proposal, slot, new WorkerHandoffParser().Parse(semantic.CapturedResponseText)));
        }
        if (parsedHandoffs.Count < _assignments.Count)
        {
            _autopilot = "WAITING_WORKERS";
            _latestManagerHandoff = $"Waiting for complete Worker handoffs: {parsedHandoffs.Count}/{_assignments.Count} captured.";
            await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var baseline = _managerBaseline ?? throw new InvalidOperationException("Manager planning baseline is unavailable; verification cannot proceed.");
        var reconciled = await new LiveWaveEvidenceReconciler(_baseline, _pcc).ReconcileAsync(_projectControlId!, baseline, parsedHandoffs, cancellationToken).ConfigureAwait(false);
        if (!reconciled.IsSuccess || reconciled.Value is null) throw new InvalidOperationException($"Live Wave reconciliation failed: {reconciled.ErrorCode ?? reconciled.Status.ToString()}.");
        var validated = parsedHandoffs.Zip(reconciled.Value.Handoffs, (source, assessment) => (source.Expected, source.Slot, Assessment: assessment)).ToArray();
        _runtimeTasks = _runtimeTasks.Select(task =>
        {
            var assessment = validated.FirstOrDefault(x => x.Expected.Task.Id == task.Id).Assessment;
            return assessment?.Quality == HandoffQuality.Valid ? task with { State = TaskState.Completed } : task with { State = TaskState.Validating };
        }).ToArray();
        var completionGates = validated.Select(x => new CompletionGate($"Task {x.Expected.Task.Id}", true, 1m, x.Assessment.Quality == HandoffQuality.Valid ? GateState.Pass : GateState.Partial, string.Join(";", x.Assessment.Findings.Select(f => f.Code)))).ToArray();
        var completion = EvaluateEvidenceCompletion(run.ManagerEstimate, reconciled.Value.Live, validated);
        _run = run with { State = completion.Mode == ProjectCompletionMode.ClosureMode ? ProjectRunState.ClosureMode : ProjectRunState.ManagerReview, VerifiedCompletion = completion.VerifiedCompletion, CompletionMode = completion.Mode };
        _recentVerifiedCompletion.Enqueue(_run.VerifiedCompletion.Percent);
        while (_recentVerifiedCompletion.Count > 3) _recentVerifiedCompletion.Dequeue();
        await PersistLoopGuardAsync(false, cancellationToken).ConfigureAwait(false);
        if (_recentVerifiedCompletion.Count == 3 && _recentVerifiedCompletion.Distinct().Count() == 1 && _run.VerifiedCompletion.Percent < 99m)
        {
            _run = _run with { State = ProjectRunState.StalledAutoStopped };
            _autopilot = "STALLED";
        }
        var loop = new LoopAssessment(LoopGuardLevel.Normal, []);
        var recommendation = _run.State == ProjectRunState.StalledAutoStopped ? OrchestrationDecision.StalledAutoStopped : OrchestrationDecision.Continue;
        var review = new ManagerReviewPacketBuilder().Build(_projectControlId!, wave.Id, validated, reconciled.Value.Live, [], completionGates, loop, [], recommendation);
        _currentWave = wave with { State = WaveState.Completed };
        await _orchestrationStore.SaveManagerReviewAsync(new OrchestrationRecoverySnapshot(_run, _currentWave, _runtimeTasks, _assignments, [], review, OrchestrationPhase.ManagerReview, DateTimeOffset.UtcNow), $"manager-review:{wave.Id}", cancellationToken: cancellationToken).ConfigureAwait(false);
        if (_run.State != ProjectRunState.StalledAutoStopped)
        {
            await SendManagerReviewAsync(review, cancellationToken).ConfigureAwait(false);
            _autopilot = "MANAGER_REVIEW";
            _latestManagerHandoff = $"Wave {wave.Sequence} reconciled against live evidence; {validated.Count(x => x.Assessment.Quality == HandoffQuality.Valid)}/{validated.Length} tasks verified. Manager review submitted.";
        }
        else
        {
            _latestManagerHandoff = "STALLED_AUTO_STOPPED — three reconciled Waves produced no Verified Completion movement.";
        }
        await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
    }

    private static CompletionControlEvaluation EvaluateEvidenceCompletion(ManagerEstimate estimate, ProjectBaselineSnapshot live, IReadOnlyList<(ManagerTaskProposal Expected, WorkerSlotId Slot, HandoffAssessment Assessment)> handoffs)
    {
        var qualityPass = new EvidenceQualityAssessment(EvidenceQuality.STRONG, []);
        var gates = new List<PolicyCompletionGate>
        {
            Gate(CompletionGateFamily.IMPLEMENTATION, "Validated Worker handoffs", handoffs.Count > 0 && handoffs.All(x => x.Assessment.Quality == HandoffQuality.Valid) ? GateState.Pass : GateState.Partial, qualityPass),
            Gate(CompletionGateFamily.CI, "Exact-head CI", string.Equals(live.CiState, "success", StringComparison.OrdinalIgnoreCase) || string.Equals(live.CiState, "green", StringComparison.OrdinalIgnoreCase) ? GateState.Pass : GateState.Unknown, qualityPass),
            Gate(CompletionGateFamily.ORCHESTRATION, "PCC routing freshness", live.Freshness == EvidenceFreshness.Current ? GateState.Pass : GateState.Unknown, qualityPass),
            Gate(CompletionGateFamily.TESTS, "Canonical task closure", live.CanonicalTasks.Count > 0 && live.CanonicalTasks.All(x => IsTerminalCanonicalState(x.State)) ? GateState.Pass : GateState.Partial, qualityPass),
            Gate(CompletionGateFamily.RELEASE, "No live blockers", live.KnownBlockers.Count == 0 ? GateState.Pass : GateState.Fail, qualityPass)
        };
        var evaluation = new CompletionGateController().Evaluate(estimate, gates, []);
        if (evaluation.VerifiedCompletion.Percent == 100m)
            return evaluation with { VerifiedCompletion = new VerifiedCompletion(99m), Mode = ProjectCompletionMode.ClosureMode };
        return evaluation;

        static PolicyCompletionGate Gate(CompletionGateFamily family, string name, GateState state, EvidenceQualityAssessment quality) =>
            new(family, new CompletionGate(name, true, 1m, state, name), quality, ClosurePriority.P0_VERIFICATION_BLOCKER);
    }

    private static bool EvidenceReadyForClosure(ProjectBaselineSnapshot live) =>
        live.Freshness == EvidenceFreshness.Current &&
        live.KnownBlockers.Count == 0 &&
        (string.Equals(live.CiState, "success", StringComparison.OrdinalIgnoreCase) || string.Equals(live.CiState, "green", StringComparison.OrdinalIgnoreCase)) &&
        live.CanonicalTasks.Count > 0 && live.CanonicalTasks.All(x => IsTerminalCanonicalState(x.State));

    private async Task<bool> RunIndependentFinalVerificationAsync(CancellationToken cancellationToken)
    {
        var run = RequireActiveRun();
        if (run.CompletionMode != ProjectCompletionMode.ClosureMode || run.VerifiedCompletion.Percent < 99m) return false;
        if (await _store.LoadCheckpointAsync($"final-verification-request:{run.Id}", cancellationToken).ConfigureAwait(false) is null) return false;
        var fresh = await _baseline.BuildAsync(_projectControlId ?? throw new InvalidOperationException("Selected PCC project identity is unavailable."), cancellationToken).ConfigureAwait(false);
        if (!fresh.IsSuccess || fresh.Value is null || !EvidenceReadyForClosure(fresh.Value))
        {
            _autopilot = "CLOSURE_VERIFY";
            _latestManagerHandoff = "Closure verification remains at 99% or below: fresh authoritative evidence is missing, stale, blocked, or non-green.";
            return false;
        }
        _run = run with { State = ProjectRunState.VerifiedComplete, VerifiedCompletion = new VerifiedCompletion(100m), CompletionMode = ProjectCompletionMode.VerifiedComplete };
        await _orchestrationStore.SaveAsync(new OrchestrationRecoverySnapshot(_run, _currentWave, _runtimeTasks, _assignments, [], null, OrchestrationPhase.VerifiedComplete, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
        _autopilot = "DONE";
        _latestManagerHandoff = "100% VERIFIED — independent fresh PCC/GitHub/test evidence reconciliation passed all terminal gates.";
        return true;
    }

    private static bool IsTerminalCanonicalState(string state) => state.ToUpperInvariant() is "DONE" or "COMPLETE" or "COMPLETED" or "VERIFIED" or "MERGED" or "CLOSED" or "ACCEPTED";

    private async Task SendManagerReviewAsync(ConsolidatedManagerReviewPacket review, CancellationToken cancellationToken)
    {
        var run = RequireActiveRun();
        var managerAgentId = _managerAgentId!.Value;
        var runtime = (await _runtimeRegistry.ListAsync(cancellationToken).ConfigureAwait(false)).First(x => !x.IsArchived && StringComparer.Ordinal.Equals(x.LogicalAgentId, managerAgentId.ToString()));
        var logicalConversation = runtime.ConversationIdentity ?? throw new InvalidOperationException("Manager logical conversation is unavailable.");
        var managerReviewConversation = new ConversationId(Guid.Parse(logicalConversation));
        if (!StringComparer.Ordinal.Equals(logicalConversation, managerReviewConversation.ToString()))
            throw new InvalidOperationException("NON_CANONICAL_LOGICAL_CONVERSATION_IDENTITY: Manager review runtime conversation must equal ConversationId.ToString().");
        var providerConversation = runtime.ProviderConversationIdentity ?? throw new InvalidOperationException("Manager provider conversation is unavailable.");
        var ownership = await _ownership.ProveAsync(runtime, cancellationToken).ConfigureAwait(false);
        if (!ownership.IsProven) throw new InvalidOperationException($"Manager review send refused before binding: {ownership.Reason}.");
        await _sessions.BindDispatchTargetAsync(runtime.RuntimeId, $"manager-review:{review.WaveId}", logicalConversation, providerConversation, cancellationToken).ConfigureAwait(false);
        await PersistAgentBindingAsync(managerAgentId, null, null, managerReviewConversation, cancellationToken).ConfigureAwait(false);
        var prompt = $"WAVE_REVIEW:\n{JsonSerializer.Serialize(review)}\nReturn the next structured Manager plan JSON only. Use 0..5 tasks and current live evidence.";
        var reviewHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(prompt))).ToLowerInvariant();
        runtime = await _runtimeRegistry.GetAsync(runtime.RuntimeId, cancellationToken).ConfigureAwait(false) ?? runtime;
        var reviewConversation = new ConversationId(Guid.Parse(logicalConversation));
        var reviewTaskKey = runtime.TaskId ?? $"manager-review:{review.WaveId}";
        var reviewTaskId = CanonicalDispatchIdentity.StableTask(run.Id, reviewTaskKey);
        var reviewWaveId = CanonicalDispatchIdentity.StableWave(run.Id, reviewTaskKey);
        var reviewProviderConversation = runtime.ProviderConversationIdentity ?? providerConversation;
        var reviewCorrelation = new DurableDispatchCorrelation(run.Id, managerAgentId, null, reviewTaskId, reviewWaveId, reviewConversation, reviewProviderConversation, reviewHash);
        var reviewDispatch = await _dispatchReservations.ReserveOrRecoverAsync(reviewCorrelation, cancellationToken).ConfigureAwait(false);
        var request = new AgentRequest(run.Id, managerAgentId, reviewConversation, reviewDispatch.Id, prompt, reviewHash, null, null, null, reviewProviderConversation);
        var result = await _agentProvider.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!result.Accepted) throw new InvalidOperationException(result.IsUncertain ? "Manager review submission is SUBMITTED_UNKNOWN; no retry is allowed before reconciliation." : $"Manager review send failed: {result.ErrorCode}.");
    }

    private void CaptureProviderAttention(string? errorCode, string runtimeId, string target)
    {
        if (errorCode is not ("LOGIN_REQUIRED" or "CHALLENGE")) return;
        var id = $"browser-attention:{runtimeId}";
        var challenge = errorCode == "CHALLENGE";
        var happened = challenge ? "ChatGPT requires an account challenge." : "ChatGPT sign-in is required.";
        var reason = challenge
            ? "PCC Executive cannot complete a CAPTCHA or account challenge. Open this PCC browser and complete the challenge."
            : "PCC Executive cannot complete account sign-in. Open this PCC browser and complete sign-in.";
        _attention[id] = (new AttentionSummary(id, happened, reason, challenge ? "Complete challenge" : "Complete sign-in", target, "P0"), runtimeId);
        _autopilot = "ATTENTION_REQUIRED";
    }

    private async Task LoadConversationHistoryAsync(string? target, CancellationToken cancellationToken)
    {
        var run = RequireActiveRun();
        LogicalAgentId? agentId = string.Equals(target, "Manager", StringComparison.OrdinalIgnoreCase) ? _managerAgentId : null;
        if (agentId is null && target is not null && target.StartsWith("Worker ", StringComparison.OrdinalIgnoreCase) && int.TryParse(target[7..], out var slot) && slot is >= 1 and <= 5)
            agentId = _workerAgentIds[slot - 1];
        if (agentId is null) throw new InvalidOperationException("Conversation History requires Manager or Worker 1..5.");
        var records = await _store.ListBrowserConversationsAsync(cancellationToken).ConfigureAwait(false);
        _conversationHistory = records
            .Where(x => StringComparer.Ordinal.Equals(x.ProjectRunId, run.Id.ToString()) && StringComparer.Ordinal.Equals(x.LogicalAgentId, agentId.Value.ToString()))
            .OrderBy(x => x.Sequence)
            .Select(x => new ConversationHistorySummary(target!, x.Sequence, x.State.ToString(), x.UrlOrProviderIdentity, x.CreatedAt, x.RetiredAt, x.RolloverReason, x.PredecessorConversationId, x.SuccessorConversationId))
            .ToArray();
        await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
    }

    private void EnsureAutopilotLoop()
    {
        if (_autopilotTask is { IsCompleted: false }) return;
        _autopilotTask = Task.Run(() => RunAutopilotLoopAsync(_autopilotCancellation.Token));
    }

    private async Task RunAutopilotLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                NormalizeRecoveredAutopilotState();
                if (_autopilot == "PAUSED" || _run?.State is ProjectRunState.VerifiedComplete or ProjectRunState.BlockedExternal or ProjectRunState.StalledAutoStopped or ProjectRunState.StoppedByOperator)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                    continue;
                }
                if (_sendGate.Snapshot is { IsPaused: true, ResumeNotBefore: not null } gate && gate.ResumeNotBefore <= DateTimeOffset.UtcNow && _settings.AutoResume)
                {
                    if (await TryResumeAfterFreshSemanticHealthAsync(cancellationToken).ConfigureAwait(false))
                        _autopilot = _currentWave?.State == WaveState.Running ? "WAITING_WORKERS" : "RECOVERING";
                }
                await _autopilotOperation.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (_currentPlan is null &&
                        _autopilot is "READY" or "RECOVERING" &&
                        DateTimeOffset.UtcNow >= _nextExternalEvidenceRetryAt)
                        await StartManagerAsync(cancellationToken).ConfigureAwait(false);
                    else if (_currentWave?.State == WaveState.Running)
                        await ReconcileWorkerResponsesAsync(cancellationToken).ConfigureAwait(false);
                    else if (_autopilot is "PLANNING" or "MANAGER_REVIEW" or "RECONCILING_CONVERSATION")
                        await ReconcileManagerResponseAsync(cancellationToken).ConfigureAwait(false);
                    else if (_autopilot == "CLOSURE_VERIFY")
                        await RunIndependentFinalVerificationAsync(cancellationToken).ConfigureAwait(false);
                    if (_settings.DispatchMode == DispatchMode.AutomaticStaged.ToString() && _currentWave?.State == WaveState.Ready && _currentPlan is not null)
                        await StartDispatchAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _autopilotOperation.Release();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (InvalidOperationException ex)
            {
                await RecordRuntimeLoopErrorAsync(ex, cancellationToken).ConfigureAwait(false);
            }
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RunSessionActionAsync(string? targetId, Func<string, Task<SessionActionResult>> operation, CancellationToken cancellationToken)
    {
        RequireActiveRun();
        if (string.IsNullOrWhiteSpace(targetId)) throw new InvalidOperationException("A PCC-owned runtime target is required.");
        if (!Snapshot.Sessions.Any(x => StringComparer.Ordinal.Equals(x.RuntimeId, targetId) && x.IsPccOwned))
            throw new InvalidOperationException("Session action refused because current PCC ownership proof is unavailable.");
        var result = await operation(targetId).ConfigureAwait(false);
        _recovery.Insert(0, new RecoveryEventSummary(DateTimeOffset.UtcNow, result.Succeeded ? "READY" : "BLOCKED", result.Reason, result.Succeeded));
        await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RefreshExternalAsync(string project, CancellationToken cancellationToken)
    {
        var result = await _pcc.ResolveProjectAsync(project, cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess && result.Project is not null)
        {
            _pccState = $"PASS@{result.Project.Provenance.SourceSha[..Math.Min(8, result.Project.Provenance.SourceSha.Length)]}";
            _projectDisplay = result.Project.DisplayName;
            _projectRepository = result.Project.Repository;
            _projectControlId = result.Project.ProjectControlId;

            var projectIdentity = result.Project.RoutingIdentity;
            var expectedProjectId = new ProjectId(StableGuid($"project:{projectIdentity}"));
            if (_run is null || _run.ProjectId != expectedProjectId)
            {
                var replacementLock = ProjectRunLock.TryAcquire(projectIdentity);
                if (!replacementLock.IsOwned)
                {
                    replacementLock.Dispose();
                    throw new InvalidOperationException($"PCC Executive is already controlling project '{result.Project.ProjectControlId}' on this machine.");
                }
                _projectLock?.Dispose();
                _projectLock = replacementLock;
                _run = new ProjectRun(ProjectRunId.New(), expectedProjectId, ProjectRunState.Initializing, DateTimeOffset.UtcNow, new ManagerEstimate(0), new VerifiedCompletion(0), ProjectCompletionMode.Active);
                _managerAgentId = AgentId(_run.Id, "manager");
                _workerAgentIds = Enumerable.Range(1, 5).Select(slot => AgentId(_run.Id, $"worker:{slot}")).ToArray();
                await _store.SaveProjectRunAsync(_run, cancellationToken).ConfigureAwait(false);
                PersistLogicalAgents(_store, _run.Id, _managerAgentId.Value, _workerAgentIds);
                var selected = new SelectedProjectState(result.Project.ProjectControlId, projectIdentity, result.Project.DisplayName, result.Project.Repository, _run.Id.Value);
                await _store.SaveCheckpointAsync(new DurableCheckpoint("active-project", _run.Id.ToString(), "active-project-v1", JsonSerializer.Serialize(selected), DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
            }

            if (_run.State == ProjectRunState.Initializing)
            {
                _run = _run with { State = ProjectRunState.ManagerPlanning };
                await _store.SaveProjectRunAsync(_run, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            _pccState = result.Status.ToString().ToUpperInvariant();
            _recovery.Insert(0, new RecoveryEventSummary(DateTimeOffset.UtcNow, _pccState, result.Message ?? "PCC project resolution did not succeed.", true));
        }
        await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RunVerificationAsync(CancellationToken cancellationToken)
    {
        var result = await _baseline.BuildAsync(_projectControlId ?? throw new InvalidOperationException("Select a project before verification."), cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess && result.Value is not null)
        {
            _pccState = $"PASS@{result.Value.PccSourceSha[..Math.Min(8, result.Value.PccSourceSha.Length)]}";
            _githubState = $"PASS@{result.Value.DefaultHeadSha[..Math.Min(8, result.Value.DefaultHeadSha.Length)]}";
            _projectDisplay = result.Value.DisplayName;
            _projectRepository = result.Value.Repository;
        }
        else
        {
            _githubState = result.Status.ToString().ToUpperInvariant();
            _recovery.Insert(0, new RecoveryEventSummary(DateTimeOffset.UtcNow, _githubState, result.ErrorCode ?? "GitHub verification did not succeed.", true));
        }
        await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RefreshLocalSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var runtimes = await _runtimeRegistry.ListAsync(cancellationToken).ConfigureAwait(false);
        var provenOwnedRuntimeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var runtime in runtimes.Where(x => !x.IsArchived))
        {
            try
            {
                var proof = await _ownership.ProveAsync(runtime, cancellationToken).ConfigureAwait(false);
                if (proof.IsProven)
                    provenOwnedRuntimeIds.Add(runtime.RuntimeId);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                _recovery.Insert(0, new RecoveryEventSummary(DateTimeOffset.UtcNow, "OWNERSHIP UNPROVEN", $"{runtime.RuntimeId}: {ex.Message}", true));
            }
        }

        Snapshot = BuildSnapshot(runtimes, provenOwnedRuntimeIds);
        SnapshotChanged?.Invoke(this, Snapshot);
    }

    private RuntimeSnapshot BuildSnapshot(IReadOnlyList<BrowserRuntimeRecord> runtimes, IReadOnlySet<string> provenOwnedRuntimeIds)
    {
        var run = _run;
        var activeRunId = run?.Id.ToString();
        var sessions = run is null
            ? Array.Empty<SessionSummary>()
            : runtimes
                .Where(x => !x.IsArchived && StringComparer.Ordinal.Equals(x.ProjectRunId, activeRunId))
                .Select(x => new SessionSummary(
                    x.RuntimeId,
                    LogicalNameFor(x),
                    _managerAgentId is not null && StringComparer.Ordinal.Equals(x.LogicalAgentId, _managerAgentId.Value.ToString()) ? "Manager" : "Worker",
                    x.State.ToString().ToUpperInvariant(),
                    x.Visibility == BrowserVisibility.Hidden ? SessionVisibility.Hidden : SessionVisibility.Visible,
                    x.ConversationIdentity ?? x.TaskId ?? "Not bound to a conversation yet",
                    x.LastActivityAt,
                    provenOwnedRuntimeIds.Contains(x.RuntimeId),
                    x.ProcessId,
                    MapSessionHealth(x.State)))
                .ToArray();

        var workers = run is null
            ? Array.Empty<WorkerSummary>()
            : _workerAgentIds.Select((id, index) =>
            {
                var slot = new WorkerSlotId(index + 1);
                var task = _assignments.Where(x => x.Value == slot).Select(x => _runtimeTasks.FirstOrDefault(t => t.Id == x.Key)).FirstOrDefault(x => x is not null);
                var session = sessions.FirstOrDefault(x => StringComparer.Ordinal.Equals(x.RuntimeId, runtimes.FirstOrDefault(r => StringComparer.Ordinal.Equals(r.LogicalAgentId, id.ToString()) && !r.IsArchived)?.RuntimeId));
                return new WorkerSummary(id.ToString(), $"Worker {index + 1}", "Worker", task?.State.ToString().ToUpperInvariant() ?? "IDLE", null, task?.Objective ?? "No task assigned", session?.Health ?? HealthState.Unknown, null);
            }).ToArray();

        var taskSummaries = _runtimeTasks.Select(task => new TaskSummary(
            task.Id.ToString(),
            task.Objective,
            task.State.ToString(),
            _currentPlan?.Tasks.FirstOrDefault(x => x.Task.Id == task.Id)?.Priority.ToString() ?? "—",
            _assignments.TryGetValue(task.Id, out var slot) ? $"Worker {slot.Value}" : null,
            task.State == TaskState.Completed)).ToArray();

        var schemaVersion = _store.GetSchemaVersionAsync().GetAwaiter().GetResult();
        var gates = new[]
        {
            new EvidenceGateSummary("Foundation", "PASS", 100, "Canonical Domain/Application contracts integrated"),
            new EvidenceGateSummary("Persistence", "PASS", 100, $"SQLite schema v{schemaVersion} · {_store.DatabasePath}"),
            new EvidenceGateSummary("PCC Integration", _pccState.StartsWith("PASS", StringComparison.Ordinal) ? "PASS" : "PARTIAL", null, _pccState),
            new EvidenceGateSummary("GitHub Integration", _githubState.StartsWith("PASS", StringComparison.Ordinal) ? "PASS" : "PARTIAL", null, _githubState),
            new EvidenceGateSummary("Browser Runtime", sessions.Length > 0 ? "PARTIAL" : "PARTIAL", null, sessions.Length > 0 ? "PCC-owned runtime inventory available; ChatGPT semantic health remains evidence-driven" : "Runtime implementation integrated; no active project Browser session"),
            new EvidenceGateSummary("UI", "PARTIAL", null, "Premium WPF shell is bound to integrated services; end-to-end user QA remains")
        };

        return new RuntimeSnapshot(
            GatewayBound: true,
            HasActiveRun: run is not null,
            RuntimeStatus: run is null ? "Select a project to begin" : "Integrated runtime",
            GlobalHealth: AggregateHealth(sessions),
            AutopilotState: run is null ? "WAITING_FOR_PROJECT" : _autopilot,
            CurrentWave: run is null ? "No project selected" : _currentWave is not null ? $"Wave {_currentWave.Sequence} · {_currentWave.State}" : run.State == ProjectRunState.ManagerPlanning ? "Manager planning" : run.State.ToString(),
            VerifiedCompletion: run is null ? null : (int)run.VerifiedCompletion.Percent,
            ManagerEstimate: run is null ? null : (int)run.ManagerEstimate.Percent,
            CompletionMode: run is null ? CompletionMode.Unknown : MapCompletionMode(run.CompletionMode),
            ActiveWorkers: _runtimeTasks.Count(x => x.State is TaskState.Assigned or TaskState.Dispatched or TaskState.Running),
            P0Count: 0,
            P1Count: 0,
            BlockerCount: 0,
            LoopGuardState: run is null ? "WAITING_FOR_PROJECT" : run.State == ProjectRunState.StalledAutoStopped ? "STALLED_AUTO_STOPPED" : _runtimeErrorCount > 0 ? $"WATCH:{_runtimeErrorCount}" : "NORMAL",
            LatestManagerHandoff: run is null ? "Select and resolve a project before Manager execution." : _latestManagerHandoff,
            CurrentExecutionFlow: run is null ? "Project Selection → resolve canonical project → Dashboard" : "Project → Manager plan → validate → staged Workers → reconcile → Manager review",
            ApiConfigured: false,
            ProviderMode: ProviderMode.BrowserWeb,
            DispatchSettings: new DispatchSettingsSummary(Enum.TryParse<DispatchMode>(_settings.DispatchMode, out var mode) ? mode : DispatchMode.AutomaticStaged, _settings.BaseDispatchIntervalSeconds, _settings.AdaptivePacing, _settings.MaxWorkers, true, _settings.AutoResume, true),
            Update: new UpdateSummary("0.1.0", null, "Release hardening integrated", "Durable data path active", $"Schema v{schemaVersion}", "Updater rollback contract integrated", false),
            Projects: run is null
                ? Array.Empty<ProjectSummary>()
                : [new ProjectSummary(_projectControlId ?? "UNKNOWN", _projectDisplay, _projectRepository, (int)run.VerifiedCompletion.Percent, run.State.ToString().ToUpperInvariant(), null, run.CreatedAt)],
            Sessions: sessions,
            Workers: workers,
            Tasks: taskSummaries,
            EvidenceGates: gates,
            AttentionItems: _attention.Values.Select(x => x.Summary).ToArray(),
            RecoveryEvents: _recovery.Take(20).ToArray(),
            ConversationHistory: _conversationHistory);
    }

    private string LogicalNameFor(BrowserRuntimeRecord runtime)
    {
        if (_managerAgentId is not null && StringComparer.Ordinal.Equals(runtime.LogicalAgentId, _managerAgentId.Value.ToString())) return "Manager";
        if (int.TryParse(runtime.WorkerSlotId, out var slot) && slot is >= 1 and <= 5) return $"Worker {slot}";
        return "Worker";
    }

    private static HealthState MapSessionHealth(BrowserSessionState state) => state switch
    {
        BrowserSessionState.Recovering => HealthState.Recovering,
        BrowserSessionState.Degraded or BrowserSessionState.FailedRequiresAttention => HealthState.Unknown,
        _ => HealthState.Unknown
    };

    private static HealthState AggregateHealth(IReadOnlyList<SessionSummary> sessions)
    {
        if (sessions.Any(x => x.Health == HealthState.Recovering)) return HealthState.Recovering;
        return HealthState.Unknown;
    }

    private static CompletionMode MapCompletionMode(ProjectCompletionMode mode) => mode switch
    {
        ProjectCompletionMode.Active => CompletionMode.Running,
        ProjectCompletionMode.ClosureMode => CompletionMode.ClosureMode,
        ProjectCompletionMode.VerifiedComplete => CompletionMode.Verified,
        ProjectCompletionMode.Blocked => CompletionMode.Blocked,
        _ => CompletionMode.Unknown
    };

    public async ValueTask DisposeAsync()
    {
        _autopilotCancellation.Cancel();
        if (_rolloverRuntime is not null)
            await _rolloverRuntime.DisposeAsync().ConfigureAwait(false);
        if (_autopilotTask is not null)
        {
            try { await _autopilotTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        if (_run is not null)
        {
            var durable = await _orchestrationStore.LoadAsync(_run.Id).ConfigureAwait(false);
            var snapshot = new OrchestrationRecoverySnapshot(
                _run,
                _currentWave,
                _runtimeTasks,
                _assignments,
                durable?.Dispatches ?? Array.Empty<PCCExecutive.Domain.Dispatch>(),
                durable?.ManagerReview,
                CurrentOrchestrationPhase(),
                DateTimeOffset.UtcNow);
            var startup = new DurableStartupRecoveryService(_store, _orchestrationStore);
            var shutdown = new SafeShutdownCoordinator(_newSendPause, new RecoveryCheckpointService(_store), startup, _orchestrationStore, _store);
            await shutdown.ShutdownAsync(snapshot, "0.1.0").ConfigureAwait(false);
        }
        _autopilotOperation.Dispose();
        _autopilotCancellation.Dispose();
        _pccHttp.Dispose();
        _githubHttp.Dispose();
        await _store.DisposeAsync().ConfigureAwait(false);
        _projectLock?.Dispose();
    }

    private OrchestrationPhase CurrentOrchestrationPhase() => _run?.State switch
    {
        ProjectRunState.ManagerPlanning => OrchestrationPhase.ManagerPlanning,
        ProjectRunState.WaveReady => OrchestrationPhase.WaveValidation,
        ProjectRunState.Dispatching => OrchestrationPhase.Dispatching,
        ProjectRunState.WaveRunning => OrchestrationPhase.WaveRunning,
        ProjectRunState.Reconciling => OrchestrationPhase.Reconciling,
        ProjectRunState.ManagerReview => OrchestrationPhase.ManagerReview,
        ProjectRunState.ClosureMode => OrchestrationPhase.ClosureMode,
        ProjectRunState.VerifiedComplete => OrchestrationPhase.VerifiedComplete,
        ProjectRunState.BlockedExternal => OrchestrationPhase.BlockedExternal,
        ProjectRunState.StalledAutoStopped => OrchestrationPhase.StalledAutoStopped,
        ProjectRunState.StoppedByOperator => OrchestrationPhase.StoppedByOperator,
        _ => OrchestrationPhase.Initializing
    };

    private static LogicalAgentId AgentId(ProjectRunId runId, string role) => new(StableGuid($"agent:{runId}:{role}"));

    private static Guid StableGuid(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        var guid = bytes[..16];
        guid[6] = (byte)((guid[6] & 0x0f) | 0x50);
        guid[8] = (byte)((guid[8] & 0x3f) | 0x80);
        return new Guid(guid);
    }

    private sealed record SelectedProjectState(string ProjectControlId, string ProjectIdentity, string DisplayName, string Repository, Guid ProjectRunId);
    private sealed record DurableRuntimeHealth(bool Active, string State, string Reason, DateTimeOffset? ResumeNotBefore, bool RequiresHumanAction, string? RuntimeId);
    private sealed record DurableLoopGuard(IReadOnlyList<string> PlanFingerprints, IReadOnlyList<decimal> VerifiedCompletion, string? RuntimeErrorFingerprint, int RuntimeErrorCount, bool AutoStopped);

    private static ChatGptResilienceState ParseResilienceState(string value) => Enum.TryParse<ChatGptResilienceState>(value, true, out var state) ? state : ChatGptResilienceState.Paused;

    private sealed class EmptyCompletedTaskIndex : ICompletedTaskIndex
    {
        public static EmptyCompletedTaskIndex Instance { get; } = new();
        public bool IsCompleted(TaskId taskId) => false;
        public bool ContainsFingerprint(string fingerprint) => false;
    }
}



