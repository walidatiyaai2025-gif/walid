using System.Collections.Concurrent;
using System.Text.Json;
using PCCExecutive.Browser;

namespace PCCExecutive.Browser.Acceptance;

public enum AcceptanceRole { Manager, Worker }

public sealed record AcceptanceTask(string TaskId, int WorkerSlot, string Prompt, string ScopeKey);
public sealed record AcceptanceHandoff(string TaskId, int WorkerSlot, string Status, string Head, IReadOnlyList<string> Changed, IReadOnlyList<string> Validation, string? Blocker, string NextAction);
public sealed record AcceptanceTrace(string RuntimeId, string LogicalAgentId, string State, long ElapsedMilliseconds, IReadOnlyList<string> Evidence);
public sealed record AcceptanceWaveResult(
    int WorkerCount,
    IReadOnlyList<BrowserDispatchResult> WorkerDispatches,
    IReadOnlyList<AcceptanceHandoff> Handoffs,
    BrowserDispatchResult ManagerSummaryDispatch,
    IReadOnlyDictionary<string, int> SubmitCounts,
    IReadOnlyList<AcceptanceTrace> Trace);

public sealed record AcceptanceScenarioReport(
    string Scenario,
    string SourceSha,
    string AdapterVersion,
    IReadOnlyList<string> RuntimeIds,
    IReadOnlyList<string> LogicalAgentIds,
    IReadOnlyList<string> StateTransitions,
    IReadOnlyList<long> TimingsMilliseconds,
    IReadOnlyList<string> Failures,
    IReadOnlyList<string> EvidenceSummary);

public sealed record AcceptanceRestartEnvelope(
    IReadOnlyList<BrowserRuntimeRecord> Runtimes,
    IReadOnlyList<DispatchLedgerEntry> Dispatches,
    IReadOnlyList<ConversationRecord> Conversations,
    string Phase,
    DateTimeOffset CapturedAt);

public static class AcceptanceArtifactWriter
{
    private static readonly string[] Forbidden = ["cookie", "authorization", "bearer ", "session-token", "access-token", "refresh-token", "password", "credential"];

    public static string Serialize(AcceptanceScenarioReport report)
    {
        var safe = report with
        {
            EvidenceSummary = report.EvidenceSummary.Where(IsSafeEvidence).Select(Sanitize).Take(100).ToArray(),
            Failures = report.Failures.Select(Sanitize).Take(50).ToArray()
        };
        return JsonSerializer.Serialize(safe, new JsonSerializerOptions { WriteIndented = true });
    }

    private static bool IsSafeEvidence(string value) =>
        !Forbidden.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static string Sanitize(string value)
    {
        var normalized = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 300 ? normalized : normalized[..300];
    }
}

public sealed class DeterministicChatGptAdapter : IChatGptBrowserAdapter
{
    private readonly ConcurrentDictionary<string, ChatGptSemanticSnapshot> _snapshots = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AdapterSubmissionResult> _submissions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _submitCounts = new(StringComparer.Ordinal);

    public string AdapterVersion => "acceptance-fixture-v1";
    public IReadOnlyDictionary<string, int> SubmitCounts => _submitCounts;

    public void SetSnapshot(string runtimeId, ChatGptSemanticSnapshot snapshot) => _snapshots[runtimeId] = snapshot;
    public void SetSubmission(string runtimeId, AdapterSubmissionResult result) => _submissions[runtimeId] = result;

    public Task<ChatGptSemanticSnapshot> InspectAsync(BrowserRuntimeRecord runtime, BrowserDispatchExpectation expectation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_snapshots.TryGetValue(runtime.RuntimeId, out var snapshot)
            ? snapshot
            : AcceptanceSnapshots.Unknown());
    }

    public Task<AdapterSubmissionResult> SubmitAsync(BrowserRuntimeRecord runtime, BrowserDispatchExpectation expectation, string prompt, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _submitCounts.AddOrUpdate(runtime.RuntimeId, 1, static (_, count) => checked(count + 1));
        return Task.FromResult(_submissions.TryGetValue(runtime.RuntimeId, out var result)
            ? result
            : new AdapterSubmissionResult(false, false, false, "NO_ACCEPTANCE_SUBMISSION_CONFIGURED", ["acceptance:submission-unconfigured"]));
    }
}

