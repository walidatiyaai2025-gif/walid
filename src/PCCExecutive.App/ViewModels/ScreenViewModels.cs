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
public sealed class WorkerChatViewModel(MainViewModel shell) : ScreenViewModelBase(shell);
public sealed class WaveSummaryViewModel(MainViewModel shell) : ScreenViewModelBase(shell);
public sealed class TaskBoardViewModel(MainViewModel shell) : ScreenViewModelBase(shell);
public sealed class EvidenceVerificationViewModel(MainViewModel shell) : ScreenViewModelBase(shell);
public sealed class LoopGuardViewModel(MainViewModel shell) : ScreenViewModelBase(shell);
public sealed class ChatGptHealthViewModel(MainViewModel shell) : ScreenViewModelBase(shell);
public sealed class SessionMonitorViewModel(MainViewModel shell) : ScreenViewModelBase(shell);
public sealed class SettingsViewModel(MainViewModel shell) : ScreenViewModelBase(shell);
public sealed class UpdateCenterViewModel(MainViewModel shell) : ScreenViewModelBase(shell);
public sealed class AttentionCenterViewModel(MainViewModel shell) : ScreenViewModelBase(shell);
