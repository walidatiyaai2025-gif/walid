using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using PCCExecutive.App.Presentation;
using PCCExecutive.App.Services;

namespace PCCExecutive.App.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly IPccExecutivePresentationGateway _gateway;
    private readonly IConfirmationService _confirmation;
    private readonly Dictionary<ScreenId, ScreenViewModelBase> _screens;
    private RuntimeSnapshot _snapshot;
    private ScreenId _selectedScreen = ScreenId.Dashboard;
    private ScreenViewModelBase _currentScreen;
    private string? _lastUiError;
    private DispatchMode _selectedDispatchMode;
    private ProviderMode _selectedProviderMode;
    private string _projectQuery = string.Empty;
    private string? _selectedWorkerId;
    private bool _showConversationHistory;
    private string _taskWorkerFilter = string.Empty;
    private string _taskWaveFilter = string.Empty;
    private string _taskPriorityFilter = string.Empty;
    private string _taskBlockerFilter = string.Empty;

    public MainViewModel(IPccExecutivePresentationGateway gateway, IConfirmationService? confirmation = null)
    {
        _gateway = gateway;
        _confirmation = confirmation ?? new DenyConfirmationService();
        _snapshot = gateway.Snapshot;
        _selectedDispatchMode = _snapshot.DispatchSettings.Mode;
        _selectedProviderMode = _snapshot.ProviderMode;
        _selectedWorkerId = _snapshot.Workers.FirstOrDefault()?.Id;

        Navigation = new ObservableCollection<NavigationItem>
        {
            new(ScreenId.Dashboard, "Dashboard", "⌂"),
            new(ScreenId.ProjectSelection, "Projects", "▣"),
            new(ScreenId.ManagerWorkspace, "Manager", "◇"),
            new(ScreenId.WorkersDispatch, "Dispatch", "⇶"),
            new(ScreenId.WorkerChat, "Worker Chat", "◫"),
            new(ScreenId.WaveSummary, "Wave Summary", "≋"),
            new(ScreenId.TaskBoard, "Task Board", "☷"),
            new(ScreenId.EvidenceVerification, "Evidence", "✓"),
            new(ScreenId.LoopGuard, "Loop Guard", "⛨"),
            new(ScreenId.ChatGptHealth, "ChatGPT Health", "♡"),
            new(ScreenId.SessionMonitor, "Sessions", "◎"),
            new(ScreenId.ChromeConnection, "Chrome", "◉"),
            new(ScreenId.UpdateCenter, "Update Center", "↻"),
            new(ScreenId.AttentionCenter, "Attention", "!"),
            new(ScreenId.Settings, "Settings", "⚙")
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
            [ScreenId.AttentionCenter] = new AttentionCenterViewModel(this)
        };
        _currentScreen = _screens[_selectedScreen];

        NavigateCommand = new RelayCommand(Navigate);
        SelectWorkerCommand = new RelayCommand(SelectWorker);
        ToggleConversationHistoryCommand = new RelayCommand(_ => ShowConversationHistory = !ShowConversationHistory);

        RefreshCommand = GatewayCommand(UiAction.Refresh);
        ResolveProjectCommand = GatewayCommand(UiAction.ResolveProject, _ => ProjectQuery);
        SelectProjectCommand = GatewayCommand(UiAction.SelectProject, p => p?.ToString());
        ConnectChromeCommand = GatewayCommand(UiAction.ConnectChrome);
        RetryHealthCommand = GatewayCommand(UiAction.RetryHealth);
        PauseAiCommand = GatewayCommand(UiAction.PauseAi);
        ResumeAiCommand = GatewayCommand(UiAction.ResumeAi);
        RequestManagerPlanCommand = GatewayCommand(UiAction.RequestManagerPlan);
        StartDispatchCommand = GatewayCommand(UiAction.StartDispatch, _ => SelectedDispatchMode.ToString());
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
        InspectLoopGuardCommand = GatewayCommand(UiAction.InspectLoopGuard);
        ReplanLoopCommand = GatewayCommand(UiAction.ReplanLoop);
        ResumeLoopOnceCommand = GatewayCommand(UiAction.ResumeLoopOnce);
        StopLoopCommand = GatewayCommand(UiAction.StopLoop);
        OpenAttentionLocationCommand = GatewayCommand(UiAction.OpenAttentionLocation, p => p?.ToString());
        InstallUpdateCommand = GatewayCommand(UiAction.InstallUpdateAndRestart);
        CheckForUpdatesCommand = GatewayCommand(UiAction.CheckForUpdates);
        SaveSettingsCommand = GatewayCommand(UiAction.SaveSettings, _ =>
            $"provider={SelectedProviderMode};dispatch={SelectedDispatchMode}");

        gateway.SnapshotChanged += OnSnapshotChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<NavigationItem> Navigation { get; }
    public RuntimeSnapshot Snapshot { get => _snapshot; private set => Set(ref _snapshot, value); }
    public ScreenId SelectedScreen { get => _selectedScreen; private set => Set(ref _selectedScreen, value); }
    public ScreenViewModelBase CurrentScreen { get => _currentScreen; private set => Set(ref _currentScreen, value); }
    public string? LastUiError { get => _lastUiError; private set => Set(ref _lastUiError, value); }
    public bool HasUiError => !string.IsNullOrWhiteSpace(LastUiError);

    public string ProjectQuery
    {
        get => _projectQuery;
        set
        {
            if (Set(ref _projectQuery, value))
                ResolveProjectCommand.RaiseCanExecuteChanged();
        }
    }

    public DispatchMode SelectedDispatchMode
    {
        get => _selectedDispatchMode;
        set
        {
            if (!CanConfigureDispatch && value != _selectedDispatchMode) return;
            Set(ref _selectedDispatchMode, value);
        }
    }

    public ProviderMode SelectedProviderMode
    {
        get => _selectedProviderMode;
        set
        {
            if (!CanEditSettings && value != _selectedProviderMode)
            {
                LastUiError = SaveSettingsDisabledReason;
                return;
            }
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

    public IEnumerable<TaskSummary> TodoTasks => Snapshot.Tasks.Where(TaskMatchesFilters).Where(t =>
        string.Equals(t.State, "To Do", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(t.State, "Todo", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(t.State, "Proposed", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(t.State, "Ready", StringComparison.OrdinalIgnoreCase));
    public IEnumerable<TaskSummary> InProgressTasks => Snapshot.Tasks.Where(TaskMatchesFilters).Where(t =>
        string.Equals(t.State, "In Progress", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(t.State, "Running", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(t.State, "Assigned", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(t.State, "Dispatched", StringComparison.OrdinalIgnoreCase));
    public IEnumerable<TaskSummary> TestingTasks => Snapshot.Tasks.Where(TaskMatchesFilters).Where(t =>
        string.Equals(t.State, "Testing", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(t.State, "Validating", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(t.State, "HandoffReceived", StringComparison.OrdinalIgnoreCase) ||
        (!t.EvidenceVerified && string.Equals(t.State, "Done", StringComparison.OrdinalIgnoreCase)));
    public IEnumerable<TaskSummary> DoneTasks => Snapshot.Tasks.Where(TaskMatchesFilters).Where(t => t.EvidenceVerified &&
        (string.Equals(t.State, "Done", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(t.State, "Completed", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(t.State, "Verified", StringComparison.OrdinalIgnoreCase)));

    public string TaskWorkerFilter { get => _taskWorkerFilter; set { if (Set(ref _taskWorkerFilter, value)) RaiseTaskFilters(); } }
    public string TaskWaveFilter { get => _taskWaveFilter; set { if (Set(ref _taskWaveFilter, value)) RaiseTaskFilters(); } }
    public string TaskPriorityFilter { get => _taskPriorityFilter; set { if (Set(ref _taskPriorityFilter, value)) RaiseTaskFilters(); } }
    public string TaskBlockerFilter { get => _taskBlockerFilter; set { if (Set(ref _taskBlockerFilter, value)) RaiseTaskFilters(); } }

    public SessionSummary? ManagerSession => Snapshot.Sessions.FirstOrDefault(s =>
        string.Equals(s.LogicalName, "Manager", StringComparison.OrdinalIgnoreCase));
    public WorkerSummary? SelectedWorker => string.IsNullOrWhiteSpace(_selectedWorkerId)
        ? Snapshot.Workers.FirstOrDefault()
        : Snapshot.Workers.FirstOrDefault(w => string.Equals(w.Id, _selectedWorkerId, StringComparison.Ordinal))
          ?? Snapshot.Workers.FirstOrDefault();
    public SessionSummary? SelectedWorkerSession => SelectedWorker is null
        ? null
        : Snapshot.Sessions.FirstOrDefault(s =>
            string.Equals(s.LogicalName, SelectedWorker.LogicalName, StringComparison.OrdinalIgnoreCase));
    public int PccOwnedSessionCount => Snapshot.Sessions.Count(s => s.IsPccOwned);
    public string PccOwnedSessionCountText => Snapshot.GatewayBound ? PccOwnedSessionCount.ToString() : "—";

    public bool ShowConversationHistory
    {
        get => _showConversationHistory;
        set => Set(ref _showConversationHistory, value);
    }

    public bool CanEditSettings => _gateway.CanExecute(UiAction.SaveSettings);
    public bool CanConfigureDispatch => _gateway.CanExecute(UiAction.StartDispatch);
    public string? SaveSettingsDisabledReason => _gateway.DisabledReason(UiAction.SaveSettings);
    public string? StartDispatchDisabledReason => _gateway.DisabledReason(UiAction.StartDispatch);
    public string? ReconcileDisabledReason => _gateway.DisabledReason(UiAction.ReconcileWave);
    public string? VerificationDisabledReason => _gateway.DisabledReason(UiAction.RunVerification);
    public string? LoopActionsDisabledReason => _gateway.DisabledReason(UiAction.InspectLoopGuard);
    public string? CheckUpdateDisabledReason => _gateway.DisabledReason(UiAction.CheckForUpdates);
    public string? InstallUpdateDisabledReason => _gateway.DisabledReason(UiAction.InstallUpdateAndRestart);
    public string? PauseAiDisabledReason => _gateway.DisabledReason(UiAction.PauseAi);
    public string? RequestPlanDisabledReason => _gateway.DisabledReason(UiAction.RequestManagerPlan);

    public ICommand NavigateCommand { get; }
    public ICommand SelectWorkerCommand { get; }
    public ICommand ToggleConversationHistoryCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand ResolveProjectCommand { get; }
    public AsyncRelayCommand SelectProjectCommand { get; }
    public AsyncRelayCommand ConnectChromeCommand { get; }
    public AsyncRelayCommand RetryHealthCommand { get; }
    public AsyncRelayCommand PauseAiCommand { get; }
    public AsyncRelayCommand ResumeAiCommand { get; }
    public AsyncRelayCommand RequestManagerPlanCommand { get; }
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
    public AsyncRelayCommand InspectLoopGuardCommand { get; }
    public AsyncRelayCommand ReplanLoopCommand { get; }
    public AsyncRelayCommand ResumeLoopOnceCommand { get; }
    public AsyncRelayCommand StopLoopCommand { get; }
    public AsyncRelayCommand OpenAttentionLocationCommand { get; }
    public AsyncRelayCommand InstallUpdateCommand { get; }
    public AsyncRelayCommand CheckForUpdatesCommand { get; }
    public AsyncRelayCommand SaveSettingsCommand { get; }

    public void Navigate(ScreenId id)
    {
        SelectedScreen = id;
        CurrentScreen = _screens[id];
        LastUiError = null;
    }

    public void Navigate(object? parameter)
    {
        if (parameter is ScreenId id) Navigate(id);
        else if (parameter is NavigationItem item) Navigate(item.Id);
        else if (parameter is string text && Enum.TryParse<ScreenId>(text, out var parsed)) Navigate(parsed);
    }

    private void SelectWorker(object? parameter)
    {
        var id = parameter switch
        {
            WorkerSummary worker => worker.Id,
            string text => text,
            _ => null
        };
        if (string.IsNullOrWhiteSpace(id)) return;
        _selectedWorkerId = id;
        OnPropertyChanged(nameof(SelectedWorker));
        OnPropertyChanged(nameof(SelectedWorkerSession));
        RaiseAllCommands();
    }

    private AsyncRelayCommand GatewayCommand(UiAction action, Func<object?, string?>? target = null) =>
        new(
            async (p, ct) =>
            {
                LastUiError = null;
                await _gateway.ExecuteAsync(action, target?.Invoke(p), ct);
            },
            p => _gateway.CanExecute(action, target?.Invoke(p)),
            ex => LastUiError = ex.Message);

    private AsyncRelayCommand ConfirmedGatewayCommand(
        UiAction action,
        string title,
        string message,
        string confirmLabel,
        Func<object?, string?>? target = null) =>
        new(
            async (p, ct) =>
            {
                LastUiError = null;
                if (!_confirmation.Confirm(title, message, confirmLabel)) return;
                await _gateway.ExecuteAsync(action, target?.Invoke(p), ct);
            },
            p => _gateway.CanExecute(action, target?.Invoke(p)),
            ex => LastUiError = ex.Message);

    private void OnSnapshotChanged(object? sender, RuntimeSnapshot snapshot)
    {
        Snapshot = snapshot;
        _selectedDispatchMode = snapshot.DispatchSettings.Mode;
        _selectedProviderMode = snapshot.ProviderMode;
        if (SelectedWorker is null || !snapshot.Workers.Any(w => w.Id == _selectedWorkerId))
            _selectedWorkerId = snapshot.Workers.FirstOrDefault()?.Id;

        OnPropertyChanged(nameof(SelectedDispatchMode));
        OnPropertyChanged(nameof(SelectedProviderMode));
        OnPropertyChanged(nameof(IsBrowserProviderSelected));
        OnPropertyChanged(nameof(IsOpenAiProviderSelected));
        OnPropertyChanged(nameof(IsHybridProviderSelected));
        OnPropertyChanged(nameof(TodoTasks));
        OnPropertyChanged(nameof(InProgressTasks));
        OnPropertyChanged(nameof(TestingTasks));
        OnPropertyChanged(nameof(DoneTasks));
        OnPropertyChanged(nameof(ManagerSession));
        OnPropertyChanged(nameof(SelectedWorker));
        OnPropertyChanged(nameof(SelectedWorkerSession));
        OnPropertyChanged(nameof(PccOwnedSessionCount));
        OnPropertyChanged(nameof(PccOwnedSessionCountText));
        OnPropertyChanged(nameof(CanEditSettings));
        OnPropertyChanged(nameof(CanConfigureDispatch));
        OnPropertyChanged(nameof(SaveSettingsDisabledReason));
        OnPropertyChanged(nameof(StartDispatchDisabledReason));
        OnPropertyChanged(nameof(ReconcileDisabledReason));
        OnPropertyChanged(nameof(VerificationDisabledReason));
        OnPropertyChanged(nameof(LoopActionsDisabledReason));
        OnPropertyChanged(nameof(CheckUpdateDisabledReason));
        OnPropertyChanged(nameof(InstallUpdateDisabledReason));
        OnPropertyChanged(nameof(PauseAiDisabledReason));
        OnPropertyChanged(nameof(RequestPlanDisabledReason));
        RaiseAllCommands();
        OnPropertyChanged(nameof(HasUiError));
    }

    private void RaiseAllCommands()
    {
        RefreshCommand.RaiseCanExecuteChanged();
        ResolveProjectCommand.RaiseCanExecuteChanged();
        SelectProjectCommand.RaiseCanExecuteChanged();
        ConnectChromeCommand.RaiseCanExecuteChanged();
        RetryHealthCommand.RaiseCanExecuteChanged();
        PauseAiCommand.RaiseCanExecuteChanged();
        ResumeAiCommand.RaiseCanExecuteChanged();
        RequestManagerPlanCommand.RaiseCanExecuteChanged();
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
        InspectLoopGuardCommand.RaiseCanExecuteChanged();
        ReplanLoopCommand.RaiseCanExecuteChanged();
        ResumeLoopOnceCommand.RaiseCanExecuteChanged();
        StopLoopCommand.RaiseCanExecuteChanged();
        OpenAttentionLocationCommand.RaiseCanExecuteChanged();
        InstallUpdateCommand.RaiseCanExecuteChanged();
        CheckForUpdatesCommand.RaiseCanExecuteChanged();
        SaveSettingsCommand.RaiseCanExecuteChanged();
    }

    private bool TaskMatchesFilters(TaskSummary task)
    {
        static bool Match(string? value, string filter) =>
            string.IsNullOrWhiteSpace(filter) ||
            (!string.IsNullOrWhiteSpace(value) && value.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase));

        return Match(task.Owner, TaskWorkerFilter) &&
               Match(task.Wave, TaskWaveFilter) &&
               Match(task.Priority, TaskPriorityFilter) &&
               Match(task.Blocker, TaskBlockerFilter);
    }

    private void RaiseTaskFilters()
    {
        OnPropertyChanged(nameof(TodoTasks));
        OnPropertyChanged(nameof(InProgressTasks));
        OnPropertyChanged(nameof(TestingTasks));
        OnPropertyChanged(nameof(DoneTasks));
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