public static class AcceptanceSnapshots
{
    public static ChatGptSemanticSnapshot Healthy(
        ConversationMatch conversation = ConversationMatch.Match,
        GenerationState generation = GenerationState.Idle,
        ResponseCompleteness completeness = ResponseCompleteness.None,
        string? response = null,
        params string[] extraEvidence) =>
        Snapshot(InputState.Ready, generation, AuthState.Authenticated, conversation, PageHealth.Healthy, completeness, response, extraEvidence);

    public static ChatGptSemanticSnapshot RateLimited(params string[] evidence) =>
        Snapshot(InputState.Ready, GenerationState.Idle, AuthState.Authenticated, ConversationMatch.Match, PageHealth.RateLimited, ResponseCompleteness.None, null,
            evidence.Length == 0 ? ["sending-too-quickly", "account-level:rate-limit"] : evidence);

    public static ChatGptSemanticSnapshot TempError(bool global = false) =>
        Snapshot(InputState.Ready, GenerationState.Idle, AuthState.Authenticated, ConversationMatch.Match, PageHealth.TempError, ResponseCompleteness.None, null,
            global ? ["account-level:temporary-error"] : ["session:temporary-error"]);

    public static ChatGptSemanticSnapshot LoginRequired() =>
        Snapshot(InputState.Unknown, GenerationState.Unknown, AuthState.LoginRequired, ConversationMatch.Match, PageHealth.Unknown, ResponseCompleteness.None, null, ["login-required"]);

    public static ChatGptSemanticSnapshot Offline() =>
        Snapshot(InputState.Unknown, GenerationState.Unknown, AuthState.Authenticated, ConversationMatch.Match, PageHealth.Offline, ResponseCompleteness.None, null, ["offline"]);

    public static ChatGptSemanticSnapshot Unknown() =>
        Snapshot(InputState.Unknown, GenerationState.Unknown, AuthState.Unknown, ConversationMatch.Unknown, PageHealth.Unknown, ResponseCompleteness.Unknown, null, ["unknown-ui"]);

    public static ChatGptSemanticSnapshot Partial(string text) =>
        Snapshot(InputState.Ready, GenerationState.Complete, AuthState.Authenticated, ConversationMatch.Match, PageHealth.Healthy, ResponseCompleteness.Partial, text, ["partial-response"]);

    private static ChatGptSemanticSnapshot Snapshot(
        InputState input,
        GenerationState generation,
        AuthState auth,
        ConversationMatch conversation,
        PageHealth health,
        ResponseCompleteness completeness,
        string? response,
        IReadOnlyList<string> evidence)
    {
        var inputConfidence = input == InputState.Unknown ? .10 : .95;
        var generationConfidence = generation == GenerationState.Unknown ? .10 : .95;
        var authConfidence = auth == AuthState.Unknown ? .10 : .95;
        var conversationConfidence = conversation == ConversationMatch.Unknown ? .10 : .95;
        var healthConfidence = health == PageHealth.Unknown ? .10 : .95;

        return new(
            SemanticDetection<InputState>.Create(input, inputConfidence, "acceptance-fixture-v1", evidence.Prepend($"input:{input}").ToArray()),
            SemanticDetection<GenerationState>.Create(generation, generationConfidence, "acceptance-fixture-v1", evidence.Prepend($"generation:{generation}").ToArray()),
            SemanticDetection<AuthState>.Create(auth, authConfidence, "acceptance-fixture-v1", evidence.Prepend($"auth:{auth}").ToArray()),
            SemanticDetection<ConversationMatch>.Create(conversation, conversationConfidence, "acceptance-fixture-v1", evidence.Prepend($"conversation:{conversation}").ToArray()),
            SemanticDetection<PageHealth>.Create(health, healthConfidence, "acceptance-fixture-v1", evidence.Prepend($"health:{health}").ToArray()),
            completeness,
            response is null ? 0 : 1,
            response,
            DateTimeOffset.UtcNow,
            "acceptance-fixture-v1");
    }
}

public sealed record AcceptanceHtmlFixture(string Name, string Url, string Html, string ExpectedConversationId);

public static class AcceptanceHtmlFixtures
{
    private static AcceptanceHtmlFixture F(string name, string conversationId, string html) =>
        new(name, $"https://chatgpt.com/c/{conversationId}", html, conversationId);

