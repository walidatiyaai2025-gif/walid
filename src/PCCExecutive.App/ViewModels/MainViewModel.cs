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
    private ScreenId _selectedScreen;
    private ScreenViewModelBase _currentScreen = null!;
    private string? _lastUiError;
    private DispatchMode _selectedDispatchMode;
    private ProviderMode _selectedProviderMode;

    public MainViewModel(IPccExecutivePresentationGateway gateway, IConfirmationService? confirmation = null)
    {
        _gateway = gateway;
        _confirmation = confirmation ?? new DenyConfirmationService();
        _snapshot = gateway.Snapshot;
        _selectedDispatchMode = _snapshot.DispatchSettings.Mode;
        _selectedProviderMode = ProviderMode.BrowserWeb;

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
        _selectedScreen = _snapshot.HasActiveRun ? ScreenId.Dashboard : ScreenId.ProjectSelection;
        _currentScreen = _screens[_selectedScreen];

        NavigateCommand = new RelayCommand(p => Navigate(p));
        RefreshCommand = GatewayCommand(UiAction.Refresh);
        SelectProjectCommand = new AsyncRelayCommand(
            async p =>
            {
                LastUiError = null;
                await _gateway.ExecuteAsync(UiAction.SelectProject, p?.ToString());
                if (_gateway.Snapshot.HasActiveRun)
                    Navigate(ScreenId.Dashboard);
                else
                    LastUiError = "Project selection did not resolve to a canonical PCC project. Review the project state and try again.";
            },
            p => _gateway.CanExecute(UiAction.SelectProject, p?.ToString()),
            ex => LastUiError = ex.Message);
        ConnectChromeCommand = GatewayCommand(UiAction.ConnectChrome);
        PauseAiCommand = GatewayCommand(UiAction.PauseAi);
        ResumeAiCommand = GatewayCommand(UiAction.ResumeAi);
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
        OpenAttentionLocationCommand = GatewayCommand(UiAction.OpenAttentionLocation, p => p?.ToString());
        InstallUpdateCommand = GatewayCommand(UiAction.InstallUpdateAndRestart);
        CheckForUpdatesCommand = GatewayCommand(UiAction.CheckForUpdates);
        SaveSettingsCommand = GatewayCommand(UiAction.SaveSettings, _ => $"provider={SelectedProviderMode};dispatch={SelectedDispatchMode}");
        ConversationHistoryCommand = GatewayCommand(UiAction.OpenConversationHistory, p => p?.ToString());

        gateway.SnapshotChanged += OnSnapshotChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<NavigationItem> Navigation { get; }
    public RuntimeSnapshot Snapshot { get => _snapshot; private set => Set(ref _snapshot, value); }
    public ScreenId SelectedScreen { get => _selectedScreen; private set => Set(ref _selectedScreen, value); }
    public ScreenViewModelBase CurrentScreen { get => _currentScreen; private set => Set(ref _currentScreen, value); }
    public string? LastUiError { get => _lastUiError; private set => Set(ref _lastUiError, value); }
    public bool HasUiError => !string.IsNullOrWhiteSpace(LastUiError);

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
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand SelectProjectCommand { get; }
    public AsyncRelayCommand ConnectChromeCommand { get; }
    public AsyncRelayCommand PauseAiCommand { get; }
    public AsyncRelayCommand ResumeAiCommand { get; }
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

    private AsyncRelayCommand GatewayCommand(UiAction action, Func<object?, string?>? target = null) =>
        new(
            async p =>
            {
                LastUiError = null;
                await _gateway.ExecuteAsync(action, target?.Invoke(p));
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
            async p =>
            {
                LastUiError = null;
                if (!_confirmation.Confirm(title, message, confirmLabel)) return;
                await _gateway.ExecuteAsync(action, target?.Invoke(p));
            },
            p => _gateway.CanExecute(action, target?.Invoke(p)),
            ex => LastUiError = ex.Message);

    private void OnSnapshotChanged(object? sender, RuntimeSnapshot snapshot)
    {
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
        RaiseAllCommands();
        OnPropertyChanged(nameof(HasUiError));
    }

    private void RaiseAllCommands()
    {
        RefreshCommand.RaiseCanExecuteChanged();
        SelectProjectCommand.RaiseCanExecuteChanged();
        ConnectChromeCommand.RaiseCanExecuteChanged();
        PauseAiCommand.RaiseCanExecuteChanged();
        ResumeAiCommand.RaiseCanExecuteChanged();
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
