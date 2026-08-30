[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

function Read-Normalized([string]$Path) {
    return (Get-Content $Path -Raw).Replace("`r`n", "`n")
}

function Replace-ExactlyOnce {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Old,
        [Parameter(Mandatory)][string]$New,
        [Parameter(Mandatory)][string]$Description
    )
    $text = Read-Normalized $Path
    $count = ([regex]::Matches($text, [regex]::Escape($Old))).Count
    if ($count -ne 1) {
        throw "PATCH_CONTRACT_MISMATCH: $Description expected exactly one match in $Path, found $count."
    }
    Set-Content -Path $Path -Value $text.Replace($Old, $New) -Encoding utf8 -NoNewline
    Write-Host "PATCHED: $Description"
}

$dispatchPath = Join-Path $repoRoot 'src/PCCExecutive.Browser/DispatchAndResilience.cs'
$hostPath = Join-Path $repoRoot 'src/PCCExecutive.Browser/PlaywrightChromeRuntimeHost.cs'
$pagePolicyPath = Join-Path $repoRoot 'src/PCCExecutive.Browser/ChatGptPageSelectionPolicy.cs'
$gatewayPath = Join-Path $repoRoot 'src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs'
$testPath = Join-Path $repoRoot 'tests/PCCExecutive.Browser.Tests/SingleConversationSerialSendV8BuildTests.cs'

# 1. Serialize every BrowserChatProvider send across Manager + all Workers.
$oldFields = @'
    private readonly IBrowserRuntimeRegistry _runtimes; private readonly IChatGptBrowserAdapter _adapter; private readonly IDispatchLedger _ledger; private readonly WrongChatGuard _wrongChatGuard; private readonly GlobalBrowserSendGate _globalGate; private readonly IOwnershipProofService _ownership; private readonly ConcurrentDictionary<string, SemaphoreSlim> _dispatchGates = new(StringComparer.Ordinal);
'@
$newFields = @'
    private readonly IBrowserRuntimeRegistry _runtimes;
    private readonly IChatGptBrowserAdapter _adapter;
    private readonly IDispatchLedger _ledger;
    private readonly WrongChatGuard _wrongChatGuard;
    private readonly GlobalBrowserSendGate _globalGate;
    private readonly IOwnershipProofService _ownership;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _dispatchGates = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _serializedSendGate = new(1, 1);
    private readonly TimeSpan _minimumSerializedSendInterval;
    private DateTimeOffset _lastSerializedSendAttemptAt = DateTimeOffset.MinValue;
'@
Replace-ExactlyOnce $dispatchPath $oldFields $newFields 'Add one provider-wide physical-send lane'

$oldCtor = @'
    public BrowserChatProvider(IBrowserRuntimeRegistry runtimes, IChatGptBrowserAdapter adapter, IDispatchLedger ledger, WrongChatGuard wrongChatGuard, GlobalBrowserSendGate globalGate, IOwnershipProofService ownership) { _runtimes = runtimes; _adapter = adapter; _ledger = ledger; _wrongChatGuard = wrongChatGuard; _globalGate = globalGate; _ownership = ownership ?? throw new ArgumentNullException(nameof(ownership)); }
'@
$newCtor = @'
    public BrowserChatProvider(
        IBrowserRuntimeRegistry runtimes,
        IChatGptBrowserAdapter adapter,
        IDispatchLedger ledger,
        WrongChatGuard wrongChatGuard,
        GlobalBrowserSendGate globalGate,
        IOwnershipProofService ownership,
        TimeSpan? minimumSerializedSendInterval = null)
    {
        _runtimes = runtimes;
        _adapter = adapter;
        _ledger = ledger;
        _wrongChatGuard = wrongChatGuard;
        _globalGate = globalGate;
        _ownership = ownership ?? throw new ArgumentNullException(nameof(ownership));
        _minimumSerializedSendInterval = minimumSerializedSendInterval is { } configured && configured > TimeSpan.Zero
            ? configured
            : TimeSpan.Zero;
    }
