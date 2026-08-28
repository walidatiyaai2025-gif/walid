using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PCCExecutive.Application;
using PCCExecutive.Browser;
using PCCExecutive.Domain;
using PCCExecutive.GitHub;
using PCCExecutive.Infrastructure;
using PCCExecutive.Pcc;

namespace PCCExecutive.App.Presentation;

public sealed partial class TerminalPresentationGateway : IPccExecutivePresentationGateway, IAsyncDisposable
{
    private const string PccRoutingPath = "portfolio/project-routing.json";
    private const string UiPreferencesFileName = "ui-preferences.json";

    private readonly SqliteStateStore _store;
    private readonly ProjectRunLock _projectLock;
    private readonly IPccDocumentSource _pccSource;
    private readonly IProjectControlResolver _resolver;
    private readonly IProjectBaselineBuilder _baselineBuilder;
    private readonly BrowserSessionController _sessions;
    private readonly IBrowserRuntimeRegistry _runtimeRegistry;
    private readonly IOwnershipProofService _ownership;
    private readonly IChatGptBrowserAdapter _adapter;
    private readonly HttpClient _pccHttp;
    private readonly HttpClient _githubHttp;
    private readonly string _uiPreferencesPath;
    private readonly List<RecoveryEventSummary> _recovery = [];

    private IReadOnlyList<ProjectSummary> _projects = Array.Empty<ProjectSummary>();
    private ProjectResolution? _lastResolution;
    private ProjectBaselineSnapshot? _baseline;
    private ProjectRun? _run;
    private LogicalAgentId? _managerAgentId;
    private LogicalAgentId[] _workerAgentIds = [];
    private string? _selectedProjectId;
    private bool _reloadProjects = true;
    private PccExecutiveSettings _settings = new();
    private UiPreferences _uiPreferences = UiPreferences.Default;

    private TerminalPresentationGateway(
        SqliteStateStore store,
        ProjectRunLock projectLock,
        IPccDocumentSource pccSource,
        IProjectControlResolver resolver,
        IProjectBaselineBuilder baselineBuilder,
        BrowserSessionController sessions,
        IBrowserRuntimeRegistry runtimeRegistry,
        IOwnershipProofService ownership,
        IChatGptBrowserAdapter adapter,
        HttpClient pccHttp,
        HttpClient githubHttp,
        string uiPreferencesPath)
    {
        _store = store;
        _projectLock = projectLock;
        _pccSource = pccSource;
        _resolver = resolver;
        _baselineBuilder = baselineBuilder;
        _sessions = sessions;
        _runtimeRegistry = runtimeRegistry;
        _ownership = ownership;
        _adapter = adapter;
        _pccHttp = pccHttp;
        _githubHttp = githubHttp;
        _uiPreferencesPath = uiPreferencesPath;
        Snapshot = RuntimeSnapshot.Unbound with
        {
            GatewayBound = true,
            RuntimeStatus = "READY · SELECT A PCC PROJECT",
            ProjectResolutionState = "NO_PROJECT_SELECTED",
            CurrentWave = "NO ACTIVE WAVE",
            StartupRecoveryState = "Startup complete · no project selected",
            BrowserProviderState = "READY · no PCC browser session required"
        };
    }

    public RuntimeSnapshot Snapshot { get; private set; }
    public event EventHandler<RuntimeSnapshot>? SnapshotChanged;

    public static TerminalPresentationGateway Create()
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

            var profileRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PCC Executive", "browser-profiles");
            var markerStore = new FileOwnershipMarkerStore();
            var processInspector = new SystemProcessInspector();
            var ownership = new OwnershipProofService(profileRoot, markerStore, processInspector);
            var runtimeHost = new PlaywrightChromeRuntimeHost(profileRoot);
            IBrowserRuntimeRegistry registry = store;
            var controller = new BrowserSessionController(registry, runtimeHost, ownership, markerStore, processInspector);
            var adapter = new PlaywrightChatGptBrowserAdapter(runtimeHost);

