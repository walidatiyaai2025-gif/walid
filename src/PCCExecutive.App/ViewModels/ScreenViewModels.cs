using System.ComponentModel;
using System.Runtime.CompilerServices;
using PCCExecutive.App.Presentation;

namespace PCCExecutive.App.ViewModels;

public abstract class ScreenViewModelBase(MainViewModel shell)
{
    public MainViewModel Shell { get; } = shell;
}

public sealed class ChromeConnectionViewModel(MainViewModel shell) : ScreenViewModelBase(shell);
public sealed class ProjectSelectionViewModel(MainViewModel shell) : ScreenViewModelBase(shell);
public sealed class DashboardViewModel(MainViewModel shell) : ScreenViewModelBase(shell);
public sealed class ManagerWorkspaceViewModel(MainViewModel shell) : ScreenViewModelBase(shell);
public sealed class WorkersDispatchViewModel(MainViewModel shell) : ScreenViewModelBase(shell);

public sealed class WorkerChatViewModel : ScreenViewModelBase, INotifyPropertyChanged
{
    private string? _selectedWorkerId;

    public WorkerChatViewModel(MainViewModel shell) : base(shell)
    {
        _selectedWorkerId = shell.Snapshot.Workers.FirstOrDefault()?.Id;
        shell.PropertyChanged += OnShellPropertyChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public WorkerSummary? SelectedWorker
    {
        get => ResolveSelectedWorker();
        set
        {
            var nextId = value?.Id;
            if (StringComparer.Ordinal.Equals(_selectedWorkerId, nextId)) return;
            _selectedWorkerId = nextId;
            RaiseSelectedWorkerProperties();
        }
    }

    public SessionSummary? SelectedWorkerSession
    {
        get
        {
            var worker = SelectedWorker;
            if (worker is null) return null;
            return Shell.Snapshot.Sessions.FirstOrDefault(
                session => string.Equals(session.LogicalName, worker.LogicalName, StringComparison.OrdinalIgnoreCase));
        }
    }

    public string SelectedWorkerConversation =>
        SelectedWorkerSession?.ConversationOrTask ?? "NOT AVAILABLE";

    public string SelectedWorkerSessionState =>
        SelectedWorkerSession?.State ?? "NOT AVAILABLE";

    public string SelectedWorkerOwnership =>
        SelectedWorkerSession is null
            ? "NOT AVAILABLE"
            : SelectedWorkerSession.IsPccOwned
                ? "PCC OWNED"
                : "OWNERSHIP NOT PROVEN";

    public string SelectedWorkerLastActivity =>
        SelectedWorkerSession?.LastActivity is { } at
            ? at.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            : "NOT AVAILABLE";

    private WorkerSummary? ResolveSelectedWorker()
    {
        if (!string.IsNullOrWhiteSpace(_selectedWorkerId))
        {
            var selected = Shell.Snapshot.Workers.FirstOrDefault(
                worker => StringComparer.Ordinal.Equals(worker.Id, _selectedWorkerId));
            if (selected is not null) return selected;
        }

        return Shell.Snapshot.Workers.FirstOrDefault();
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(MainViewModel.Snapshot), StringComparison.Ordinal)) return;

        var selectedStillExists = !string.IsNullOrWhiteSpace(_selectedWorkerId) &&
            Shell.Snapshot.Workers.Any(worker => StringComparer.Ordinal.Equals(worker.Id, _selectedWorkerId));

        if (!selectedStillExists)
            _selectedWorkerId = Shell.Snapshot.Workers.FirstOrDefault()?.Id;