'@
Replace-ExactlyOnce $dispatchPath $oldCtor $newCtor 'Make serialized-send pacing configurable'

$oldSendStart = @'
    public async Task<BrowserDispatchResult> SendAsync(string runtimeId, BrowserDispatchRequest request, CancellationToken cancellationToken = default, Func<CancellationToken, Task>? beforeSubmit = null)
    {
        var gate = _globalGate.Snapshot;
        if (gate.IsPaused) return new(request.DispatchId, BrowserDispatchOutcome.NotSent, DispatchState.Prepared, "GLOBAL_SEND_PAUSED", new[] { gate.Reason ?? "global-pause" });
        var runtime = await _runtimes.GetAsync(runtimeId, cancellationToken).ConfigureAwait(false);
'@
$newSendStart = @'
    public async Task<BrowserDispatchResult> SendAsync(string runtimeId, BrowserDispatchRequest request, CancellationToken cancellationToken = default, Func<CancellationToken, Task>? beforeSubmit = null)
    {
        var gate = _globalGate.Snapshot;
        if (gate.IsPaused) return new(request.DispatchId, BrowserDispatchOutcome.NotSent, DispatchState.Prepared, "GLOBAL_SEND_PAUSED", new[] { gate.Reason ?? "global-pause" });

        // Manager, Manager repair/review, and every Worker share this one physical-send lane.
        // The lane covers semantic preflight, Fill, final authorization, and Enter, so two
        // ChatGPT conversations can never be physically driven in parallel by PCC Executive.
        await _serializedSendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            gate = _globalGate.Snapshot;
            if (gate.IsPaused) return new(request.DispatchId, BrowserDispatchOutcome.NotSent, DispatchState.Prepared, "GLOBAL_SEND_PAUSED", new[] { gate.Reason ?? "global-pause" });

            if (_minimumSerializedSendInterval > TimeSpan.Zero && _lastSerializedSendAttemptAt != DateTimeOffset.MinValue)
            {
                var eligibleAt = _lastSerializedSendAttemptAt + _minimumSerializedSendInterval;
                var delay = eligibleAt - DateTimeOffset.UtcNow;
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            _lastSerializedSendAttemptAt = DateTimeOffset.UtcNow;

            // A rate-limit/global-health pause may have been raised while this request waited.
            gate = _globalGate.Snapshot;
            if (gate.IsPaused) return new(request.DispatchId, BrowserDispatchOutcome.NotSent, DispatchState.Prepared, "GLOBAL_SEND_PAUSED", new[] { gate.Reason ?? "global-pause" });

            var runtime = await _runtimes.GetAsync(runtimeId, cancellationToken).ConfigureAwait(false);
'@
Replace-ExactlyOnce $dispatchPath $oldSendStart $newSendStart 'Serialize complete Browser send attempts and pace them'

$oldSendTail = @'
        finally
        {
            dispatchGate.Release();
            if (dispatchGate.CurrentCount == 1) _dispatchGates.TryRemove(request.DispatchId, out _);
        }
    }
}

public sealed class BrowserDispatchScheduler
'@
$newSendTail = @'
        finally
        {
            dispatchGate.Release();
            if (dispatchGate.CurrentCount == 1) _dispatchGates.TryRemove(request.DispatchId, out _);
        }
        }
        finally
        {
            _serializedSendGate.Release();
        }
    }
}

public sealed class BrowserDispatchScheduler
'@
Replace-ExactlyOnce $dispatchPath $oldSendTail $newSendTail 'Release provider-wide send lane on every outcome'

