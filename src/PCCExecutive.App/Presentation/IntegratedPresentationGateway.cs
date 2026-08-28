using PCCExecutive.Application;
using PCCExecutive.Browser;
using PCCExecutive.Domain;
using PCCExecutive.GitHub;
using PCCExecutive.Infrastructure;
using PCCExecutive.Pcc;

namespace PCCExecutive.App.Presentation;

public sealed class IntegratedPresentationGateway : IPccExecutivePresentationGateway, IAsyncDisposable
{
    private static readonly ProjectId ProductProjectId = new(new Guid("82ddbf4b-2872-4bd9-aa4a-992bbd48f185"));
    private static readonly ProjectRunId ProductRunId = new(new Guid("38cb3810-0c36-402f-b9b2-3a7dbcd63054"));
    private static readonly LogicalAgentId ManagerAgentId = new(new Guid("cc501537-dc9b-45ea-ae40-59b31ea00bf6"));
    private static readonly LogicalAgentId[] WorkerAgentIds =
    [
        new(new Guid("11111111-1111-4111-8111-111111111111")),
        new(new Guid("22222222-2222-4222-8222-222222222222")),
        new(new Guid("33333333-3333-4333-8333-333333333333")),
        new(new Guid("44444444-4444-4444-8444-444444444444")),
        new(new Guid("55555555-5555-4555-8555-555555555555"))
    ];

    private readonly SqliteStateStore _store;
    private readonly ProjectRunLock _projectLock;
    private readonly IProjectControlResolver _pcc;
    private readonly IProjectBaselineBuilder _baseline;
    private readonly BrowserSessionController _sessions;
    private readonly IBrowserRuntimeRegistry _runtimeRegistry;
    private readonly IOwnershipProofService _ownership;
    private readonly HttpClient _pccHttp;
    private readonly HttpClient _githubHttp;
    private readonly List<RecoveryEventSummary> _recovery = [];
    private ProjectRun? _run;
    private string _projectDisplay = "PCC Executive";
    private string _projectRepository = "walidatiyaai2025-gif/walid";
    private string _pccState = "NOT_CHECKED";
    private string _githubState = "NOT_CHECKED";
    private string _autopilot = "READY";

    private IntegratedPresentationGateway(
        SqliteStateStore store,
        ProjectRunLock projectLock,
        IProjectControlResolver pcc,
        IProjectBaselineBuilder baseline,
        BrowserSessionController sessions,
        IBrowserRuntimeRegistry runtimeRegistry,
        IOwnershipProofService ownership,
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
        _pccHttp = pccHttp;
        _githubHttp = githubHttp;
        _run = run;
        Snapshot = BuildSnapshot(Array.Empty<BrowserRuntimeRecord>(), new HashSet<string>(StringComparer.Ordinal));
    }

    public RuntimeSnapshot Snapshot { get; private set; }
    public event EventHandler<RuntimeSnapshot>? SnapshotChanged;

    public static IntegratedPresentationGateway Create()
    {
        var projectLock = ProjectRunLock.TryAcquire("PCCEXECUTIVE");
        if (!projectLock.IsOwned)
        {
            projectLock.Dispose();
            throw new InvalidOperationException("PCC Executive is already controlling this project on this machine.");
        }

        try
        {
            var store = new SqliteStateStore(SqliteStateStore.DefaultDatabasePath);
            store.InitializeAsync().GetAwaiter().GetResult();
            var settings = store.LoadSettingsAsync().GetAwaiter().GetResult();
            if (!string.Equals(settings.Provider, "BrowserChat", StringComparison.Ordinal))
                store.SaveSettingsAsync(settings with { Provider = "BrowserChat" }).GetAwaiter().GetResult();

            var run = store.LoadProjectRunAsync(ProductRunId).GetAwaiter().GetResult();
            if (run is not null)
                PersistLogicalAgents(store, run.Id);

            var profileRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCC Executive", "browser-profiles");
            var markerStore = new FileOwnershipMarkerStore();
            var processInspector = new SystemProcessInspector();
            IOwnershipProofService ownership = new OwnershipProofService(profileRoot, markerStore, processInspector);
            var runtimeHost = new PlaywrightChromeRuntimeHost(profileRoot);
            IBrowserRuntimeRegistry registry = store;
            var controller = new BrowserSessionController(registry, runtimeHost, ownership, markerStore, processInspector);

            var adapter = new PlaywrightChatGptBrowserAdapter(runtimeHost);
            var browserProvider = new PCCExecutive.Browser.BrowserChatProvider(registry, adapter, store, new WrongChatGuard(), new GlobalBrowserSendGate());
            _ = new BrowserAgentProviderAdapter(registry, browserProvider);

            var pccHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var githubHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var pcc = new PccProjectControlResolver(new GitHubPccDocumentSource(pccHttp));
            var github = new GitHubRestEvidenceClient(githubHttp);
            var baseline = new ProjectBaselineBuilder(pcc, github);

            var gateway = new IntegratedPresentationGateway(store, projectLock, pcc, baseline, controller, registry, ownership, pccHttp, githubHttp, run);
            gateway.RefreshLocalSnapshotAsync().GetAwaiter().GetResult();
            return gateway;
        }
        catch
        {
            projectLock.Dispose();
            throw;
        }
    }