        RaiseSelectedWorkerProperties();
    }

    private void RaiseSelectedWorkerProperties()
    {
        OnPropertyChanged(nameof(SelectedWorker));
        OnPropertyChanged(nameof(SelectedWorkerSession));
        OnPropertyChanged(nameof(SelectedWorkerConversation));
        OnPropertyChanged(nameof(SelectedWorkerSessionState));
        OnPropertyChanged(nameof(SelectedWorkerOwnership));
        OnPropertyChanged(nameof(SelectedWorkerLastActivity));
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class WaveSummaryViewModel(MainViewModel shell) : ScreenViewModelBase(shell);
public sealed class TaskBoardViewModel(MainViewModel shell) : ScreenViewModelBase(shell);
public sealed class EvidenceVerificationViewModel(MainViewModel shell) : ScreenViewModelBase(shell);
public sealed class LoopGuardViewModel(MainViewModel shell) : ScreenViewModelBase(shell);
public sealed class ChatGptHealthViewModel(MainViewModel shell) : ScreenViewModelBase(shell);
public sealed class SessionMonitorViewModel(MainViewModel shell) : ScreenViewModelBase(shell);

public sealed class SettingsViewModel : ScreenViewModelBase, INotifyPropertyChanged
{
    private DispatchMode _selectedDispatchMode;
    private int _selectedBaseIntervalSeconds;
    private int _selectedMaxWorkers;
    private bool _selectedAdaptivePacing;
    private bool _selectedAutoResume;
    private string _maxWorkersValidationMessage = string.Empty;
    private string _baseIntervalValidationMessage = string.Empty;

    public SettingsViewModel(MainViewModel shell) : base(shell)
    {
        SyncFromSnapshot();
        shell.PropertyChanged += OnShellPropertyChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<DispatchMode> DispatchModes => Shell.DispatchModes;

    public DispatchMode SelectedDispatchMode
    {
        get => _selectedDispatchMode;
        set
        {
            if (!Set(ref _selectedDispatchMode, value)) return;
            Shell.SelectedDispatchMode = value;
        }
    }

    public int SelectedBaseIntervalSeconds
    {
        get => _selectedBaseIntervalSeconds;
        set
        {
            var clamped = Math.Clamp(value, 0, 3600);
            BaseIntervalValidationMessage = value == clamped
                ? string.Empty
                : "Base interval must stay between 0 and 3600 seconds.";
            if (!Set(ref _selectedBaseIntervalSeconds, clamped)) return;
            Shell.SelectedBaseIntervalSeconds = clamped;
        }
    }

    public int SelectedMaxWorkers
    {
        get => _selectedMaxWorkers;
        set
        {
            var clamped = Math.Clamp(value, 1, 5);
            MaxWorkersValidationMessage = value == clamped
                ? string.Empty
                : "Max Workers is limited to 1–5.";
            if (!Set(ref _selectedMaxWorkers, clamped)) return;
            Shell.SelectedMaxWorkers = clamped;
        }
    }

    public bool SelectedAdaptivePacing
    {
        get => _selectedAdaptivePacing;
        set
        {
            if (!Set(ref _selectedAdaptivePacing, value)) return;
            Shell.SelectedAdaptivePacing = value;
        }
    }

    public bool SelectedAutoResume
    {
        get => _selectedAutoResume;
        set
        {
            if (!Set(ref _selectedAutoResume, value)) return;
            Shell.SelectedAutoResume = value;
        }
    }

    public string MaxWorkersValidationMessage
    {
        get => _maxWorkersValidationMessage;
        private set => Set(ref _maxWorkersValidationMessage, value);
    }

    public string BaseIntervalValidationMessage
    {
        get => _baseIntervalValidationMessage;
        private set => Set(ref _baseIntervalValidationMessage, value);
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(MainViewModel.Snapshot), StringComparison.Ordinal))
            SyncFromSnapshot();
    }

    private void SyncFromSnapshot()
    {
        var settings = Shell.Snapshot.DispatchSettings;
        _selectedDispatchMode = settings.Mode;
        _selectedBaseIntervalSeconds = Math.Clamp(settings.BaseIntervalSeconds, 0, 3600);
        _selectedMaxWorkers = Math.Clamp(settings.MaxWorkers, 1, 5);
        _selectedAdaptivePacing = settings.AdaptivePacing;
        _selectedAutoResume = settings.AutoResume;

        Shell.SelectedDispatchMode = _selectedDispatchMode;
        Shell.SelectedBaseIntervalSeconds = _selectedBaseIntervalSeconds;
        Shell.SelectedMaxWorkers = _selectedMaxWorkers;
        Shell.SelectedAdaptivePacing = _selectedAdaptivePacing;
        Shell.SelectedAutoResume = _selectedAutoResume;

        MaxWorkersValidationMessage = string.Empty;
        BaseIntervalValidationMessage = string.Empty;
        OnPropertyChanged(nameof(SelectedDispatchMode));
        OnPropertyChanged(nameof(SelectedBaseIntervalSeconds));
        OnPropertyChanged(nameof(SelectedMaxWorkers));
        OnPropertyChanged(nameof(SelectedAdaptivePacing));
        OnPropertyChanged(nameof(SelectedAutoResume));
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class UpdateCenterViewModel(MainViewModel shell) : ScreenViewModelBase(shell);
public sealed class AttentionCenterViewModel(MainViewModel shell) : ScreenViewModelBase(shell);
public sealed class ConversationHistoryViewModel(MainViewModel shell) : ScreenViewModelBase(shell);