# 2. Reopen the exact durable provider conversation when a PCC-owned Chrome process is replaced.
$pagePolicy = Read-Normalized $pagePolicyPath
if ($pagePolicy.Contains('BuildLaunchTarget(', [StringComparison]::Ordinal)) {
    throw 'PATCH_CONTRACT_MISMATCH: BuildLaunchTarget already exists.'
}
$policyAnchor = '    public static bool TryGetConversationIdentity(string? value, out string identity)'
$policyIndex = $pagePolicy.IndexOf($policyAnchor, [StringComparison]::Ordinal)
if ($policyIndex -lt 0) { throw 'PATCH_CONTRACT_MISMATCH: TryGetConversationIdentity anchor not found.' }
$launchTargetMethod = @'
    public static string BuildLaunchTarget(string? expectedProviderConversationIdentity)
    {
        if (string.IsNullOrWhiteSpace(expectedProviderConversationIdentity) ||
            string.Equals(expectedProviderConversationIdentity, "NEW", StringComparison.OrdinalIgnoreCase))
            return "https://chatgpt.com/";

        if (TryGetConversationIdentity(expectedProviderConversationIdentity, out var fromUrl))
            return $"https://chatgpt.com/c/{Uri.EscapeDataString(fromUrl)}";

        var bareIdentity = expectedProviderConversationIdentity.Trim().Trim('/');
        return string.IsNullOrWhiteSpace(bareIdentity)
            ? "https://chatgpt.com/"
            : $"https://chatgpt.com/c/{Uri.EscapeDataString(bareIdentity)}";
    }

'@
$pagePolicy = $pagePolicy.Insert($policyIndex, $launchTargetMethod)
Set-Content -Path $pagePolicyPath -Value $pagePolicy -Encoding utf8 -NoNewline
Write-Host 'PATCHED: Stable provider identity maps to exact ChatGPT conversation URL'

$oldChrome = @'
        var chrome = _chromeLocator.LocateChrome();
'@
$newChrome = @'
        var chrome = _chromeLocator.LocateChrome();
        var launchTarget = ChatGptPageSelectionPolicy.BuildLaunchTarget(request.ProviderConversationIdentity);
'@
Replace-ExactlyOnce $hostPath $oldChrome $newChrome 'Resolve exact launch target before starting Chrome'

$oldRootArgument = @'
        startInfo.ArgumentList.Add("https://chatgpt.com/");
'@
$newRootArgument = @'
        startInfo.ArgumentList.Add(launchTarget);
'@
Replace-ExactlyOnce $hostPath $oldRootArgument $newRootArgument 'Do not force replacement Chrome onto NEW chat'

$oldLaunchSelection = @'
            var launchPages = context.Pages.Where(x => !x.IsClosed).ToArray();
            var launchPageIndex = ChatGptPageSelectionPolicy.SelectForLaunch(launchPages.Select(x => x.Url).ToArray());
'@
$newLaunchSelection = @'
            var launchPages = context.Pages.Where(x => !x.IsClosed).ToArray();
            var launchPageIndex = ChatGptPageSelectionPolicy.SelectForRecovery(
                launchPages.Select(x => x.Url).ToArray(),
                request.ProviderConversationIdentity);
'@
Replace-ExactlyOnce $hostPath $oldLaunchSelection $newLaunchSelection 'Select exact provider conversation during launch/replacement'

$oldFallbackGoto = @'
                await page.GotoAsync("https://chatgpt.com/", new PageGotoOptions
'@
$newFallbackGoto = @'
                await page.GotoAsync(launchTarget, new PageGotoOptions
'@
Replace-ExactlyOnce $hostPath $oldFallbackGoto $newFallbackGoto 'Navigate fallback page to exact durable conversation'

$oldLaunchContext = @'
            }
            var contextIdentity = Guid.NewGuid().ToString("N");
'@
$newLaunchContext = @'
            }
            await CloseOtherChatGptPagesAsync(context, page).ConfigureAwait(false);
            var contextIdentity = Guid.NewGuid().ToString("N");
'@
Replace-ExactlyOnce $hostPath $oldLaunchContext $newLaunchContext 'Close duplicate ChatGPT tabs after canonical launch page is selected'

$oldRecoveryBind = @'
        var page = recoveryPages[recoveryPageIndex];
        _connections[runtime.RuntimeId] = new Connection(process, browser, page, runtime.ContextIdentity ?? Guid.NewGuid().ToString("N"), runtime.ProfilePath);
