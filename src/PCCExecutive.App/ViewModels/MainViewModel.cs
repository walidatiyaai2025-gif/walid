using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using PCCExecutive.App.Presentation;
using PCCExecutive.App.Services;
using PCCExecutive.Application;

namespace PCCExecutive.App.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly IPccExecutivePresentationGateway _gateway;
    private readonly IConfirmationService _confirmation;
    private readonly RuntimeInspectorServices? _runtimeInspector;
    private readonly Dictionary<ScreenId, ScreenViewModelBase> _screens;
    private readonly GuidedExecutionEvaluator _guidedEvaluator = new();
    private readonly GuidedNavigationGuard _navigationGuard;
    private RuntimeSnapshot _snapshot;
    private ScreenId _selectedScreen;
    private ScreenViewModelBase _currentScreen = null!;
    private string? _lastUiError;
    private DispatchMode _selectedDispatchMode;
    private ProviderMode _selectedProviderMode;
    private int _selectedBaseIntervalSeconds;
    private int _selectedMaxWorkers;
    private bool _selectedAdaptivePacing;
    private bool _selectedAutoResume;
    private GuidedExecutionEvaluation _guidedExecution = null!;
    private NavigationGuardResult? _blockedNavigation;

    public MainViewModel(IPccExecutivePresentationGateway gateway, IConfirmationService? confirmation = null, RuntimeInspectorServices? runtimeInspector = null)
    {
        _gateway = gateway;
        _navigationGuard = new(_guidedEvaluator);
        _confirmation = confirmation ?? new DenyConfirmationService();
        _runtimeInspector = runtimeInspector;
        _snapshot = gateway.Snapshot;
        _selectedDispatchMode = _snapshot.DispatchSettings.Mode;
        _selectedProviderMode = ProviderMode.BrowserWeb;
        _selectedBaseIntervalSeconds = _snapshot.DispatchSettings.BaseIntervalSeconds;
        _selectedMaxWorkers = _snapshot.DispatchSettings.MaxWorkers;
        _selectedAdaptivePacing = _snapshot.DispatchSettings.AdaptivePacing;
        _selectedAutoResume = _snapshot.DispatchSettings.AutoResume;

        Navigation = new ObservableCollection<NavigationItem>
        {
            new(ScreenId.ChromeConnection, "01  Chrome", "◉"),
            new(ScreenId.ProjectSelection, "02  Projects", "▣"),
            new(ScreenId.Dashboard, "03  Dashboard", "⌂"),
            new(ScreenId.ManagerWorkspace, "04  Manager", "◇"),
            new(ScreenId.WorkersDispatch, "05  Dispatch", "⇶"),
            new(ScreenId.WorkerChat, "06  Worker Chat", "◫"),
            new(ScreenId.WaveSummary, "07  Wave Summary", "≋"),
            new(ScreenId.TaskBoard, "08  Task Board", "☷"),
            new(ScreenId.EvidenceVerification, "09  Evidence", "✓"),
            new(ScreenId.LoopGuard, "10  Loop Guard", "⛨"),
            new(ScreenId.ChatGptHealth, "11  ChatGPT Health", "♡"),
            new(ScreenId.SessionMonitor, "12  Sessions", "◎"),
            new(ScreenId.Settings, "13  Settings", "⚙"),
            new(ScreenId.UpdateCenter, "14  Update Center", "↻"),
            new(ScreenId.AttentionCenter, "15  Attention", "!"),
            new(ScreenId.RuntimeInspector, "16  Runtime Inspector", "⌁"),
            new(ScreenId.ConversationHistory, "History", "↺")
        };

        _screens = new()
        {
            [ScreenId.ChromeConnection] = new ChromeConnectionViewModel(this),
            [ScreenId.ProjectSelection] = new ProjectSelectionViewModel(this),
            [ScreenId.Dashboard] = new DashboardViewModel(this),
            [ScreenId.ManagerWorkspace] = new ManagerWorkspaceViewModel(this),
            [ScreenId.WorkersDispatch] = new WorkersDispatchViewModel(this),
            [ScreenId.WorkerChat] = new WorkerChatViewModel(this),
            [ScreenId.WaveSummary] = new WaveSummaryViewModel(this),
            [ScreenId.TaskBoard] = new TaskBoardViewModel(this),
            [ScreenId.EvidenceVerification] = new EvidenceVerificationViewModel(this),
            [ScreenId.LoopGuard] = new LoopGuardViewModel(this),
            [ScreenId.ChatGptHealth] = new ChatGptHealthViewModel(this),
            [ScreenId.SessionMonitor] = new SessionMonitorViewModel(this),
            [ScreenId.Settings] = new SettingsViewModel(this),
            [ScreenId.UpdateCenter] = new UpdateCenterViewModel(this),
            [ScreenId.AttentionCenter] = new AttentionCenterViewModel(this),
            [ScreenId.RuntimeInspector] = new RuntimeInspectorViewModel(this, runtimeInspector),
            [ScreenId.ConversationHistory] = new ConversationHistoryViewModel(this)
        };
        _guidedExecution = _guidedEvaluator.Evaluate(CreateGuidedRuntimeState());
        _selectedScreen = _guidedExecution.NextAction.Kind == GuidedActionKind.None
            ? ScreenId.Dashboard
            : ScreenForStep(_guidedExecution.NextAction.Step);
        _currentScreen = _screens[_selectedScreen];
        ApplyGuidedProjection();

        NavigateCommand = new RelayCommand(p => Navigate(p));
        GoToRequiredStepCommand = new RelayCommand(_ => GoToRequiredStep(), _ => BlockedNavigation?.NextAction.Step is not null);
        RefreshCommand = GatewayCommand(UiAction.Refresh);
        SelectProjectCommand = new AsyncRelayCommand(
            async p =>
            {
                LastUiError = null;
                var correlation = _runtimeInspector?.Collector.BeginCorrelation();
                RecordDiagnostic(RuntimeDiagnosticKind.UserAction, "PROJECT_SELECTION_INVOKED", "Project selection invoked.", correlation, screen: SelectedScreen.ToString(), command: UiAction.SelectProject.ToString(), target: p?.ToString());
                await _gateway.ExecuteAsync(UiAction.SelectProject, p?.ToString());
                if (_gateway.Snapshot.HasActiveRun)
                {
                    // Zero-touch handoff: once the owner explicitly opens a canonical project,
                    // establishing/recovering the PCC-owned Manager Chrome runtime is routine work.
                    // Do it automatically instead of forcing the owner to discover another button.
                    if (_gateway.CanExecute(UiAction.ConnectChrome))
                        await _gateway.ExecuteAsync(UiAction.ConnectChrome);

                    var managerReady = _gateway.Snapshot.Sessions.Any(s =>
                        s.IsPccOwned && string.Equals(s.Role, "Manager", StringComparison.OrdinalIgnoreCase));
                    Navigate(managerReady ? ScreenId.ManagerWorkspace : ScreenId.ChromeConnection);
                }
                else
                {
                    LastUiError = "Project selection did not resolve to a canonical PCC project. Review the project state and try again.";
                }
            },
            p => _gateway.CanExecute(UiAction.SelectProject, p?.ToString()),
            ex => LastUiError = ex.Message);
        ConnectChromeCommand = GatewayCommand(UiAction.ConnectChrome);
        PauseAiCommand = GatewayCommand(UiAction.PauseAi);
        ResumeAiCommand = GatewayCommand(UiAction.ResumeAi);
        StartManagerCommand = GatewayCommand(UiAction.StartManager, requiredStep: GuidedStepId.Manager);
        StartDispatchCommand = GatewayCommand(UiAction.StartDispatch, _ => SelectedDispatchMode.ToString(), GuidedStepId.Orchestration);
        PauseDispatchCommand = GatewayCommand(UiAction.PauseDispatch);
        OpenSessionCommand = GatewayCommand(UiAction.OpenSession, p => p?.ToString());
        BringSessionToFrontCommand = GatewayCommand(UiAction.BringSessionToFront, p => p?.ToString());
        HideSessionCommand = GatewayCommand(UiAction.HideSession, p => p?.ToString());
        RestartSessionCommand = GatewayCommand(UiAction.RestartSession, p => p?.ToString());
        KillSessionCommand = ConfirmedGatewayCommand(
            UiAction.KillSession,
            "Kill PCC Session?",
            "This stops only the selected session after the runtime has positively proven PCC ownership. Personal Chrome is excluded.",
            "Kill Session",
            p => p?.ToString());
        KillAllPccSessionsCommand = ConfirmedGatewayCommand(
            UiAction.KillAllPccSessions,
            "Kill All PCC Sessions?",
            "This stops only sessions with positive PCC ownership evidence. Personal or unknown Chrome processes are excluded.",
            "Kill PCC Sessions");
        ReconcileWaveCommand = GatewayCommand(UiAction.ReconcileWave);
        RunVerificationCommand = GatewayCommand(UiAction.RunVerification);
        OpenAttentionLocationCommand = GatewayCommand(UiAction.OpenAttentionLocation, p => p?.ToString());
        InstallUpdateCommand = GatewayCommand(UiAction.InstallUpdateAndRestart);
        CheckForUpdatesCommand = GatewayCommand(UiAction.CheckForUpdates);
        SaveSettingsCommand = GatewayCommand(UiAction.SaveSettings, _ => $"provider={SelectedProviderMode};dispatch={SelectedDispatchMode};interval={SelectedBaseIntervalSeconds};maxWorkers={SelectedMaxWorkers};adaptive={SelectedAdaptivePacing};autoResume={SelectedAutoResume}");
        ConversationHistoryCommand = new AsyncRelayCommand(
            async p =>
            {
                LastUiError = null;
                var correlation = _runtimeInspector?.Collector.BeginCorrelation();
                RecordDiagnostic(RuntimeDiagnosticKind.UserAction, "CONVERSATION_HISTORY_INVOKED", "Conversation history requested.", correlation, screen: SelectedScreen.ToString(), command: UiAction.OpenConversationHistory.ToString(), target: p?.ToString());
                await _gateway.ExecuteAsync(UiAction.OpenConversationHistory, p?.ToString());
                Navigate(ScreenId.ConversationHistory);
            },
            p => _gateway.CanExecute(UiAction.OpenConversationHistory, p?.ToString()),
            ex => LastUiError = ex.Message);

        gateway.SnapshotChanged += OnSnapshotChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<NavigationItem> Navigation { get; }
    public RuntimeSnapshot Snapshot { get => _snapshot; private set => Set(ref _snapshot, value); }
    public ScreenId SelectedScreen { get => _selectedScreen; private set => Set(ref _selectedScreen, value); }
    public ScreenViewModelBase CurrentScreen { get => _currentScreen; private set => Set(ref _currentScreen, value); }
    public string? LastUiError { get => _lastUiError; private set => Set(ref _lastUiError, value); }
    public bool HasUiError => !string.IsNullOrWhiteSpace(LastUiError);
    public GuidedExecutionEvaluation GuidedExecution { get => _guidedExecution; private set => Set(ref _guidedExecution, value); }
    public GuidedNextAction NextAction => GuidedExecution.NextAction;
    public NavigationGuardResult? BlockedNavigation { get => _blockedNavigation; private set { if (Set(ref _blockedNavigation, value)) OnPropertyChanged(nameof(HasBlockedNavigation)); } }
    public bool HasBlockedNavigation => BlockedNavigation is not null;
    public string BlockedActionTitle => BlockedNavigation is null ? string.Empty : $"Cannot open {GuidedExecutionEvaluator.NumberedName(BlockedNavigation.AttemptedStep)}";
    public string BlockedActionDetail => BlockedNavigation?.MissingPrerequisite is not { } missing ? string.Empty :
        $"Required prerequisite: {GuidedExecutionEvaluator.NumberedName(missing.RequiredStep ?? missing.Step)} — {missing.Reason} " +
        (missing.AutomaticallyRecoverable ? "PCC Executive is recovering automatically; no operator action is required yet." : $"Required action: {missing.RequiredControl ?? "Review Status"}.");

    public DispatchMode SelectedDispatchMode
    {
        get => _selectedDispatchMode;
        set => Set(ref _selectedDispatchMode, value);
    }

    public ProviderMode SelectedProviderMode
    {
        get => _selectedProviderMode;
        set
        {
            if (value is ProviderMode.OpenAiApi or ProviderMode.Hybrid && !Snapshot.ApiConfigured)
            {
                LastUiError = "OpenAI API / Hybrid stays disabled until the API provider is explicitly configured.";
                return;
            }
            if (Set(ref _selectedProviderMode, value))
            {
                OnPropertyChanged(nameof(IsBrowserProviderSelected));
                OnPropertyChanged(nameof(IsOpenAiProviderSelected));
                OnPropertyChanged(nameof(IsHybridProviderSelected));
            }
        }
    }

    public bool IsBrowserProviderSelected
    {
        get => SelectedProviderMode == ProviderMode.BrowserWeb;
        set { if (value) SelectedProviderMode = ProviderMode.BrowserWeb; }
    }

    public bool IsOpenAiProviderSelected
    {
        get => SelectedProviderMode == ProviderMode.OpenAiApi;
        set { if (value) SelectedProviderMode = ProviderMode.OpenAiApi; }
    }

    public bool IsHybridProviderSelected
    {
        get => SelectedProviderMode == ProviderMode.Hybrid;
        set { if (value) SelectedProviderMode = ProviderMode.Hybrid; }
    }

    public IReadOnlyList<DispatchMode> DispatchModes { get; } = Enum.GetValues<DispatchMode>();
    public IReadOnlyList<ProviderMode> ProviderModes { get; } = Enum.GetValues<ProviderMode>();
    public int SelectedBaseIntervalSeconds { get => _selectedBaseIntervalSeconds; set => Set(ref _selectedBaseIntervalSeconds, value); }
    public int SelectedMaxWorkers { get => _selectedMaxWorkers; set => Set(ref _selectedMaxWorkers, value); }
    public bool SelectedAdaptivePacing { get => _selectedAdaptivePacing; set => Set(ref _selectedAdaptivePacing, value); }
    public bool SelectedAutoResume { get => _selectedAutoResume; set => Set(ref _selectedAutoResume, value); }

    public IEnumerable<TaskSummary> TodoTasks => Snapshot.Tasks.Where(t => string.Equals(t.State, "To Do", StringComparison.OrdinalIgnoreCase) || string.Equals(t.State, "Todo", StringComparison.OrdinalIgnoreCase));
    public IEnumerable<TaskSummary> InProgressTasks => Snapshot.Tasks.Where(t => string.Equals(t.State, "In Progress", StringComparison.OrdinalIgnoreCase) || string.Equals(t.State, "Running", StringComparison.OrdinalIgnoreCase));
    public IEnumerable<TaskSummary> TestingTasks => Snapshot.Tasks.Where(t =>
        string.Equals(t.State, "Testing", StringComparison.OrdinalIgnoreCase) ||
        (!t.EvidenceVerified && string.Equals(t.State, "Done", StringComparison.OrdinalIgnoreCase)));
    public IEnumerable<TaskSummary> DoneTasks => Snapshot.Tasks.Where(t => t.EvidenceVerified &&
        (string.Equals(t.State, "Done", StringComparison.OrdinalIgnoreCase) || string.Equals(t.State, "Verified", StringComparison.OrdinalIgnoreCase)));
    public SessionSummary? ManagerSession => Snapshot.Sessions.FirstOrDefault(s => string.Equals(s.LogicalName, "Manager", StringComparison.OrdinalIgnoreCase));
    public WorkerSummary? SelectedWorker => Snapshot.Workers.FirstOrDefault();
    public SessionSummary? SelectedWorkerSession => SelectedWorker is null
        ? null
        : Snapshot.Sessions.FirstOrDefault(s => string.Equals(s.LogicalName, SelectedWorker.LogicalName, StringComparison.OrdinalIgnoreCase));
    public int PccOwnedSessionCount => Snapshot.Sessions.Count(s => s.IsPccOwned);
    public string PccOwnedSessionCountText => Snapshot.GatewayBound ? PccOwnedSessionCount.ToString() : "—";

    public ICommand NavigateCommand { get; }
    public RelayCommand GoToRequiredStepCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand SelectProjectCommand { get; }
    public AsyncRelayCommand ConnectChromeCommand { get; }
    public AsyncRelayCommand PauseAiCommand { get; }
    public AsyncRelayCommand ResumeAiCommand { get; }
    public AsyncRelayCommand StartManagerCommand { get; }
    public AsyncRelayCommand StartDispatchCommand { get; }
    public AsyncRelayCommand PauseDispatchCommand { get; }
    public AsyncRelayCommand OpenSessionCommand { get; }
    public AsyncRelayCommand BringSessionToFrontCommand { get; }
    public AsyncRelayCommand HideSessionCommand { get; }
    public AsyncRelayCommand RestartSessionCommand { get; }
    public AsyncRelayCommand KillSessionCommand { get; }
    public AsyncRelayCommand KillAllPccSessionsCommand { get; }
    public AsyncRelayCommand ReconcileWaveCommand { get; }
    public AsyncRelayCommand RunVerificationCommand { get; }
    public AsyncRelayCommand OpenAttentionLocationCommand { get; }
    public AsyncRelayCommand InstallUpdateCommand { get; }
    public AsyncRelayCommand CheckForUpdatesCommand { get; }
    public AsyncRelayCommand SaveSettingsCommand { get; }
    public AsyncRelayCommand ConversationHistoryCommand { get; }

    public void Navigate(ScreenId id)
    {
        var correlation = _runtimeInspector?.Collector.BeginCorrelation();
        // Project selection is a safe preparatory screen: the operator may inspect/select a
        // project while Chrome remains the canonical current prerequisite. Opening a project
        // does not itself start Manager/dispatch execution.
        if (id != ScreenId.ProjectSelection && TryMapGuidedStep(id, out var step))
        {
            var guard = _navigationGuard.Evaluate(CreateGuidedRuntimeState(), step);
            if (!guard.Allowed)
            {
                RecordGuardDecision(guard, correlation);
                BlockedNavigation = guard;
                LastUiError = $"{BlockedActionTitle}. {BlockedActionDetail}";
                RaiseBlockedProperties();
                RuntimeDiagnosticEmitted?.Invoke(this, new(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow,
                    RuntimeDiagnosticKind.GuardDecision, guard.MissingPrerequisite?.ReasonCode ?? "NAVIGATION_BLOCKED",
                    LastUiError, Screen: id.ToString(), Control: "Navigation", Target: id.ToString(), Allowed: false));
                return;
            }

            RecordGuardDecision(guard, correlation);
        }
        RecordDiagnostic(RuntimeDiagnosticKind.Navigation, "NAVIGATION_ALLOWED", $"Navigation to {id} allowed.", correlation, screen: SelectedScreen.ToString(), target: id.ToString(), allowed: true);
        SelectedScreen = id;
        CurrentScreen = _screens[id];
        LastUiError = null;
        BlockedNavigation = null;
        RaiseBlockedProperties();
        RuntimeDiagnosticEmitted?.Invoke(this, new(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow,
            RuntimeDiagnosticKind.Navigation, "NAVIGATION_ALLOWED", $"Opened {id}.", Screen: id.ToString(), Control: "Navigation", Target: id.ToString(), Allowed: true));
    }

    public void Navigate(object? parameter)
    {
        if (parameter is ScreenId id) Navigate(id);
        else if (parameter is NavigationItem item) Navigate(item.Id);
        else if (parameter is string text && Enum.TryParse<ScreenId>(text, out var parsed)) Navigate(parsed);
    }

    private AsyncRelayCommand GatewayCommand(UiAction action, Func<object?, string?>? target = null, GuidedStepId? requiredStep = null) =>
        new(
            async p =>
            {
                LastUiError = null;
                var correlation = _runtimeInspector?.Collector.BeginCorrelation();
                var destination = target?.Invoke(p);
                RecordDiagnostic(RuntimeDiagnosticKind.UserAction, "COMMAND_INVOKED", $"Command {action} invoked.", correlation, screen: SelectedScreen.ToString(), command: action.ToString(), target: destination);
                try
                {
                    await _gateway.ExecuteAsync(action, destination);
                    RecordDiagnostic(RuntimeDiagnosticKind.Command, "COMMAND_COMPLETED", $"Command {action} completed.", correlation, screen: SelectedScreen.ToString(), command: action.ToString(), target: destination, allowed: true);
                }
                catch (Exception ex)
                {
                    RecordDiagnostic(RuntimeDiagnosticKind.Exception, "COMMAND_FAILED", $"Command {action} failed: {ex.GetType().Name}.", correlation, screen: SelectedScreen.ToString(), command: action.ToString(), target: destination, allowed: false, exceptionClassification: ex.GetType().Name);
                    throw;
                }
            },
            p => _gateway.CanExecute(action, target?.Invoke(p)) && (requiredStep is null || IsCommandAllowed(requiredStep.Value)),
            ex => LastUiError = ex.Message);

    public void RecordGuardDecision(NavigationGuardResult result, Guid? correlationId = null) =>
        RecordDiagnostic(RuntimeDiagnosticKind.GuardDecision,
            result.Allowed ? "GUARD_ALLOWED" : result.MissingPrerequisite?.ReasonCode ?? "GUARD_BLOCKED",
            result.Allowed ? $"Guard allowed {result.AttemptedStep}." : $"Guard blocked {result.AttemptedStep}; required step {result.MissingPrerequisite?.RequiredStep}.",
            correlationId, screen: SelectedScreen.ToString(), target: result.AttemptedStep.ToString(), allowed: result.Allowed,
            afterState: result.NextAction.Instruction,
            details: result.MissingPrerequisite is null ? null : [new("requiredStep", result.MissingPrerequisite.RequiredStep?.ToString()), new("requiredControl", result.MissingPrerequisite.RequiredControl)]);

    private void RecordDiagnostic(RuntimeDiagnosticKind kind, string reason, string summary, Guid? correlationId = null,
        string? screen = null, string? control = null, string? command = null, string? target = null, bool? allowed = null,
        string? beforeState = null, string? afterState = null, string? exceptionClassification = null, IReadOnlyList<RuntimeDiagnosticDetail>? details = null)
    {
        if (_runtimeInspector is null) return;
        var record = _runtimeInspector.Collector.Create(kind, reason, summary, correlationId, screen, control, command, target, allowed, beforeState, afterState,
            Snapshot.HasActiveRun ? Snapshot.Projects.FirstOrDefault()?.Id : null, exceptionClassification: exceptionClassification, details: details);
        _ = _runtimeInspector.Collector.RecordAsync(record);
    }

    private AsyncRelayCommand ConfirmedGatewayCommand(
        UiAction action,
        string title,
        string message,
        string confirmLabel,
        Func<object?, string?>? target = null) =>
        new(
            async p =>
            {
                LastUiError = null;
                var correlation = _runtimeInspector?.Collector.BeginCorrelation();
                var destination = target?.Invoke(p);
                RecordDiagnostic(RuntimeDiagnosticKind.UserAction, "DESTRUCTIVE_COMMAND_REQUESTED", $"Confirmation requested for {action}.", correlation, screen: SelectedScreen.ToString(), command: action.ToString(), target: destination);
                if (!_confirmation.Confirm(title, message, confirmLabel))
                {
                    RecordDiagnostic(RuntimeDiagnosticKind.GuardDecision, "OPERATOR_CONFIRMATION_DENIED", $"Operator declined {action}.", correlation, screen: SelectedScreen.ToString(), command: action.ToString(), target: destination, allowed: false);
                    return;
                }
                await _gateway.ExecuteAsync(action, destination);
                RecordDiagnostic(RuntimeDiagnosticKind.Command, "CONFIRMED_COMMAND_COMPLETED", $"Confirmed command {action} completed.", correlation, screen: SelectedScreen.ToString(), command: action.ToString(), target: destination, allowed: true);
            },
            p => _gateway.CanExecute(action, target?.Invoke(p)),
            ex => LastUiError = ex.Message);

    private void OnSnapshotChanged(object? sender, RuntimeSnapshot snapshot)
    {
        var before = $"health={Snapshot.GlobalHealth};autopilot={Snapshot.AutopilotState};sessions={Snapshot.Sessions.Count}";
        var after = $"health={snapshot.GlobalHealth};autopilot={snapshot.AutopilotState};sessions={snapshot.Sessions.Count}";
        RecordDiagnostic(RuntimeDiagnosticKind.StateTransition, "RUNTIME_SNAPSHOT_CHANGED", "Canonical runtime snapshot changed.", beforeState: before, afterState: after);
        Snapshot = snapshot;
        if (snapshot.ProviderMode == ProviderMode.BrowserWeb || snapshot.ApiConfigured)
            SelectedProviderMode = snapshot.ProviderMode;
        OnPropertyChanged(nameof(TodoTasks));
        OnPropertyChanged(nameof(InProgressTasks));
        OnPropertyChanged(nameof(TestingTasks));
        OnPropertyChanged(nameof(DoneTasks));
        OnPropertyChanged(nameof(ManagerSession));
        OnPropertyChanged(nameof(SelectedWorker));
        OnPropertyChanged(nameof(SelectedWorkerSession));
        OnPropertyChanged(nameof(PccOwnedSessionCount));
        OnPropertyChanged(nameof(PccOwnedSessionCountText));
        GuidedExecution = _guidedEvaluator.Evaluate(CreateGuidedRuntimeState());
        ApplyGuidedProjection();
        OnPropertyChanged(nameof(NextAction));
        RaiseAllCommands();
        OnPropertyChanged(nameof(HasUiError));
    }

    public event EventHandler<RuntimeDiagnosticEvent>? RuntimeDiagnosticEmitted;

    private GuidedRuntimeState CreateGuidedRuntimeState()
    {
        var manager = Snapshot.Sessions.Any(s => s.IsPccOwned && string.Equals(s.Role, "Manager", StringComparison.OrdinalIgnoreCase));
        var browserState = Snapshot.GlobalHealth switch
        {
            HealthState.Healthy or HealthState.Slow or HealthState.Throttled or HealthState.RateLimited or HealthState.Cooldown or HealthState.PartialResponse => BrowserRecoveryState.Ready,
            HealthState.Recovering or HealthState.Offline or HealthState.Stuck or HealthState.TemporaryError => BrowserRecoveryState.RecoveringRuntime,
            HealthState.LoginRequired or HealthState.Challenge => BrowserRecoveryState.LoginRequired,
            HealthState.AdapterUncertain => BrowserRecoveryState.OwnershipUncertain,
            _ when manager => BrowserRecoveryState.Ready,
            _ => BrowserRecoveryState.Unknown,
        };
        var managerPlanning = manager && !Snapshot.ManagerNeedsStart && !string.IsNullOrWhiteSpace(Snapshot.CurrentWave) && Snapshot.CurrentWave != "—";
        return new(Snapshot.GatewayBound, SelectedProviderMode == ProviderMode.BrowserWeb, browserState,
            Snapshot.HasActiveRun, Snapshot.HasActiveRun, Snapshot.HasActiveRun, manager, managerPlanning,
            managerPlanning && _gateway.CanExecute(UiAction.StartDispatch), Snapshot.GlobalHealth == HealthState.AdapterUncertain);
    }

    private bool IsCommandAllowed(GuidedStepId step)
    {
        var result = GuidedExecution[step];
        return result.Satisfied || result.State == GuidedStepState.Current;
    }

    private void ApplyGuidedProjection()
    {
        foreach (var item in Navigation)
            if (TryMapGuidedStep(item.Id, out var step)) item.Apply(GuidedExecution[step]);
    }

    private void GoToRequiredStep()
    {
        if (BlockedNavigation is not { } blocked) return;
        var screen = ScreenForStep(blocked.NextAction.Step);
        BlockedNavigation = null;
        RaiseBlockedProperties();
        Navigate(screen);
    }

    private void RaiseBlockedProperties()
    {
        OnPropertyChanged(nameof(BlockedActionTitle));
        OnPropertyChanged(nameof(BlockedActionDetail));
        OnPropertyChanged(nameof(HasBlockedNavigation));
        GoToRequiredStepCommand.RaiseCanExecuteChanged();
    }

    private static bool TryMapGuidedStep(ScreenId screen, out GuidedStepId step)
    {
        step = screen switch
        {
            ScreenId.ChromeConnection => GuidedStepId.Chrome,
            ScreenId.ProjectSelection or ScreenId.Dashboard => GuidedStepId.Project,
            ScreenId.ManagerWorkspace => GuidedStepId.Manager,
            ScreenId.WorkersDispatch or ScreenId.WorkerChat or ScreenId.WaveSummary or ScreenId.TaskBoard or ScreenId.EvidenceVerification or ScreenId.LoopGuard => GuidedStepId.Orchestration,
            _ => default,
        };
        return screen is ScreenId.ChromeConnection or ScreenId.ProjectSelection or ScreenId.Dashboard or ScreenId.ManagerWorkspace or
            ScreenId.WorkersDispatch or ScreenId.WorkerChat or ScreenId.WaveSummary or ScreenId.TaskBoard or ScreenId.EvidenceVerification or ScreenId.LoopGuard;
    }

    private static ScreenId ScreenForStep(GuidedStepId step) => step switch
    {
        GuidedStepId.Chrome => ScreenId.ChromeConnection,
        GuidedStepId.Project => ScreenId.ProjectSelection,
        GuidedStepId.Manager => ScreenId.ManagerWorkspace,
        _ => ScreenId.WorkersDispatch,
    };

    private void RaiseAllCommands()
    {
        RefreshCommand.RaiseCanExecuteChanged();
        SelectProjectCommand.RaiseCanExecuteChanged();
        ConnectChromeCommand.RaiseCanExecuteChanged();
        PauseAiCommand.RaiseCanExecuteChanged();
        ResumeAiCommand.RaiseCanExecuteChanged();
        StartManagerCommand.RaiseCanExecuteChanged();
        StartDispatchCommand.RaiseCanExecuteChanged();
        PauseDispatchCommand.RaiseCanExecuteChanged();
        OpenSessionCommand.RaiseCanExecuteChanged();
        BringSessionToFrontCommand.RaiseCanExecuteChanged();
        HideSessionCommand.RaiseCanExecuteChanged();
        RestartSessionCommand.RaiseCanExecuteChanged();
        KillSessionCommand.RaiseCanExecuteChanged();
        KillAllPccSessionsCommand.RaiseCanExecuteChanged();
        ReconcileWaveCommand.RaiseCanExecuteChanged();
        RunVerificationCommand.RaiseCanExecuteChanged();
        OpenAttentionLocationCommand.RaiseCanExecuteChanged();
        InstallUpdateCommand.RaiseCanExecuteChanged();
        CheckForUpdatesCommand.RaiseCanExecuteChanged();
        SaveSettingsCommand.RaiseCanExecuteChanged();
        ConversationHistoryCommand.RaiseCanExecuteChanged();
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        if (name == nameof(LastUiError)) OnPropertyChanged(nameof(HasUiError));
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
