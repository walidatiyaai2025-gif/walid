using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PCCExecutive.App.Presentation;
using PCCExecutive.App.ViewModels;

namespace PCCExecutive.App;

public partial class MainWindow : Window
{
    private static readonly string[] PulseFrames = ["●", "◐", "◓", "◑", "◒"];

    private bool _allowClose;
    private readonly DispatcherTimer _activityTimer;
    private DateTimeOffset _lastRuntimeSnapshotAt = DateTimeOffset.UtcNow;
    private int _pulseFrame;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        ApplyAuthorityNavigation(viewModel);
        if (!viewModel.Snapshot.HasActiveRun)
            viewModel.Navigate(ScreenId.ChromeConnection);

        viewModel.PropertyChanged += ViewModel_PropertyChanged;
        _activityTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _activityTimer.Tick += ActivityTimer_Tick;
        _activityTimer.Start();
        UpdateRuntimeActivity(viewModel);

        Closing += MainWindow_Closing;
        Closed += (_, _) =>
        {
            _activityTimer.Stop();
            viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        };
    }

    private static void ApplyAuthorityNavigation(MainViewModel viewModel)
    {
        MoveNavigationItem(viewModel, ScreenId.ChromeConnection, 0);
        MoveNavigationItem(viewModel, ScreenId.ProjectSelection, 1);
        MoveNavigationItem(viewModel, ScreenId.Dashboard, 2);
    }

    private static void MoveNavigationItem(MainViewModel viewModel, ScreenId id, int targetIndex)
    {
        var sourceIndex = -1;
        for (var i = 0; i < viewModel.Navigation.Count; i++)
        {
            if (viewModel.Navigation[i].Id == id)
            {
                sourceIndex = i;
                break;
            }
        }

        if (sourceIndex >= 0 && sourceIndex != targetIndex)
            viewModel.Navigation.Move(sourceIndex, targetIndex);
    }

    public void AllowCloseAndClose()
    {
        _allowClose = true;
        Close();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.Snapshot) || sender is not MainViewModel viewModel)
            return;

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _lastRuntimeSnapshotAt = DateTimeOffset.UtcNow;
                UpdateRuntimeActivity(viewModel);
            }));
            return;
        }

        _lastRuntimeSnapshotAt = DateTimeOffset.UtcNow;
        UpdateRuntimeActivity(viewModel);
    }

    private void ActivityTimer_Tick(object? sender, EventArgs e)
    {
        ActivityPulseText.Text = PulseFrames[_pulseFrame++ % PulseFrames.Length];
        if (DataContext is MainViewModel viewModel)
            UpdateRuntimeActivity(viewModel);
    }

    private void UpdateRuntimeActivity(MainViewModel viewModel)
    {
        var snapshot = viewModel.Snapshot;
        var handoff = snapshot.LatestManagerHandoff ?? string.Empty;
        var autopilot = snapshot.AutopilotState ?? string.Empty;
        var stage = snapshot.CurrentWave ?? string.Empty;

        var globalSendPaused = Contains(handoff, "GLOBAL_SEND_PAUSED") ||
                               string.Equals(autopilot, "PAUSED", StringComparison.OrdinalIgnoreCase);
        var stalled = Contains(autopilot, "STALLED") || Contains(stage, "STALLED") ||
                      Contains(handoff, "stopped safely");
        var recovering = snapshot.GlobalHealth is HealthState.Recovering or HealthState.RateLimited or
                         HealthState.Cooldown or HealthState.TemporaryError or HealthState.Offline or
                         HealthState.Stuck || Contains(autopilot, "RECOVER");
        var working = autopilot is "PLANNING" or "READING_MANAGER_RESPONSE" or "DISPATCHING" or
                      "WAITING_WORKERS" or "MANAGER_REVIEW" or "CLOSURE_VERIFY" ||
                      Contains(handoff, "waiting for") || Contains(handoff, "reading") ||
                      Contains(handoff, "submitted");
        var done = string.Equals(autopilot, "DONE", StringComparison.OrdinalIgnoreCase) ||
                   snapshot.CompletionMode == CompletionMode.Verified;

        string state;
        string detail;
        Brush stateBrush;
        var moving = false;

        if (globalSendPaused)
        {
            state = "PAUSED";
            detail = snapshot.DispatchSettings.AutoResume
                ? "GLOBAL SEND PAUSED — no new ChatGPT sends are being made. Auto-resume is ON and is waiting for fresh safe ChatGPT semantic health."
                : "GLOBAL SEND PAUSED — no new ChatGPT sends are being made. Auto-resume is OFF; operator Resume is required after health is safe.";
            stateBrush = Brushes.LightCoral;
        }
        else if (stalled)
        {
            state = "STALLED";
            detail = string.IsNullOrWhiteSpace(handoff)
                ? "Autopilot is stopped; no new work is being sent. Review ChatGPT Health / Attention for the blocking reason."
                : handoff;
            stateBrush = Brushes.LightCoral;
        }
        else if (recovering)
        {
            state = "RECOVERING";
            detail = string.IsNullOrWhiteSpace(handoff)
                ? $"Runtime recovery is active. Health: {snapshot.GlobalHealth}."
                : handoff;
            stateBrush = Brushes.Gold;
            moving = true;
        }
        else if (working)
        {
            state = "WORKING";
            detail = string.IsNullOrWhiteSpace(handoff)
                ? $"PCC Executive is advancing stage {stage}."
                : handoff;
            stateBrush = Brushes.LightGreen;
            moving = true;
        }
        else if (done)
        {
            state = "DONE";
            detail = string.IsNullOrWhiteSpace(handoff) ? "Verified completion reached." : handoff;
            stateBrush = Brushes.LightGreen;
        }
        else
        {
            state = "READY / IDLE";
            detail = string.IsNullOrWhiteSpace(handoff)
                ? "Runtime is responsive, but no background action is currently advancing."
                : handoff;
            stateBrush = Brushes.DeepSkyBlue;
        }

        LiveStateText.Text = state;
        LiveStateText.Foreground = stateBrush;
        LiveDetailText.Text = detail;
        RuntimeActivityStateText.Text = state;
        RuntimeActivityStateText.Foreground = stateBrush;
        RuntimeActivityDetailText.Text = detail;
        RuntimeActivityProgress.IsIndeterminate = moving;
        RuntimeActivityProgress.Visibility = moving ? Visibility.Visible : Visibility.Collapsed;

        var age = DateTimeOffset.UtcNow - _lastRuntimeSnapshotAt;
        RuntimeActivityAgeText.Text = $"Last runtime update: {FormatAge(age)} ago";
        RuntimeActivityAgeText.Foreground = age > TimeSpan.FromSeconds(30) && moving ? Brushes.Gold : Brushes.SlateGray;
        RuntimeAutoResumeText.Text = $"Auto-resume: {(snapshot.DispatchSettings.AutoResume ? "ON" : "OFF")} • Health: {snapshot.GlobalHealth}";
    }

    private static bool Contains(string value, string token) =>
        value.Contains(token, StringComparison.OrdinalIgnoreCase);

    private static string FormatAge(TimeSpan age)
    {
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        if (age.TotalSeconds < 60) return $"{Math.Floor(age.TotalSeconds)}s";
        if (age.TotalMinutes < 60) return $"{Math.Floor(age.TotalMinutes)}m {age.Seconds}s";
        return $"{Math.Floor(age.TotalHours)}h {age.Minutes}m";
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        if (DataContext is MainViewModel vm && vm.Snapshot.HasActiveRun)
        {
            e.Cancel = true;
            Hide();
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) ToggleMaximize();
        else if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
}