    public static IReadOnlyList<AcceptanceHtmlFixture> All { get; } =
    [
        F("healthy-idle", "conv-a", "<textarea data-testid='composer-text-input'></textarea>"),
        F("generating", "conv-a", "<textarea data-testid='composer-text-input'></textarea><button data-testid='stop-button'>Stop</button>"),
        F("response-complete", "conv-a", "<article data-message-author-role='assistant'>done<button data-testid='copy-turn-action-button'>Copy</button></article><textarea></textarea>"),
        F("slow-generation", "conv-a", "<textarea></textarea><button data-testid='stop-button'>Stop</button><div>taking longer than expected</div>"),
        F("sending-too-fast", "conv-a", "<textarea></textarea><div>sending too quickly - try again in a few minutes</div>"),
        F("temporary-error", "conv-a", "<textarea></textarea><div>something went wrong</div>"),
        F("login-required", "conv-a", "<button>Log in</button>"),
        F("challenge", "conv-a", "<div>Verify you are human</div>"),
        F("partial-response", "conv-a", "<textarea></textarea><article data-message-author-role='assistant'>partial</article><button>Continue generating</button>"),
        F("context-limit", "conv-a", "<textarea></textarea><div>This conversation has reached its limit. Start a new chat to continue.</div>"),
        F("changed-unknown-ui", "conv-a", "<div class='new-shell-v999'>unknown controls</div>"),
        F("uncertain-submission", "conv-a", "<textarea></textarea><div data-submit-uncertain='true'></div>"),
        new("wrong-conversation", "https://chatgpt.com/c/conv-b", "<textarea></textarea>", "conv-a"),
        F("continuation-success", "conv-next", "<textarea></textarea><article data-message-author-role='assistant'>ack<button data-testid='copy-turn-action-button'>Copy</button></article>"),
        F("continuation-failed", "conv-next", "<textarea></textarea><div data-continuation='failed'></div>"),
        F("offline", "conv-a", "<div>You are offline</div>")
    ];
}

public sealed class ControlledBrowserAcceptanceHarness
{
    private readonly InMemoryBrowserRuntimeRegistry _registry = new();
    private readonly InMemoryDispatchLedger _ledger = new();
    private readonly GlobalBrowserSendGate _globalGate = new();
    private readonly DeterministicChatGptAdapter _adapter = new();
    private readonly BrowserDispatchScheduler _scheduler = new();
    private readonly BrowserChatProvider _provider;
    private readonly Dictionary<int, BrowserRuntimeRecord> _workers = new();
    private BrowserRuntimeRecord? _manager;
    private readonly List<AcceptanceTrace> _trace = [];

    public ControlledBrowserAcceptanceHarness()
    {
        _provider = new BrowserChatProvider(_registry, _adapter, _ledger, new WrongChatGuard(), _globalGate);
    }

    public InMemoryBrowserRuntimeRegistry Registry => _registry;
    public InMemoryDispatchLedger Ledger => _ledger;
    public GlobalBrowserSendGate GlobalGate => _globalGate;
    public DeterministicChatGptAdapter Adapter => _adapter;
    public IReadOnlyList<AcceptanceTrace> Trace => _trace;
    public BrowserRuntimeRecord Manager => _manager ?? throw new InvalidOperationException("Topology has not been created.");
    public BrowserRuntimeRecord Worker(int slot) => _workers.TryGetValue(slot, out var runtime) ? runtime : throw new KeyNotFoundException($"Worker {slot} does not exist.");

    public async Task CreateTopologyAsync(int workerCount, CancellationToken cancellationToken = default)
    {
        if (workerCount is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(workerCount));
        _workers.Clear();
        _manager = CreateRuntime("manager-runtime", "manager-agent", null, "manager-task", "M-C01");
        await _registry.UpsertAsync(_manager, cancellationToken);
        for (var slot = 1; slot <= workerCount; slot++)
        {
            var runtime = CreateRuntime($"worker-{slot}-runtime", $"worker-{slot}-agent", slot, $"worker-{slot}-task", $"W{slot}-C01");
            _workers[slot] = runtime;
            await _registry.UpsertAsync(runtime, cancellationToken);
        }
    }

