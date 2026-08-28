using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PCCExecutive.App.Presentation;
using PCCExecutive.Application;
using PCCExecutive.Browser;
using PCCExecutive.Domain;
using PCCExecutive.Infrastructure;

namespace PCCExecutive.E2E;

internal sealed class ProductionRuntimeAcceptanceHarness : IAsyncDisposable
{
    internal const string ProjectControlId = "PCCEXECUTIVE";
    internal const string Repository = "walidatiyaai2025-gif/walid";
    internal const string ExactHead = "1111111111111111111111111111111111111111";
    internal static readonly DateTimeOffset Now = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root;
    private readonly string _databasePath;
    private readonly ControlledExternalState _external = new();
    private ProjectRunLock? _restartLock;
    private int _disposed;

    private ProductionRuntimeAcceptanceHarness(string root)
    {
        _root = root;
        _databasePath = Path.Combine(root, "production-runtime.db");
        Route = BuildRoute("IN_PROGRESS");
        Baseline = new MutableBaselineBuilder(BuildBaseline(Route));
        Pcc = new ControlledProjectControl(Route);
    }

    internal PccExecutiveRuntimeHost Host { get; private set; } = null!;
    internal SqliteStateStore Store { get; private set; } = null!;
    internal GlobalBrowserSendGate SendGate { get; private set; } = null!;
    internal BrowserAgentProviderAdapter AgentProvider { get; private set; } = null!;
    internal BrowserSessionController Sessions { get; private set; } = null!;
    internal ScriptedBrowserAdapter Adapter { get; private set; } = null!;
    internal ControlledOwnershipProof Ownership { get; private set; } = null!;
    internal ControlledProjectControl Pcc { get; }
    internal MutableBaselineBuilder Baseline { get; }
    internal ProjectRoutingSnapshot Route { get; private set; }
    internal string DatabasePath => _databasePath;

