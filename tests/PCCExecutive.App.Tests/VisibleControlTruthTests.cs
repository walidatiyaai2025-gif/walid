using PCCExecutive.App.Presentation;
using PCCExecutive.App.ViewModels;
using Xunit;

namespace PCCExecutive.App.Tests;

public sealed class VisibleControlTruthTests
{
    [Theory]
    [InlineData(0, "Worker 1")]
    [InlineData(1, "Worker 2")]
    [InlineData(4, "Worker 5")]
    public void Worker_selection_changes_visible_worker(int index, string logicalName)
    {
        var gateway = new RecordingGateway(SnapshotWithWorkers());
        var shell = new MainViewModel(gateway);
        var screen = new WorkerChatViewModel(shell);

        screen.SelectedWorker = shell.Snapshot.Workers[index];

        Assert.Equal(logicalName, screen.SelectedWorker?.LogicalName);
        Assert.Equal($"runtime-{index + 1}", screen.SelectedWorkerSession?.RuntimeId);
    }

    [Fact]
    public void Selected_worker_session_is_the_target_for_open_chat()
    {
        var gateway = new RecordingGateway(SnapshotWithWorkers());
        var shell = new MainViewModel(gateway);
        var screen = new WorkerChatViewModel(shell);
        screen.SelectedWorker = shell.Snapshot.Workers[1];

        shell.OpenSessionCommand.Execute(screen.SelectedWorkerSession?.RuntimeId);

        Assert.Equal((UiAction.OpenSession, "runtime-2"), gateway.Executions.Last());
    }

    [Fact]
    public void Selected_worker_session_is_the_target_for_confirmed_kill()
    {
        var gateway = new RecordingGateway(SnapshotWithWorkers());
        var shell = new MainViewModel(gateway, new AlwaysConfirm());
        var screen = new WorkerChatViewModel(shell);
        screen.SelectedWorker = shell.Snapshot.Workers[3];

        shell.KillSessionCommand.Execute(screen.SelectedWorkerSession?.RuntimeId);

        Assert.Equal((UiAction.KillSession, "runtime-4"), gateway.Executions.Last());
    }

    [Fact]
    public void Worker_selection_survives_snapshot_refresh_when_worker_still_exists()
    {
        var gateway = new RecordingGateway(SnapshotWithWorkers());
        var shell = new MainViewModel(gateway);
        var screen = new WorkerChatViewModel(shell);
        screen.SelectedWorker = shell.Snapshot.Workers[4];

        gateway.Push(SnapshotWithWorkers(workerState: "RUNNING"));

        Assert.Equal("Worker 5", screen.SelectedWorker?.LogicalName);
        Assert.Equal("RUNNING", screen.SelectedWorker?.State);
        Assert.Equal("runtime-5", screen.SelectedWorkerSession?.RuntimeId);
    }

    [Fact]
    public void Worker_selection_falls_back_safely_when_selected_worker_disappears()
    {
        var gateway = new RecordingGateway(SnapshotWithWorkers());
        var shell = new MainViewModel(gateway);
        var screen = new WorkerChatViewModel(shell);
        screen.SelectedWorker = shell.Snapshot.Workers[4];

        gateway.Push(SnapshotWithWorkers().WithWorkers(2));

        Assert.Equal("Worker 1", screen.SelectedWorker?.LogicalName);
        Assert.Equal("runtime-1", screen.SelectedWorkerSession?.RuntimeId);
    }

    [Fact]
    public void Settings_view_model_tracks_persisted_snapshot_values()
    {
        var initial = SnapshotWithWorkers() with
        {
            DispatchSettings = new DispatchSettingsSummary(
                DispatchMode.AutomaticStaged, 10, true, 5, true, true, true)
        };
        var gateway = new RecordingGateway(initial);
        var shell = new MainViewModel(gateway);
        var settings = new SettingsViewModel(shell);

        gateway.Push(initial with
        {
            DispatchSettings = new DispatchSettingsSummary(
                DispatchMode.Assisted, 23, false, 3, true, false, true)
        });

        Assert.Equal(DispatchMode.Assisted, settings.SelectedDispatchMode);
        Assert.Equal(23, settings.SelectedBaseIntervalSeconds);
        Assert.Equal(3, settings.SelectedMaxWorkers);
        Assert.False(settings.SelectedAdaptivePacing);
        Assert.False(settings.SelectedAutoResume);

        Assert.Equal(DispatchMode.Assisted, shell.SelectedDispatchMode);
        Assert.Equal(23, shell.SelectedBaseIntervalSeconds);
        Assert.Equal(3, shell.SelectedMaxWorkers);
        Assert.False(shell.SelectedAdaptivePacing);
        Assert.False(shell.SelectedAutoResume);
    }