    public async Task<AcceptanceWaveResult> RunIndependentWaveAsync(int workerCount, CancellationToken cancellationToken = default)
    {
        await CreateTopologyAsync(workerCount, cancellationToken);
        var tasks = Enumerable.Range(1, workerCount).Select(slot =>
            new AcceptanceTask($"task-{slot}", slot, $"Execute independent acceptance task {slot}.", $"scope-{slot}")).ToArray();
        ValidateIndependentWave(tasks);

        var dispatches = new List<BrowserDispatchResult>();
        var handoffs = new List<AcceptanceHandoff>();
        var clock = DateTimeOffset.Parse("2026-08-28T00:00:00Z");
        DateTimeOffset? lastDispatchAt = null;

        foreach (var task in tasks)
        {
            var runtime = await BindTaskAsync(task, cancellationToken);
            _adapter.SetSnapshot(runtime.RuntimeId, AcceptanceSnapshots.Healthy());
            _adapter.SetSubmission(runtime.RuntimeId, new(true, true, false, "SUBMISSION_PROVEN", ["acceptance:worker-dispatch-proven"]));
            var timing = _scheduler.Evaluate(clock, lastDispatchAt, dispatches.Count(x => x.Outcome == BrowserDispatchOutcome.Submitted),
                new DispatchSchedulerOptions(), _globalGate.Snapshot);
            if (!timing.MayDispatch)
            {
                clock = timing.NextEligibleAt ?? clock.AddSeconds(10);
                timing = _scheduler.Evaluate(clock, lastDispatchAt, dispatches.Count(x => x.Outcome == BrowserDispatchOutcome.Submitted),
                    new DispatchSchedulerOptions(), _globalGate.Snapshot);
            }
            if (!timing.MayDispatch) throw new InvalidOperationException($"Staged dispatch did not become eligible: {timing.Reason}");

            var request = Request(runtime, task.TaskId, $"dispatch-{task.TaskId}", task.Prompt);
            var result = await _provider.SendAsync(runtime.RuntimeId, request, cancellationToken);
            dispatches.Add(result);
            _trace.Add(new(runtime.RuntimeId, runtime.LogicalAgentId, result.State.ToString(), 0, result.Evidence));
            if (result.Outcome != BrowserDispatchOutcome.Submitted)
                throw new InvalidOperationException($"Worker {task.WorkerSlot} dispatch was not submitted: {result.Reason}");

            handoffs.Add(new(task.TaskId, task.WorkerSlot, "DONE", $"head-{task.WorkerSlot}", [$"scope-{task.WorkerSlot}.cs"], ["deterministic-handoff:validated"], null, "Reconcile into wave summary."));
            lastDispatchAt = clock;
            clock = clock.AddSeconds(10);
        }

        var manager = Manager with { TaskId = "wave-summary-task" };
        _manager = manager;
        await _registry.UpsertAsync(manager, cancellationToken);
        _adapter.SetSnapshot(manager.RuntimeId, AcceptanceSnapshots.Healthy());
        _adapter.SetSubmission(manager.RuntimeId, new(true, true, false, "SUBMISSION_PROVEN", ["acceptance:manager-summary-proven"]));
        var summaryRequest = Request(manager, "wave-summary-task", "dispatch-wave-summary",
            $"WAVE_SUMMARY: {string.Join(',', handoffs.Select(x => $"{x.TaskId}:{x.Status}"))}");
        var summary = await _provider.SendAsync(manager.RuntimeId, summaryRequest, cancellationToken);
        _trace.Add(new(manager.RuntimeId, manager.LogicalAgentId, summary.State.ToString(), 0, summary.Evidence));

        return new(workerCount, dispatches, handoffs, summary,
            _adapter.SubmitCounts.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal), _trace.ToArray());
    }

    public async Task<BrowserRuntimeRecord> BindTaskAsync(AcceptanceTask task, CancellationToken cancellationToken = default)
    {
        var current = Worker(task.WorkerSlot);
        var bound = current with { TaskId = task.TaskId };
        _workers[task.WorkerSlot] = bound;
        await _registry.UpsertAsync(bound, cancellationToken);
        return bound;
    }

    public BrowserDispatchRequest Request(BrowserRuntimeRecord runtime, string taskId, string dispatchId, string prompt) =>
        new(dispatchId, runtime.ProjectRunId, runtime.LogicalAgentId, taskId,
            runtime.ConversationIdentity ?? throw new InvalidOperationException("Conversation identity required."),
            runtime.ProviderConversationIdentity ?? throw new InvalidOperationException("Provider conversation identity required."),
            prompt);

    public async Task<AcceptanceRestartEnvelope> CaptureRestartAsync(
        IEnumerable<string> dispatchIds,
        IEnumerable<ConversationRecord>? conversations = null,
        string phase = "active-wave",
        CancellationToken cancellationToken = default)
    {
        var runtimes = await _registry.ListAsync(cancellationToken);
        var dispatches = new List<DispatchLedgerEntry>();
        foreach (var id in dispatchIds)
        {
            var entry = await _ledger.GetAsync(id, cancellationToken);
            if (entry is not null) dispatches.Add(entry);
        }
        return new(runtimes, dispatches, (conversations ?? []).ToArray(), phase, DateTimeOffset.UtcNow);
    }

    public static void ValidateRestartEnvelope(AcceptanceRestartEnvelope envelope)
    {
        foreach (var runtime in envelope.Runtimes)
        {
            if (string.IsNullOrWhiteSpace(runtime.RuntimeId) || string.IsNullOrWhiteSpace(runtime.ProjectRunId) || string.IsNullOrWhiteSpace(runtime.LogicalAgentId))
                throw new InvalidOperationException("Restart envelope lost stable browser identity.");
        }
        foreach (var dispatch in envelope.Dispatches)
        {
            if (string.IsNullOrWhiteSpace(dispatch.DispatchId) || string.IsNullOrWhiteSpace(dispatch.ContentHash))
                throw new InvalidOperationException("Restart envelope lost dispatch identity.");
        }
        foreach (var conversation in envelope.Conversations)
        {
            if (string.IsNullOrWhiteSpace(conversation.ConversationId) || string.IsNullOrWhiteSpace(conversation.LogicalAgentId))
                throw new InvalidOperationException("Restart envelope lost conversation lineage identity.");
        }
    }

    public AcceptanceScenarioReport Report(string scenario, params string[] failures)
    {
        var runtimes = _registry.ListAsync().GetAwaiter().GetResult();
        return new(
            scenario,
            Environment.GetEnvironmentVariable("GITHUB_SHA") ?? "deterministic-no-github-sha",
            _adapter.AdapterVersion,
            runtimes.Select(x => x.RuntimeId).ToArray(),
            runtimes.Select(x => x.LogicalAgentId).Distinct(StringComparer.Ordinal).ToArray(),
            _trace.Select(x => x.State).ToArray(),
            _trace.Select(x => x.ElapsedMilliseconds).ToArray(),
            failures,
            _trace.SelectMany(x => x.Evidence).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public static BrowserRuntimeRecord CreateRuntime(string runtimeId, string logicalAgentId, int? workerSlot, string taskId, string conversationId, bool owned = true, int? processId = null)
    {
        var pid = processId ?? (10_000 + Math.Abs(runtimeId.GetHashCode(StringComparison.Ordinal)) % 40_000);
        var profileRoot = Path.Combine(Path.GetTempPath(), "pcc-executive-acceptance");
        return new BrowserRuntimeRecord
        {
            RuntimeId = runtimeId,
            ProjectRunId = "acceptance-project-run",
            LogicalAgentId = logicalAgentId,
            WorkerSlotId = workerSlot?.ToString(),
            TaskId = taskId,
            ProcessId = pid,
            ProcessStartIdentity = $"pid:{pid}:start:acceptance",
            ContextIdentity = $"ctx-{runtimeId}",
            ProfilePath = Path.Combine(profileRoot, runtimeId),
            CreatedByPcc = owned,
            AdoptedExplicitly = false,
            ConversationIdentity = conversationId,
            ProviderConversationIdentity = $"https://chatgpt.com/c/{conversationId}",
            Visibility = BrowserVisibility.Hidden,
            State = BrowserSessionState.Hidden,
            LastHeartbeatAt = DateTimeOffset.UtcNow,
            LastActivityAt = DateTimeOffset.UtcNow,
            OwnershipNonce = $"nonce-{runtimeId}"
        };
    }

    private static void ValidateIndependentWave(IReadOnlyList<AcceptanceTask> tasks)
    {
        if (tasks.Count is < 1 or > 5) throw new InvalidOperationException("Acceptance wave must contain 1..5 tasks.");
        if (tasks.Select(x => x.TaskId).Distinct(StringComparer.Ordinal).Count() != tasks.Count) throw new InvalidOperationException("Duplicate task identity.");
        if (tasks.Select(x => x.WorkerSlot).Distinct().Count() != tasks.Count) throw new InvalidOperationException("Worker collision.");
        if (tasks.Select(x => x.ScopeKey).Distinct(StringComparer.Ordinal).Count() != tasks.Count) throw new InvalidOperationException("Acceptance scopes are not independent.");
    }
}