    internal static async Task<ProductionRuntimeAcceptanceHarness> CreateAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "pcc-runtime-e2e-final", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var harness = new ProductionRuntimeAcceptanceHarness(root);
        await harness.ConstructFreshHostAsync(null, null).ConfigureAwait(false);
        return harness;
    }

    internal ProjectRun Run => GetField<ProjectRun?>(Host, "_run") ?? throw new InvalidOperationException("Production host has no active ProjectRun.");
    internal LogicalAgentId ManagerAgentId => GetField<LogicalAgentId?>(Host, "_managerAgentId") ?? throw new InvalidOperationException("Manager logical agent is unavailable.");
    internal LogicalAgentId[] WorkerAgentIds => GetField<LogicalAgentId[]>(Host, "_workerAgentIds");
    internal Wave? CurrentWave => GetField<Wave?>(Host, "_currentWave");
    internal IReadOnlyDictionary<TaskId, WorkerSlotId> Assignments => GetField<IReadOnlyDictionary<TaskId, WorkerSlotId>>(Host, "_assignments");
    internal IReadOnlyList<WorkerTask> RuntimeTasks => GetField<IReadOnlyList<WorkerTask>>(Host, "_runtimeTasks");
    internal string Autopilot => GetField<string>(Host, "_autopilot");
    internal object? RolloverRuntime => GetField<object?>(Host, "_rolloverRuntime");

    internal Task SelectProjectAsync() => Host.ExecuteAsync(UiAction.SelectProject, ProjectControlId);
    internal Task ConnectManagerAsync() => Host.ExecuteAsync(UiAction.ConnectChrome);
    internal Task StartManagerAsync() => Host.ExecuteAsync(UiAction.StartManager);
    internal Task ReconcileAsync() => Host.ExecuteAsync(UiAction.ReconcileWave);
    internal Task StartDispatchAsync() => Host.ExecuteAsync(UiAction.StartDispatch);
    internal Task PauseAsync() => Host.ExecuteAsync(UiAction.PauseAi);
    internal Task ResumeAsync() => Host.ExecuteAsync(UiAction.ResumeAi);

    internal async Task<BrowserRuntimeRecord> RuntimeForAsync(LogicalAgentId logicalAgentId)
    {
        var runtimes = await Store.ListBrowserRuntimesAsync().ConfigureAwait(false);
        return runtimes
            .Where(x => !x.IsArchived && x.State is not BrowserSessionState.Killed and not BrowserSessionState.Archived)
            .OrderByDescending(x => x.LastActivityAt)
            .First(x => StringComparer.Ordinal.Equals(x.ProjectRunId, Run.Id.ToString()) && StringComparer.Ordinal.Equals(x.LogicalAgentId, logicalAgentId.ToString()));
    }

    internal async Task ForceInterruptedRestartAsync()
    {
        var runId = Run.Id;
        var identity = Route.RoutingIdentity;
        var hostLock = GetField<ProjectRunLock?>(Host, "_projectLock");
        try
        {
            await Host.DisposeAsync().ConfigureAwait(false);
            _restartLock = null;
        }
        catch
        {
            // The production host owns the lock. Only use this fallback when shutdown failed
            // before normal lock release so a failed assertion cannot poison the next test.
            hostLock?.Dispose();
            _restartLock = null;
            throw;
        }

        await using (var markerStore = new SqliteStateStore(_databasePath))
        {
            await markerStore.InitializeAsync().ConfigureAwait(false);
            var interrupted = new ShutdownMarker(runId, false, DateTimeOffset.UtcNow, "FORCED_PROCESS_TERMINATION_ACCEPTANCE");
            await markerStore.SaveCheckpointAsync(new DurableCheckpoint(
                $"shutdown:{runId}", runId.ToString(), "shutdown-marker-v1",
                JsonSerializer.Serialize(interrupted), DateTimeOffset.UtcNow)).ConfigureAwait(false);
        }

        var restartLock = ProjectRunLock.TryAcquire(identity);
        if (!restartLock.IsOwned)
        {
            restartLock.Dispose();
            throw new InvalidOperationException("Fresh production host could not reacquire the project singleton lock after disposal.");
        }

        _restartLock = restartLock;
        try
        {
            await ConstructFreshHostAsync(runId, restartLock).ConfigureAwait(false);
        }
        catch
        {
            restartLock.Dispose();
            _restartLock = null;
            throw;
        }
    }

    private async Task ConstructFreshHostAsync(ProjectRunId? runId, ProjectRunLock? projectLock)
    {
        Store = new SqliteStateStore(_databasePath);
        await Store.InitializeAsync().ConfigureAwait(false);
        await Store.SaveSettingsAsync(new PccExecutiveSettings(
            "BrowserChat", PCCExecutive.Browser.DispatchMode.AutomaticStaged.ToString(), 5, 0, true, false)).ConfigureAwait(false);

        var processInspector = new ControlledProcessInspector(_external);
        var browserHost = new ControlledBrowserRuntimeHost(_root, _external);
        var markers = new ControlledMarkerStore();
        Ownership = new ControlledOwnershipProof(_external);
        Sessions = new BrowserSessionController(Store, browserHost, Ownership, markers, processInspector);
        Adapter = new ScriptedBrowserAdapter(_external);
        SendGate = new GlobalBrowserSendGate();
        var browserProvider = new BrowserChatProvider(Store, Adapter, Store, new WrongChatGuard(), SendGate, Ownership);
        AgentProvider = new BrowserAgentProviderAdapter(Store, browserProvider, Ownership);
        var pausePort = new BrowserNewSendPausePort(SendGate);
        var run = runId is null ? null : await Store.LoadProjectRunAsync(runId.Value).ConfigureAwait(false);

        var constructor = typeof(PccExecutiveRuntimeHost)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single();
        Host = (PccExecutiveRuntimeHost)constructor.Invoke(new object?[]
        {
            Store, projectLock, Pcc, Baseline, Sessions, Store, Ownership, pausePort,
            AgentProvider, Adapter, SendGate, new HttpClient(), new HttpClient(), run
        });

        if (run is not null)
        {
            SetField(Host, "_projectControlId", Route.ProjectControlId);
            SetField(Host, "_projectDisplay", Route.DisplayName);
            SetField(Host, "_projectRepository", Route.Repository);
            await InvokePrivateAsync(Host, "RecoverStartupBrowserStateAsync", CancellationToken.None).ConfigureAwait(false);
        }

        var rollover = AutonomousConversationRolloverRuntime.Attach(Host);
        SetField(Host, "_rolloverRuntime", rollover);
        await InvokePrivateAsync(Host, "RefreshLocalSnapshotAsync", CancellationToken.None).ConfigureAwait(false);
    }

    internal Task<bool> RunIndependentFinalVerificationAsync() =>
        InvokePrivateAsync<bool>(Host, "RunIndependentFinalVerificationAsync", CancellationToken.None);

    internal Task RecordRuntimeLoopErrorAsync(string message) =>
        InvokePrivateAsync(Host, "RecordRuntimeLoopErrorAsync", new InvalidOperationException(message), CancellationToken.None);

    internal Task InvokeGovernedRolloverAsync(BrowserRuntimeRecord runtime, ConversationRecord predecessor, string reason)
    {
        var rollover = RolloverRuntime ?? throw new InvalidOperationException("Production rollover runtime is not attached.");
        return InvokePrivateAsync(rollover, "GovernedRolloverAsync", runtime, predecessor, reason, CancellationToken.None);
    }

    internal async Task<ConversationRecord> ActiveBrowserConversationAsync(LogicalAgentId agentId)
    {
        var records = await Store.ListBrowserConversationsAsync().ConfigureAwait(false);
        return records
            .Where(x => StringComparer.Ordinal.Equals(x.ProjectRunId, Run.Id.ToString()) &&
                        StringComparer.Ordinal.Equals(x.LogicalAgentId, agentId.ToString()) &&
                        x.State == ConversationLifecycleState.Active)
            .OrderByDescending(x => x.Sequence)
            .ThenByDescending(x => x.CreatedAt)
            .First();
    }

    internal async Task<IReadOnlyList<ConversationRecord>> BrowserConversationsAsync(LogicalAgentId agentId)
    {
        var records = await Store.ListBrowserConversationsAsync().ConfigureAwait(false);
        return records
            .Where(x => StringComparer.Ordinal.Equals(x.ProjectRunId, Run.Id.ToString()) &&
                        StringComparer.Ordinal.Equals(x.LogicalAgentId, agentId.ToString()))
            .OrderBy(x => x.Sequence)
            .ToArray();
    }

    internal void MakeCanonicalTaskTerminal(string state = "DONE")
    {
        Route = BuildRoute(state);
        Pcc.Route = Route;
        Baseline.Current = BuildBaseline(Route);
    }

    internal void SetBaseline(ProjectBaselineSnapshot snapshot) => Baseline.Current = snapshot;

    internal static ProjectRoutingSnapshot BuildRoute(string canonicalState)
    {
        var task = new CanonicalTaskSnapshot(
            "PCCEXECUTIVE-T0001", ProjectControlId, null, "Final runtime closure", canonicalState, "P0",
            "worker/pcc-final-runtime-e2e-closure", "main", ExactHead, ExactHead, "0.1.0",
            ["src", "tests"], [], ["all mandatory runtime acceptance is green"], [], ["exact-head CI"]);
        var provenance = new ProjectControlProvenance(
            "walidatiyaai2025-gif/project-control-center", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "1.6.0", "v1", Now, EvidenceFreshness.Current);
        return new ProjectRoutingSnapshot(
            ProjectControlId, "PCC Executive", Repository, ProjectModel.Standalone, ProjectScopeKind.Project,
            null, null, null, "READY", "READY", ["PCC Executive"], [task], null, provenance);
    }

    internal static ProjectBaselineSnapshot BuildBaseline(
        ProjectRoutingSnapshot route,
        string? head = null,
        string ci = "success",
        EvidenceFreshness freshness = EvidenceFreshness.Current,
        IReadOnlyList<string>? blockers = null)
    {
        var exactHead = head ?? ExactHead;
        return new ProjectBaselineSnapshot(
            route.ProjectControlId, route.DisplayName, route.Repository, route.ProjectModel, route.Scope,
            route.VariantId, route.ImplementationLocation, route.Provenance.SourceSha, route.RoutingIdentity,
            "main", exactHead, route.CanonicalTasks, [],
            new GitHubCheckSummary(route.Repository, exactHead, ci,
                [new GitHubCheckSnapshot("runtime-e2e", "completed", ci, null)]),
            route.DesiredState, null, blockers ?? [], Now, freshness);
    }

    internal static string PlanJson(ProjectRoutingSnapshot route, decimal estimate, string decision, params PlannedTask[] planned) =>
        JsonSerializer.Serialize(new
        {
            managerEstimate = estimate,
            expectedHead = ExactHead,
            expectedRoutingIdentity = route.RoutingIdentity,
            projectDecision = decision,
            knownBlockers = Array.Empty<string>(),
            tasks = planned.Select(x => new
            {
                taskId = x.Id.Value,
                objective = x.Objective,
                repository = Repository,
                paths = new[] { x.Path },
                components = Array.Empty<string>(),
                exclusiveResources = x.ExclusiveResources,
                dependencies = x.Dependencies.Select(d => d.Value).ToArray(),
                acceptanceCriteria = new[] { "deterministic production runtime acceptance" },
                evidenceExpected = new[] { "exact-head evidence" },
                priority = x.Priority,
                suggestedWorkerSlot = (int?)null,
                reason = "final production runtime acceptance",
                knownBlockers = Array.Empty<string>(),
                requiredPreviousTasks = Array.Empty<Guid>(),
                recommendedExecutionMode = "AutomaticStaged",
                targetScope = "Project",
                targetVariant = (string?)null,
                expectedHead = ExactHead,
                relatedPullRequest = (int?)null,
                expectedPullRequestState = (string?)null,
                targetBranch = (string?)null,
                featureExpansion = false
            }).ToArray()
        });

    internal static string Handoff(TaskId taskId, WorkerSlotId slot, string path) => string.Join('\n',
        $"TASK: {taskId.Value}",
        $"WORKER_SLOT: Worker {slot.Value}",
        $"PROJECT: {ProjectControlId}",
        $"REPOSITORY: {Repository}",
        "STATUS: DONE",
        $"HEAD: {ExactHead}",
        "BRANCH: worker/pcc-runtime-e2e-final",
        "PR: N/A",
        $"CHANGED: {path}",
        "TESTS: PASS",
        "BUILD: PASS",
        "BLOCKER: N/A",
        "NEXT_ACTION: manager-review");

    internal static PlannedTask CreatePlannedTask(
        string objective,
        string path,
        int priority = 0,
        IReadOnlyList<TaskId>? dependencies = null,
        params string[] exclusiveResources) =>
        new(TaskId.New(), objective, path, priority, dependencies ?? [], exclusiveResources);

    internal static T GetField<T>(object target, string name)
    {
        var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(target.GetType().FullName, name);
        return (T)field.GetValue(target)!;
    }

    internal static void SetField(object target, string name, object? value)
    {
        var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(target.GetType().FullName, name);
        field.SetValue(target, value);
    }

    internal static async Task InvokePrivateAsync(object target, string name, params object?[] args)
    {
        var method = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(target.GetType().FullName, name);
        var task = method.Invoke(target, args) as Task
            ?? throw new InvalidOperationException($"{name} did not return Task.");
        await task.ConfigureAwait(false);
    }

    internal static async Task<T> InvokePrivateAsync<T>(object target, string name, params object?[] args)
    {
        var method = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(target.GetType().FullName, name);
        var task = method.Invoke(target, args) as Task<T>
            ?? throw new InvalidOperationException($"{name} did not return Task<{typeof(T).Name}>.");
        return await task.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        Exception? shutdownFailure = null;
        ProjectRunLock? fallbackLock = null;
        if (Host is not null)
        {
            try { fallbackLock = GetField<ProjectRunLock?>(Host, "_projectLock"); } catch { }
            try
            {
                await Host.DisposeAsync().ConfigureAwait(false);
                _restartLock = null;
            }
            catch (Exception ex)
            {
                shutdownFailure = ex;
                try
                {
                    // Normal release belongs to the production host. Fallback only when shutdown
                    // failed before it could release the process-wide project lease.
                    fallbackLock?.Dispose();
                }
                catch (Exception releaseFailure)
                {
                    shutdownFailure = new AggregateException(ex, releaseFailure);
                }
                _restartLock = null;
            }
        }
        else
        {
            _restartLock?.Dispose();
            _restartLock = null;
        }

        try { Directory.Delete(_root, true); } catch { }
        if (shutdownFailure is not null)
            throw new InvalidOperationException("Production host disposal failed during E2E acceptance cleanup.", shutdownFailure);
    }

    internal sealed record PlannedTask(
        TaskId Id,
        string Objective,
        string Path,
        int Priority,
        IReadOnlyList<TaskId> Dependencies,
        IReadOnlyList<string> ExclusiveResources);

    internal sealed class ControlledProjectControl(ProjectRoutingSnapshot initial) : IProjectControlResolver
    {
        internal ProjectRoutingSnapshot Route { get; set; } = initial;
        internal int ResolveCalls { get; private set; }

        public Task<ProjectResolution> ResolveProjectAsync(string nameOrAlias, CancellationToken cancellationToken = default)
        {
            ResolveCalls++;
            return Task.FromResult(string.Equals(nameOrAlias, Route.ProjectControlId, StringComparison.OrdinalIgnoreCase)
                ? new ProjectResolution(ProjectResolutionStatus.Success, Route, null)
                : new ProjectResolution(ProjectResolutionStatus.ProjectNotFound, null, "PROJECT_NOT_FOUND"));
        }

        public Task<ProjectResolution> GetProjectAsync(string projectControlId, string? variantId = null, ProjectScopeKind? scope = null, CancellationToken cancellationToken = default) =>
            ResolveProjectAsync(projectControlId, cancellationToken);

        public Task<ExternalResult<ProjectRoutingSnapshot>> GetRoutingSnapshotAsync(string projectControlId, string? variantId = null, ProjectScopeKind? scope = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExternalResult<ProjectRoutingSnapshot>(ExternalReadStatus.Success, Route, Now));

        public Task<ExternalResult<IReadOnlyList<CanonicalTaskSnapshot>>> GetCanonicalTasksAsync(string projectControlId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExternalResult<IReadOnlyList<CanonicalTaskSnapshot>>(ExternalReadStatus.Success, Route.CanonicalTasks, Now));

        public Task<ExternalResult<DesiredStateSnapshot>> GetDesiredStateAsync(string projectControlId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExternalResult<DesiredStateSnapshot>(ExternalReadStatus.NotFound, null, Now));
    }

    internal sealed class MutableBaselineBuilder(ProjectBaselineSnapshot initial) : IProjectBaselineBuilder
    {
        internal ProjectBaselineSnapshot Current { get; set; } = initial;
        internal ExternalReadStatus Status { get; set; } = ExternalReadStatus.Success;
        internal int Calls { get; private set; }

        public Task<ExternalResult<ProjectBaselineSnapshot>> BuildAsync(string nameOrAlias, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(Status == ExternalReadStatus.Success
                ? new ExternalResult<ProjectBaselineSnapshot>(Status, Current, Current.CapturedAt)
                : new ExternalResult<ProjectBaselineSnapshot>(Status, null, DateTimeOffset.UtcNow, false, Status.ToString().ToUpperInvariant()));
        }
    }

    internal sealed class ControlledExternalState
    {
        internal int NextProcessId = 2000;
        internal ConcurrentDictionary<int, string> Processes { get; } = new();
        internal ConcurrentDictionary<string, string> ProviderIdentities { get; } = new(StringComparer.Ordinal);
        internal ConcurrentDictionary<string, ChatGptSemanticSnapshot> Semantics { get; } = new(StringComparer.Ordinal);
        internal ConcurrentQueue<SubmissionPlan> SubmissionPlans { get; } = new();
        internal ConcurrentDictionary<string, int> EnterByRuntime { get; } = new(StringComparer.Ordinal);
        internal ConcurrentQueue<SubmittedPrompt> SubmittedPrompts { get; } = new();
        internal Func<BrowserRuntimeRecord, Task>? BeforeFinalAuthorization { get; set; }
    }

    internal sealed record SubmissionPlan(bool ProvenSubmitted, bool SubmittedUnknown, string Reason);
    internal sealed record SubmittedPrompt(string RuntimeId, BrowserDispatchExpectation Expectation, string Prompt);

    internal sealed class ControlledBrowserRuntimeHost(string root, ControlledExternalState state) : IBrowserRuntimeHost
    {
        public Task<BrowserRuntimeRecord> LaunchAsync(BrowserSessionRequest request, CancellationToken cancellationToken = default)
        {
            var pid = Interlocked.Increment(ref state.NextProcessId);
            var start = $"pid:{pid}:start:acceptance";
            state.Processes[pid] = start;
            var runtimeId = request.RuntimeId ?? Guid.NewGuid().ToString("N");
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new BrowserRuntimeRecord
            {
                RuntimeId = runtimeId,
                ProjectRunId = request.ProjectRunId,
                LogicalAgentId = request.LogicalAgentId,
                WorkerSlotId = request.WorkerSlotId,
                TaskId = request.TaskId,
                ProcessId = pid,
                ProcessStartIdentity = start,
                ContextIdentity = $"ctx:{runtimeId}",
                ProfilePath = Path.Combine(root, "profiles", runtimeId),
                CreatedByPcc = true,
                AdoptedExplicitly = false,
                ConversationIdentity = request.ConversationIdentity,
                ProviderConversationIdentity = request.ProviderConversationIdentity,
                Visibility = request.DefaultVisibility,
                State = request.DefaultVisibility == BrowserVisibility.Hidden ? BrowserSessionState.Hidden : BrowserSessionState.Visible,
                LastHeartbeatAt = now,
                LastActivityAt = now,
                OwnershipNonce = $"nonce:{runtimeId}"
            });
        }

        public Task<bool> RecoverAsync(BrowserRuntimeRecord runtime, CancellationToken cancellationToken = default) =>
            Task.FromResult(runtime.ProcessId is int pid && state.Processes.ContainsKey(pid));

        public Task SetVisibilityAsync(BrowserRuntimeRecord runtime, BrowserVisibility visibility, bool bringToFront, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task KillAsync(BrowserRuntimeRecord runtime, OwnershipProof proof, CancellationToken cancellationToken = default)
        {
            if (runtime.ProcessId is int pid) state.Processes.TryRemove(pid, out _);
            return Task.CompletedTask;
        }

        public Task<BrowserRuntimeTelemetry> GetTelemetryAsync(BrowserRuntimeRecord runtime, CancellationToken cancellationToken = default) =>
            Task.FromResult(new BrowserRuntimeTelemetry(
                runtime.RuntimeId,
                runtime.ProcessId is int pid && state.Processes.ContainsKey(pid),
                1, 1024, TimeSpan.Zero, runtime.LastHeartbeatAt, false, runtime.IsArchived));
    }

    internal sealed class ControlledProcessInspector(ControlledExternalState state) : IProcessInspector
    {
        public bool IsAlive(int processId) => state.Processes.ContainsKey(processId);
        public string? GetStartIdentity(int processId) => state.Processes.TryGetValue(processId, out var value) ? value : null;
    }

    internal sealed class ControlledMarkerStore : IOwnershipMarkerStore
    {
        private readonly ConcurrentDictionary<string, OwnershipMarker> _items = new(StringComparer.OrdinalIgnoreCase);
        public Task WriteAsync(OwnershipMarker marker, CancellationToken cancellationToken = default)
        {
            _items[marker.ProfilePath] = marker;
            return Task.CompletedTask;
        }
        public Task<OwnershipMarker?> ReadAsync(string profilePath, CancellationToken cancellationToken = default) =>
            Task.FromResult(_items.TryGetValue(marker.ProfilePath, out var value) ? value : null);
    }

    internal sealed class ControlledOwnershipProof(ControlledExternalState state) : IOwnershipProofService
    {
        internal bool Allow { get; set; } = true;
        public Task<OwnershipProof> ProveAsync(BrowserRuntimeRecord runtime, CancellationToken cancellationToken = default)
        {
            var alive = runtime.ProcessId is int pid && state.Processes.ContainsKey(pid);
            return Task.FromResult(Allow && alive && runtime.CreatedByPcc && !runtime.IsArchived
                ? OwnershipProof.Proven(runtime.RuntimeId)
                : OwnershipProof.Denied(runtime.RuntimeId,
                    Allow ? "PROCESS_OR_RUNTIME_OWNERSHIP_INVALID" : "OWNERSHIP_DENIED_BY_ACCEPTANCE_BOUNDARY"));
        }
    }

    internal sealed class ScriptedBrowserAdapter(ControlledExternalState state) : IPhysicalSubmitAuthorizationAdapter
    {
        public string AdapterVersion => "controlled-external-browser-v1";
        internal int PhysicalEnterCount => state.EnterByRuntime.Values.Sum();
        internal IReadOnlyCollection<SubmittedPrompt> SubmittedPrompts => state.SubmittedPrompts.ToArray();
        internal int EnterCount(string runtimeId) => state.EnterByRuntime.TryGetValue(runtimeId, out var value) ? value : 0;
        internal void QueueSubmission(bool provenSubmitted, bool submittedUnknown, string reason) =>
            state.SubmissionPlans.Enqueue(new SubmissionPlan(provenSubmitted, submittedUnknown, reason));
        internal void SetSemantic(string runtimeId, ChatGptSemanticSnapshot semantic) => state.Semantics[runtimeId] = semantic;
        internal void BeforeFinalAuthorization(Func<BrowserRuntimeRecord, Task>? callback) => state.BeforeFinalAuthorization = callback;

        public Task<ChatGptSemanticSnapshot> InspectAsync(BrowserRuntimeRecord runtime, BrowserDispatchExpectation expectation, CancellationToken cancellationToken = default) =>
            Task.FromResult(state.Semantics.TryGetValue(runtime.RuntimeId, out var semantic) ? semantic : SemanticReady());

        public Task<AdapterSubmissionResult> SubmitAsync(BrowserRuntimeRecord runtime, BrowserDispatchExpectation expectation, string prompt, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AdapterSubmissionResult(false, false, false, "PHYSICAL_AUTHORIZATION_REQUIRED", ["submit:refused-without-final-authorization"]));

        public async Task<AdapterSubmissionResult> SubmitAuthorizedAsync(
            BrowserRuntimeRecord runtime,
            BrowserDispatchExpectation expectation,
            string prompt,
            Func<CancellationToken, Task<PreEnterAuthorizationDecision>> authorizeBeforeEnter,
            CancellationToken cancellationToken = default)
        {
            if (state.BeforeFinalAuthorization is not null)
            {
                var callback = state.BeforeFinalAuthorization;
                state.BeforeFinalAuthorization = null;
                await callback(runtime).ConfigureAwait(false);
            }

            var authorization = await authorizeBeforeEnter(cancellationToken).ConfigureAwait(false);
            if (!authorization.Authorized)
                return new(false, false, false, "PRE_ENTER_AUTHORIZATION_DENIED",
                    authorization.Evidence.Append(authorization.Reason).ToArray());

            state.EnterByRuntime.AddOrUpdate(runtime.RuntimeId, 1, static (_, count) => count + 1);
            state.SubmittedPrompts.Enqueue(new SubmittedPrompt(runtime.RuntimeId, expectation, prompt));
            state.ProviderIdentities.TryAdd(runtime.RuntimeId, $"provider-{runtime.RuntimeId[..Math.Min(12, runtime.RuntimeId.Length)]}");
            var plan = state.SubmissionPlans.TryDequeue(out var queued)
                ? queued
                : new SubmissionPlan(true, false, "SUBMISSION_PROVEN");
            return new(true, plan.ProvenSubmitted, plan.SubmittedUnknown, plan.Reason,
                ["physical-enter:1", $"runtime:{runtime.RuntimeId}"]);
        }

        public Task<string?> GetCurrentConversationIdentityAsync(BrowserRuntimeRecord runtime, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(state.ProviderIdentities.GetOrAdd(
                runtime.RuntimeId, $"provider-{runtime.RuntimeId[..Math.Min(12, runtime.RuntimeId.Length)]}"));

        internal static ChatGptSemanticSnapshot SemanticReady(
            string? response = null,
            bool complete = false,
            PageHealth health = PageHealth.Healthy,
            AuthState auth = AuthState.Authenticated,
            params string[] healthEvidence) =>
            new(
                SemanticDetection<InputState>.Create(InputState.Ready, .99, "controlled", "input:ready"),
                SemanticDetection<GenerationState>.Create(
                    complete ? GenerationState.Complete : GenerationState.Idle, .99, "controlled",
                    complete ? "generation:complete" : "generation:idle"),
                SemanticDetection<AuthState>.Create(auth, .99, "controlled", auth.ToString()),
                SemanticDetection<ConversationMatch>.Create(ConversationMatch.Match, .99, "controlled", "conversation:match"),
                SemanticDetection<PageHealth>.Create(
                    health, .99, "controlled", healthEvidence.Length == 0 ? [health.ToString()] : healthEvidence),
                complete ? ResponseCompleteness.Complete : ResponseCompleteness.None,
                response is null ? 0 : 1,
                response,
                DateTimeOffset.UtcNow,
                "controlled-external-browser-v1");
    }
}