'@
$newRecoveryBind = @'
        var page = recoveryPages[recoveryPageIndex];
        await CloseOtherChatGptPagesAsync(context, page).ConfigureAwait(false);
        _connections[runtime.RuntimeId] = new Connection(process, browser, page, runtime.ContextIdentity ?? Guid.NewGuid().ToString("N"), runtime.ProfilePath);
'@
Replace-ExactlyOnce $hostPath $oldRecoveryBind $newRecoveryBind 'Close duplicate ChatGPT tabs after recovery selects canonical page'

$host = Read-Normalized $hostPath
$helperAnchor = '    private async Task<IPlaywright> GetPlaywrightAsync(CancellationToken cancellationToken)'
$helperIndex = $host.IndexOf($helperAnchor, [StringComparison]::Ordinal)
if ($helperIndex -lt 0) { throw 'PATCH_CONTRACT_MISMATCH: GetPlaywrightAsync helper anchor not found.' }
if ($host.Contains('private static async Task CloseOtherChatGptPagesAsync', [StringComparison]::Ordinal)) {
    throw 'PATCH_CONTRACT_MISMATCH: duplicate-tab cleanup helper already exists.'
}
$closeHelper = @'
    private static async Task CloseOtherChatGptPagesAsync(IBrowserContext context, IPage selected)
    {
        var pages = context.Pages.Where(x => !x.IsClosed && !ReferenceEquals(x, selected)).ToArray();
        foreach (var candidate in pages)
        {
            if (!Uri.TryCreate(candidate.Url, UriKind.Absolute, out var uri)) continue;
            if (!string.Equals(uri.Host, "chatgpt.com", StringComparison.OrdinalIgnoreCase) &&
                !uri.Host.EndsWith(".chatgpt.com", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                await candidate.CloseAsync().ConfigureAwait(false);
            }
            catch (PlaywrightException)
            {
                // A duplicate tab may already be closing. Never replace the selected canonical page.
            }
        }
    }

'@
$host = $host.Insert($helperIndex, $closeHelper)
Set-Content -Path $hostPath -Value $host -Encoding utf8 -NoNewline
Write-Host 'PATCHED: Each PCC-owned runtime keeps one canonical ChatGPT tab'

# 3. Wire conservative production pacing and stronger repeated-rate-limit cooldown.
$oldProviderWire = @'
            var browserProvider = new PCCExecutive.Browser.BrowserChatProvider(registry, adapter, store, new WrongChatGuard(), sendGate, ownership);
'@
$newProviderWire = @'
            var browserProvider = new PCCExecutive.Browser.BrowserChatProvider(
                registry,
                adapter,
                store,
                new WrongChatGuard(),
                sendGate,
                ownership,
                minimumSerializedSendInterval: TimeSpan.FromSeconds(Math.Max(15, settings.BaseDispatchIntervalSeconds)));
'@
Replace-ExactlyOnce $gatewayPath $oldProviderWire $newProviderWire 'Use at least 15 seconds between production Browser send attempts'

$oldCooldown = @'
                ? new ConservativeCooldownPolicy().GetCooldown(Math.Max(1, _runtimeHealthRetryCount))
'@
$newCooldown = @'
                ? new ConservativeCooldownPolicy(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(30)).GetCooldown(Math.Max(1, _runtimeHealthRetryCount))
'@
Replace-ExactlyOnce $gatewayPath $oldCooldown $newCooldown 'Back off rate limits at 2m/4m/8m... up to 30m'

# 4. Build-time tests for the two invariants introduced by V8.
$testContent = @'
using PCCExecutive.Browser;
using Xunit;

namespace PCCExecutive.Browser.Tests;

public sealed class SingleConversationSerialSendV8BuildTests
{
    [Theory]
    [InlineData(null, "https://chatgpt.com/")]
    [InlineData("NEW", "https://chatgpt.com/")]
    [InlineData("abc-123", "https://chatgpt.com/c/abc-123")]
    [InlineData("https://chatgpt.com/c/abc-123", "https://chatgpt.com/c/abc-123")]
    public void Launch_target_reuses_exact_existing_provider_conversation(string? providerIdentity, string expected)
        => Assert.Equal(expected, ChatGptPageSelectionPolicy.BuildLaunchTarget(providerIdentity));

    [Fact]
    public async Task Different_dispatches_never_physically_submit_in_parallel()
    {
        var registry = new InMemoryBrowserRuntimeRegistry();
        var left = Runtime("runtime-left", "agent-left", "task-left", "conversation-left");
        var right = Runtime("runtime-right", "agent-right", "task-right", "conversation-right");
        await registry.UpsertAsync(left);
        await registry.UpsertAsync(right);

        var adapter = new ConcurrentPhysicalAdapter();
        var provider = new BrowserChatProvider(
            registry,
            adapter,
            new InMemoryDispatchLedger(),
            new WrongChatGuard(),
            new GlobalBrowserSendGate(),
            new AlwaysOwned(),
            minimumSerializedSendInterval: TimeSpan.Zero);

        var sends = await Task.WhenAll(
            provider.SendAsync(left.RuntimeId, Request(left, "dispatch-left")),
            provider.SendAsync(right.RuntimeId, Request(right, "dispatch-right")));

        Assert.All(sends, x => Assert.Equal(BrowserDispatchOutcome.Submitted, x.Outcome));
        Assert.Equal(1, adapter.MaxConcurrentSubmissions);
        Assert.Equal(2, adapter.EnterCount);
    }

    private static BrowserDispatchRequest Request(BrowserRuntimeRecord runtime, string dispatchId) =>
        new(dispatchId, runtime.ProjectRunId, runtime.LogicalAgentId, runtime.TaskId!, runtime.ConversationIdentity!, runtime.ProviderConversationIdentity!, "serial-v8", null, runtime.WorkerSlotId);

    private static BrowserRuntimeRecord Runtime(string runtimeId, string agentId, string taskId, string conversationId) => new()
    {
        RuntimeId = runtimeId,
        ProjectRunId = "project-run-v8",
        LogicalAgentId = agentId,
        TaskId = taskId,
        ProcessId = 41008,
        ProcessStartIdentity = "pid:41008:start:v8",
        ContextIdentity = $"ctx-{runtimeId}",
        ProfilePath = Path.Combine(Path.GetTempPath(), "pcc-v8", runtimeId),
        CreatedByPcc = true,
        AdoptedExplicitly = false,
        ConversationIdentity = conversationId,
        ProviderConversationIdentity = $"https://chatgpt.com/c/{conversationId}",
        Visibility = BrowserVisibility.Hidden,
        State = BrowserSessionState.Hidden,
        LastHeartbeatAt = DateTimeOffset.UtcNow,
        LastActivityAt = DateTimeOffset.UtcNow,
        OwnershipNonce = $"nonce-{runtimeId}"
    };

    private static ChatGptSemanticSnapshot Healthy() => new(
        SemanticDetection<InputState>.Create(InputState.Ready, .99, "v8", "input:ready"),
        SemanticDetection<GenerationState>.Create(GenerationState.Idle, .99, "v8", "generation:idle"),
        SemanticDetection<AuthState>.Create(AuthState.Authenticated, .99, "v8", "auth:authenticated"),
        SemanticDetection<ConversationMatch>.Create(ConversationMatch.Match, .99, "v8", "conversation:match"),
        SemanticDetection<PageHealth>.Create(PageHealth.Healthy, .99, "v8", "health:healthy"),
        ResponseCompleteness.None,
        0,
        null,
        DateTimeOffset.UtcNow,
        "v8");

    private sealed class ConcurrentPhysicalAdapter : IPhysicalSubmitAuthorizationAdapter
    {
        private int _concurrent;
        private int _maxConcurrent;
        private int _enterCount;
        public string AdapterVersion => "v8";
        public int MaxConcurrentSubmissions => Volatile.Read(ref _maxConcurrent);
        public int EnterCount => Volatile.Read(ref _enterCount);

        public Task<ChatGptSemanticSnapshot> InspectAsync(BrowserRuntimeRecord runtime, BrowserDispatchExpectation expectation, CancellationToken cancellationToken = default)
            => Task.FromResult(Healthy());

        public Task<AdapterSubmissionResult> SubmitAsync(BrowserRuntimeRecord runtime, BrowserDispatchExpectation expectation, string prompt, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Direct SubmitAsync is not allowed by this regression test.");

        public async Task<AdapterSubmissionResult> SubmitAuthorizedAsync(
            BrowserRuntimeRecord runtime,
            BrowserDispatchExpectation expectation,
            string prompt,
            Func<CancellationToken, Task<PreEnterAuthorizationDecision>> authorizeBeforeEnter,
            CancellationToken cancellationToken = default)
        {
            var current = Interlocked.Increment(ref _concurrent);
            while (true)
            {
                var observed = Volatile.Read(ref _maxConcurrent);
                if (current <= observed || Interlocked.CompareExchange(ref _maxConcurrent, current, observed) == observed) break;
            }
            try
            {
                var authorization = await authorizeBeforeEnter(cancellationToken).ConfigureAwait(false);
                if (!authorization.Authorized)
                    return new(false, false, false, "PRE_ENTER_AUTHORIZATION_DENIED", authorization.Evidence.Prepend(authorization.Reason).ToArray());
                Interlocked.Increment(ref _enterCount);
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                return new(true, true, false, "SUBMISSION_PROVEN", authorization.Evidence.Append("v8:serialized-enter").ToArray());
            }
            finally
            {
                Interlocked.Decrement(ref _concurrent);
            }
        }
    }

    private sealed class AlwaysOwned : IOwnershipProofService
    {
        public Task<OwnershipProof> ProveAsync(BrowserRuntimeRecord runtime, CancellationToken cancellationToken = default)
            => Task.FromResult(OwnershipProof.Proven(runtime.RuntimeId));
    }
}
'@
Set-Content -Path $testPath -Value $testContent -Encoding utf8 -NoNewline
Write-Host 'PATCHED: Added V8 single-conversation/serialized-send regression tests'

$finalDispatch = Read-Normalized $dispatchPath
$finalHost = Read-Normalized $hostPath
$finalPolicy = Read-Normalized $pagePolicyPath
$finalGateway = Read-Normalized $gatewayPath
if (-not $finalDispatch.Contains('_serializedSendGate', [StringComparison]::Ordinal)) { throw 'V8_ASSERTION_FAILED: serialized send gate missing.' }
if (-not $finalDispatch.Contains('_minimumSerializedSendInterval', [StringComparison]::Ordinal)) { throw 'V8_ASSERTION_FAILED: serialized pacing missing.' }
if (-not $finalHost.Contains('startInfo.ArgumentList.Add(launchTarget);', [StringComparison]::Ordinal)) { throw 'V8_ASSERTION_FAILED: launch target not wired.' }
if (-not $finalHost.Contains('private static async Task CloseOtherChatGptPagesAsync', [StringComparison]::Ordinal)) { throw 'V8_ASSERTION_FAILED: duplicate ChatGPT tab cleanup missing.' }
if (-not $finalPolicy.Contains('BuildLaunchTarget', [StringComparison]::Ordinal)) { throw 'V8_ASSERTION_FAILED: exact conversation launch resolver missing.' }
if (-not $finalGateway.Contains('Math.Max(15, settings.BaseDispatchIntervalSeconds)', [StringComparison]::Ordinal)) { throw 'V8_ASSERTION_FAILED: production send pacing missing.' }
if (-not $finalGateway.Contains('TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(30)', [StringComparison]::Ordinal)) { throw 'V8_ASSERTION_FAILED: conservative rate-limit cooldown missing.' }
Write-Host 'SINGLE_CONVERSATION_SERIAL_SEND_V8B_FIX_APPLIED'