            var pccHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var githubHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var source = new GitHubPccDocumentSource(pccHttp);
            var resolver = new PccProjectControlResolver(source);
            var github = new GitHubRestEvidenceClient(githubHttp);
            var baseline = new ProjectBaselineBuilder(resolver, github);
            var preferencesPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PCC Executive", "state", UiPreferencesFileName);

            var gateway = new TerminalPresentationGateway(
                store, projectLock, source, resolver, baseline, controller, registry, ownership, adapter,
                pccHttp, githubHttp, preferencesPath);
            gateway.LoadSettingsAsync().GetAwaiter().GetResult();
            gateway.RefreshAsync().GetAwaiter().GetResult();
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
        UiAction.Refresh or UiAction.RetryHealth or UiAction.SaveSettings => true,
        UiAction.ResolveProject or UiAction.SelectProject => !string.IsNullOrWhiteSpace(targetId),
        UiAction.ConnectChrome => _run is not null,
        UiAction.OpenSession or UiAction.BringSessionToFront or UiAction.HideSession => FindSession(targetId)?.IsPccOwned == true,
        UiAction.RestartSession or UiAction.KillSession => FindSession(targetId)?.CanKill == true,
        UiAction.KillAllPccSessions => Snapshot.Sessions.Any(x => x.CanKill),
        UiAction.RunVerification => _run is not null && !string.IsNullOrWhiteSpace(_selectedProjectId),
        UiAction.OpenAttentionLocation => FindAttention(targetId) is not null,
        UiAction.CheckForUpdates => UpdateManifestConfigured(),
        UiAction.InstallUpdateAndRestart => false,
        _ => false
    };

    public string? DisabledReason(UiAction action, string? targetId = null)
    {
        if (CanExecute(action, targetId)) return null;
        return action switch
        {
            UiAction.ResolveProject or UiAction.SelectProject => "Choose or enter a live PCC project/alias first.",
            UiAction.ConnectChrome => "NO_PROJECT_SELECTED · Select a PCC project before opening its Manager browser.",
            UiAction.OpenSession or UiAction.BringSessionToFront or UiAction.HideSession =>
                FindSession(targetId) is { IsPccOwned: false } session
                    ? $"Control disabled: positive PCC ownership is not proven ({session.OwnershipReason ?? "unknown ownership"})."
                    : "The requested PCC browser runtime is unavailable.",
            UiAction.RestartSession or UiAction.KillSession =>
                FindSession(targetId) is { } session && !session.CanKill
                    ? $"Control disabled: positive PCC ownership is not proven ({session.OwnershipReason ?? "unknown ownership"})."
                    : "The requested PCC browser runtime is unavailable.",
            UiAction.KillAllPccSessions => "No live runtime currently has positive PCC ownership proof. Personal/unknown Chrome remains excluded.",
            UiAction.PauseAi or UiAction.ResumeAi or UiAction.RequestManagerPlan or UiAction.StartDispatch or UiAction.PauseDispatch or UiAction.ReconcileWave =>
                "Canonical Manager orchestration contracts are integrated, but a live WPF orchestration command host is not composed yet; the UI will not fake local execution state.",
            UiAction.RunVerification => "NO_PROJECT_SELECTED · Select a PCC project before refreshing live PCC/GitHub verification evidence.",
            UiAction.InspectLoopGuard or UiAction.ReplanLoop or UiAction.ResumeLoopOnce or UiAction.StopLoop =>
                "Canonical Loop Guard policy exists, but no active persisted loop snapshot/command host is composed for this UI action.",
            UiAction.CheckForUpdates => "NO UPDATE SOURCE CONFIGURED · Set PCC_EXECUTIVE_UPDATE_MANIFEST to a real manifest path.",
            UiAction.InstallUpdateAndRestart => "Worker 5 staged-install execution is not exposed as an in-process WPF command; installation stays disabled rather than simulating success.",
            UiAction.OpenAttentionLocation => "The attention item is no longer active.",
            UiAction.DisconnectChrome => "Disconnect is not a safe v1 operation; use Hide, Restart, Kill, or Kill All PCC Sessions with ownership proof.",
            _ => "The required runtime service is unavailable."
        };
    }
}