    [Fact]
    public void Max_workers_is_visibly_constrained_to_one_through_five()
    {
        var gateway = new RecordingGateway(SnapshotWithWorkers());
        var shell = new MainViewModel(gateway);
        var settings = new SettingsViewModel(shell);

        settings.SelectedMaxWorkers = 0;
        Assert.Equal(1, settings.SelectedMaxWorkers);
        Assert.Equal(1, shell.SelectedMaxWorkers);
        Assert.NotEmpty(settings.MaxWorkersValidationMessage);

        settings.SelectedMaxWorkers = 9;
        Assert.Equal(5, settings.SelectedMaxWorkers);
        Assert.Equal(5, shell.SelectedMaxWorkers);
        Assert.NotEmpty(settings.MaxWorkersValidationMessage);

        settings.SelectedMaxWorkers = 4;
        Assert.Equal(4, settings.SelectedMaxWorkers);
        Assert.Equal(4, shell.SelectedMaxWorkers);
        Assert.Empty(settings.MaxWorkersValidationMessage);
    }

    [Fact]
    public void Settings_values_flow_into_existing_save_payload()
    {
        var gateway = new RecordingGateway(SnapshotWithWorkers());
        var shell = new MainViewModel(gateway);
        var settings = new SettingsViewModel(shell)
        {
            SelectedDispatchMode = DispatchMode.Assisted,
            SelectedBaseIntervalSeconds = 17,
            SelectedMaxWorkers = 3,
            SelectedAdaptivePacing = false,
            SelectedAutoResume = false
        };

        shell.SaveSettingsCommand.Execute(null);

        Assert.Equal(
            (UiAction.SaveSettings, "provider=BrowserWeb;dispatch=Assisted;interval=17;maxWorkers=3;adaptive=False;autoResume=False"),
            gateway.Executions.Last());
    }

    [Fact]
    public void Conversation_history_command_navigates_to_durable_history_screen()
    {
        var gateway = new RecordingGateway(SnapshotWithWorkers());
        var shell = new MainViewModel(gateway);

        shell.ConversationHistoryCommand.Execute("Worker 2");

        Assert.Equal(ScreenId.ConversationHistory, shell.SelectedScreen);
        Assert.IsType<ConversationHistoryViewModel>(shell.CurrentScreen);
        Assert.Equal((UiAction.OpenConversationHistory, "Worker 2"), gateway.Executions.Last());
    }

    [Fact]
    public void Update_center_exposes_explicit_external_block_reasons()
    {
        var update = SnapshotWithWorkers().Update;

        Assert.Equal("NO GOVERNED UPDATE SOURCE CONFIGURED", update.UpdateSourceStatus);
        Assert.Equal("NO VERIFIED STAGED UPDATE AVAILABLE", update.InstallAvailabilityStatus);
        Assert.False(update.InstallReady);
    }

    [Fact]
    public void Empty_attention_is_not_presented_as_zero_when_runtime_truth_is_unknown()
    {
        var unknown = SnapshotWithWorkers() with
        {
            GlobalHealth = HealthState.Unknown,
            AttentionItems = []
        };

        Assert.False(unknown.AttentionStateKnown);
        Assert.Equal("—", unknown.AttentionCountText);
        Assert.Contains("not yet verified", unknown.AttentionHeadline.ToLowerInvariant());

        var known = unknown with { GlobalHealth = HealthState.Healthy };
        Assert.True(known.AttentionStateKnown);
        Assert.Equal("0", known.AttentionCountText);
        Assert.Equal("0 — Nothing needs you", known.AttentionHeadline);
    }

    [Fact]
    public void Unproven_pass_is_not_rendered_as_verified_evidence()
    {
        var localClaim = new EvidenceGateSummary(
            "Foundation",
            "PASS",
            100,
            "Canonical Domain/Application contracts integrated");
        var exactVerified = new EvidenceGateSummary(
            "PCC Integration",
            "PASS",
            null,
            "PASS@abcdef12");

        Assert.Equal("PENDING", localClaim.VisibleState);
        Assert.Equal("—", localClaim.VisibleScoreText);
        Assert.Equal("VERIFIED", exactVerified.VisibleState);
    }

    [Fact]
    public void P0_commands_continue_to_honor_gateway_can_execute()
    {
        var gateway = new RecordingGateway(
            SnapshotWithWorkers(),
            canExecute: (action, _) => action == UiAction.ReconcileWave);
        var shell = new MainViewModel(gateway);

        Assert.False(shell.StartManagerCommand.CanExecute(null));
        Assert.False(shell.StartDispatchCommand.CanExecute(null));
        Assert.True(shell.ReconcileWaveCommand.CanExecute(null));
    }