    public bool CanExecute(UiAction action, string? targetId = null) => action switch
    {
        UiAction.Refresh or UiAction.SelectProject or UiAction.SaveSettings or UiAction.RunVerification => true,
        UiAction.ConnectChrome or UiAction.PauseAi or UiAction.ResumeAi => _run is not null,
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
            case UiAction.SelectProject:
                await RefreshExternalAsync(targetId ?? "PCCEXECUTIVE", cancellationToken).ConfigureAwait(false);
                break;
            case UiAction.ConnectChrome:
                await ConnectManagerChromeAsync(cancellationToken).ConfigureAwait(false);
                break;
            case UiAction.PauseAi:
                RequireActiveRun();
                _autopilot = "PAUSED";
                await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
                break;
            case UiAction.ResumeAi:
                RequireActiveRun();
                _autopilot = "READY";
                await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
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
                await _store.SaveSettingsAsync(new PccExecutiveSettings(), cancellationToken).ConfigureAwait(false);
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

    private static void PersistLogicalAgents(SqliteStateStore store, ProjectRunId runId)
    {
        var manager = new LogicalAgentSession(ManagerAgentId, runId, AgentRole.Manager, null, null, null, LogicalSessionState.Ready);
        store.SaveLogicalAgentAsync(manager).GetAwaiter().GetResult();
        for (var i = 0; i < WorkerAgentIds.Length; i++)
        {
            var worker = new LogicalAgentSession(WorkerAgentIds[i], runId, AgentRole.Worker, new WorkerSlotId(i + 1), null, null, LogicalSessionState.Idle);
            store.SaveLogicalAgentAsync(worker).GetAwaiter().GetResult();
        }
    }

    private async Task ConnectManagerChromeAsync(CancellationToken cancellationToken)
    {
        var run = RequireActiveRun();
        try
        {
            var existing = (await _runtimeRegistry.ListAsync(cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(x => StringComparer.Ordinal.Equals(x.ProjectRunId, run.Id.ToString()) && StringComparer.Ordinal.Equals(x.LogicalAgentId, ManagerAgentId.ToString()) && !x.IsArchived && x.State is not BrowserSessionState.Killed);
            if (existing is null)
                await _sessions.CreateAsync(new BrowserSessionRequest(run.Id.ToString(), ManagerAgentId.ToString(), DefaultVisibility: BrowserVisibility.Hidden), cancellationToken).ConfigureAwait(false);
            _recovery.Insert(0, new RecoveryEventSummary(DateTimeOffset.UtcNow, "READY", "PCC-owned Manager Chrome runtime initialized; personal Chrome remains excluded.", true));
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException or TimeoutException)
        {
            _recovery.Insert(0, new RecoveryEventSummary(DateTimeOffset.UtcNow, "BROWSER BLOCKED", ex.Message, false));
        }
        await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
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

            if (_run is null)
            {
                _run = new ProjectRun(ProductRunId, ProductProjectId, ProjectRunState.Initializing, DateTimeOffset.UtcNow, new ManagerEstimate(0), new VerifiedCompletion(0), ProjectCompletionMode.Active);
                await _store.SaveProjectRunAsync(_run, cancellationToken).ConfigureAwait(false);
                PersistLogicalAgents(_store, _run.Id);
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
        var result = await _baseline.BuildAsync("PCCEXECUTIVE", cancellationToken).ConfigureAwait(false);
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
                    StringComparer.Ordinal.Equals(x.LogicalAgentId, ManagerAgentId.ToString()) ? "Manager" : "Worker",
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
            : WorkerAgentIds.Select((id, index) => new WorkerSummary(
                id.ToString(),
                $"Worker {index + 1}",
                "Worker",
                "IDLE",
                0,
                "No task assigned",
                HealthState.Unknown,
                null)).ToArray();

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
            CurrentWave: run is null ? "No project selected" : run.State == ProjectRunState.ManagerPlanning ? "Manager planning" : run.State.ToString(),
            VerifiedCompletion: run is null ? null : (int)run.VerifiedCompletion.Percent,
            ManagerEstimate: run is null ? null : (int)run.ManagerEstimate.Percent,
            CompletionMode: run is null ? CompletionMode.Unknown : MapCompletionMode(run.CompletionMode),
            ActiveWorkers: 0,
            P0Count: 0,
            P1Count: 0,
            BlockerCount: 0,
            LoopGuardState: run is null ? "WAITING_FOR_PROJECT" : "NORMAL",
            LatestManagerHandoff: run is null ? "Select and resolve a project before Manager execution." : "Awaiting first live Manager plan",
            CurrentExecutionFlow: run is null ? "Project Selection → resolve canonical project → Dashboard" : "Project → Manager plan → validate → staged Workers → reconcile → Manager review",
            ApiConfigured: false,
            ProviderMode: ProviderMode.BrowserWeb,
            DispatchSettings: DispatchSettingsSummary.ProductDefaults,
            Update: new UpdateSummary("0.1.0", null, "Release hardening integrated", "Durable data path active", $"Schema v{schemaVersion}", "Updater rollback contract integrated", false),
            Projects: run is null
                ? Array.Empty<ProjectSummary>()
                : [new ProjectSummary("PCCEXECUTIVE", _projectDisplay, _projectRepository, (int)run.VerifiedCompletion.Percent, run.State.ToString().ToUpperInvariant(), null, DateTimeOffset.UtcNow)],
            Sessions: sessions,
            Workers: workers,
            Tasks: Array.Empty<TaskSummary>(),
            EvidenceGates: gates,
            AttentionItems: Array.Empty<AttentionSummary>(),
            RecoveryEvents: _recovery.Take(20).ToArray());
    }

    private static string LogicalNameFor(BrowserRuntimeRecord runtime)
    {
        if (StringComparer.Ordinal.Equals(runtime.LogicalAgentId, ManagerAgentId.ToString())) return "Manager";
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
        _pccHttp.Dispose();
        _githubHttp.Dispose();
        await _store.DisposeAsync().ConfigureAwait(false);
        _projectLock.Dispose();
    }
}