    [Fact]
    public void Extended_health_states_have_explicit_visible_semantics()
    {
        Assert.Equal("SENDING", (SnapshotWithWorkers() with { GlobalHealth = HealthState.Sending }).HealthText);
        Assert.Equal("GENERATING", (SnapshotWithWorkers() with { GlobalHealth = HealthState.Generating }).HealthText);
        Assert.Equal("SESSION EXPIRED", (SnapshotWithWorkers() with { GlobalHealth = HealthState.SessionExpired }).HealthText);
        Assert.Equal("FAILED", (SnapshotWithWorkers() with { GlobalHealth = HealthState.Failed }).HealthText);
        Assert.Equal("DONE", (SnapshotWithWorkers() with { GlobalHealth = HealthState.Done }).HealthText);
    }

    private static RuntimeSnapshot SnapshotWithWorkers(string workerState = "IDLE")
    {
        var workers = Enumerable.Range(1, 5)
            .Select(index => new WorkerSummary(
                $"worker-{index}",
                $"Worker {index}",
                "Worker",
                workerState,
                null,
                $"Task {index}",
                HealthState.Unknown,
                null))
            .ToArray();

        var sessions = Enumerable.Range(1, 5)
            .Select(index => new SessionSummary(
                $"runtime-{index}",
                $"Worker {index}",
                "Worker",
                "READY",
                SessionVisibility.Hidden,
                $"conversation-{index}",
                DateTimeOffset.UtcNow,
                true,
                1000 + index,
                HealthState.Unknown))
            .ToArray();

        return new RuntimeSnapshot(
            GatewayBound: true,
            HasActiveRun: true,
            RuntimeStatus: "Integrated runtime",
            GlobalHealth: HealthState.Healthy,
            AutopilotState: "READY",
            CurrentWave: "Wave 1 · Ready",
            VerifiedCompletion: 10,
            ManagerEstimate: 20,
            CompletionMode: CompletionMode.Running,
            ActiveWorkers: 0,
            P0Count: 0,
            P1Count: 0,
            BlockerCount: 0,
            LoopGuardState: "UNKNOWN",
            LatestManagerHandoff: "Runtime handoff",
            CurrentExecutionFlow: "Project → Manager → Workers",
            ApiConfigured: false,
            ProviderMode: ProviderMode.BrowserWeb,
            DispatchSettings: DispatchSettingsSummary.ProductDefaults,
            Update: new UpdateSummary("0.1.0", null, "Not checked", "Durable data active", "Schema ready", "Rollback contract ready", false),
            Projects: [],
            Sessions: sessions,
            Workers: workers,
            Tasks: [],
            EvidenceGates: [],
            AttentionItems: [],
            RecoveryEvents: []);
    }

    private sealed class AlwaysConfirm : Services.IConfirmationService
    {
        public bool Confirm(string title, string message, string confirmLabel) => true;
    }

    private sealed class RecordingGateway : IPccExecutivePresentationGateway
    {
        private RuntimeSnapshot _snapshot;
        private readonly Func<UiAction, string?, bool> _canExecute;

        public RecordingGateway(
            RuntimeSnapshot snapshot,
            Func<UiAction, string?, bool>? canExecute = null)
        {
            _snapshot = snapshot;
            _canExecute = canExecute ?? ((_, _) => true);
        }

        public RuntimeSnapshot Snapshot => _snapshot;
        public event EventHandler<RuntimeSnapshot>? SnapshotChanged;
        public List<(UiAction Action, string? Target)> Executions { get; } = [];

        public bool CanExecute(UiAction action, string? targetId = null) =>
            _canExecute(action, targetId);

        public Task ExecuteAsync(
            UiAction action,
            string? targetId = null,
            CancellationToken cancellationToken = default)
        {
            Executions.Add((action, targetId));
            return Task.CompletedTask;
        }

        public void Push(RuntimeSnapshot snapshot)
        {
            _snapshot = snapshot;
            SnapshotChanged?.Invoke(this, snapshot);
        }
    }
}

internal static class RuntimeSnapshotTestExtensions
{
    public static RuntimeSnapshot WithWorkers(this RuntimeSnapshot snapshot, int count)
    {
        var workers = snapshot.Workers.Take(count).ToArray();
        var logicalNames = workers.Select(worker => worker.LogicalName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sessions = snapshot.Sessions.Where(session => logicalNames.Contains(session.LogicalName)).ToArray();
        return snapshot with { Workers = workers, Sessions = sessions };
    }
}
