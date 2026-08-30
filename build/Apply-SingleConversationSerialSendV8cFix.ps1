[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

function Read-Normalized([string]$Path) {
    (Get-Content $Path -Raw).Replace("`r`n", "`n")
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
$gatewayPath = Join-Path $repoRoot 'src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs'
$testPath = Join-Path $repoRoot 'tests/PCCExecutive.Browser.Tests/SingleConversationSerialSendV8BuildTests.cs'

# -----------------------------------------------------------------------------
# A. One global physical-submit lane for the Browser provider.
#    Existing per-dispatch semaphores prevent duplicate retries of one dispatch,
#    but they do not stop two DIFFERENT dispatch IDs (Manager/repair/review/Workers)
#    from reaching ChatGPT at the same time. V8 adds a provider-wide lane.
# -----------------------------------------------------------------------------
$oldFields = '    private readonly IBrowserRuntimeRegistry _runtimes; private readonly IChatGptBrowserAdapter _adapter; private readonly IDispatchLedger _ledger; private readonly WrongChatGuard _wrongChatGuard; private readonly GlobalBrowserSendGate _globalGate; private readonly IOwnershipProofService _ownership; private readonly ConcurrentDictionary<string, SemaphoreSlim> _dispatchGates = new(StringComparer.Ordinal);'
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
Replace-ExactlyOnce $dispatchPath $oldFields $newFields 'Add provider-wide serialized physical-submit lane'

$oldCtor = '    public BrowserChatProvider(IBrowserRuntimeRegistry runtimes, IChatGptBrowserAdapter adapter, IDispatchLedger ledger, WrongChatGuard wrongChatGuard, GlobalBrowserSendGate globalGate, IOwnershipProofService ownership) { _runtimes = runtimes; _adapter = adapter; _ledger = ledger; _wrongChatGuard = wrongChatGuard; _globalGate = globalGate; _ownership = ownership ?? throw new ArgumentNullException(nameof(ownership)); }'
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

$oldRuntimeLine = '        var runtime = await _runtimes.GetAsync(runtimeId, cancellationToken).ConfigureAwait(false);'
$newRuntimeBlock = @'
        await _serializedSendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            gate = _globalGate.Snapshot;
            if (gate.IsPaused)
                return new(request.DispatchId, BrowserDispatchOutcome.NotSent, DispatchState.Prepared, "GLOBAL_SEND_PAUSED", new[] { gate.Reason ?? "global-pause" });

            if (_minimumSerializedSendInterval > TimeSpan.Zero && _lastSerializedSendAttemptAt != DateTimeOffset.MinValue)
            {
                var eligibleAt = _lastSerializedSendAttemptAt + _minimumSerializedSendInterval;
                var delay = eligibleAt - DateTimeOffset.UtcNow;
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            _lastSerializedSendAttemptAt = DateTimeOffset.UtcNow;

            // Re-check after waiting because another semantic probe may have raised a
            // global rate-limit pause while this dispatch was queued behind the lane.
            gate = _globalGate.Snapshot;
            if (gate.IsPaused)
                return new(request.DispatchId, BrowserDispatchOutcome.NotSent, DispatchState.Prepared, "GLOBAL_SEND_PAUSED", new[] { gate.Reason ?? "global-pause" });

            var runtime = await _runtimes.GetAsync(runtimeId, cancellationToken).ConfigureAwait(false);
'@
Replace-ExactlyOnce $dispatchPath $oldRuntimeLine $newRuntimeBlock 'Serialize every Manager/Worker Browser send before runtime preflight'

$oldMethodTail = @'
        finally
        {
            dispatchGate.Release();
            if (dispatchGate.CurrentCount == 1) _dispatchGates.TryRemove(request.DispatchId, out _);
        }
    }
}

public sealed class BrowserDispatchScheduler
'@
$newMethodTail = @'
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
Replace-ExactlyOnce $dispatchPath $oldMethodTail $newMethodTail 'Always release provider-wide physical-submit lane'

# -----------------------------------------------------------------------------
# B. Production pacing: one message-driving attempt at a time, >= 15 seconds apart.
#    Unit tests keep the constructor default at zero to remain deterministic/fast.
# -----------------------------------------------------------------------------
$oldProviderWire = '            var browserProvider = new PCCExecutive.Browser.BrowserChatProvider(registry, adapter, store, new WrongChatGuard(), sendGate, ownership);'
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
Replace-ExactlyOnce $gatewayPath $oldProviderWire $newProviderWire 'Enforce at least 15 seconds between production Browser send attempts'

# Repeated account/provider rate limiting needs minutes, not another aggressive semantic retry.
$oldCooldown = '                ? new ConservativeCooldownPolicy().GetCooldown(Math.Max(1, _runtimeHealthRetryCount))'
$newCooldown = '                ? new ConservativeCooldownPolicy(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(30)).GetCooldown(Math.Max(1, _runtimeHealthRetryCount))'
Replace-ExactlyOnce $gatewayPath $oldCooldown $newCooldown 'Use 2m/4m/8m adaptive backoff after repeated rate limits'

# -----------------------------------------------------------------------------
# C. V6 already reopens the exact durable provider conversation after Chrome recovery.
#    V8 additionally removes stale duplicate ChatGPT tabs from that PCC-owned browser
#    context, so recovery cannot leave several ChatGPT tabs racing for semantics.
# -----------------------------------------------------------------------------
$oldContextIdentity = '            var contextIdentity = Guid.NewGuid().ToString("N");'
$newContextIdentity = @'
            await CloseOtherChatGptPagesAsync(context, page).ConfigureAwait(false);
            var contextIdentity = Guid.NewGuid().ToString("N");
'@
Replace-ExactlyOnce $hostPath $oldContextIdentity $newContextIdentity 'Retain one canonical ChatGPT tab after PCC Chrome launch/replacement'

$oldRecoveryBind = @'
        var page = recoveryPages[recoveryPageIndex];
        _connections[runtime.RuntimeId] = new Connection(process, browser, page, runtime.ContextIdentity ?? Guid.NewGuid().ToString("N"), runtime.ProfilePath);
'@
$newRecoveryBind = @'
        var page = recoveryPages[recoveryPageIndex];
        await CloseOtherChatGptPagesAsync(context, page).ConfigureAwait(false);
        _connections[runtime.RuntimeId] = new Connection(process, browser, page, runtime.ContextIdentity ?? Guid.NewGuid().ToString("N"), runtime.ProfilePath);
'@
Replace-ExactlyOnce $hostPath $oldRecoveryBind $newRecoveryBind 'Retain one canonical ChatGPT tab after CDP/Playwright recovery'

$host = Read-Normalized $hostPath
$helperAnchor = '    private async Task<IPlaywright> GetPlaywrightAsync(CancellationToken cancellationToken)'
$helperIndex = $host.IndexOf($helperAnchor, [StringComparison]::Ordinal)
if ($helperIndex -lt 0) { throw 'PATCH_CONTRACT_MISMATCH: GetPlaywrightAsync anchor missing.' }
if ($host.Contains('private static async Task CloseOtherChatGptPagesAsync', [StringComparison]::Ordinal)) {
    throw 'PATCH_CONTRACT_MISMATCH: duplicate-tab cleanup helper already exists.'
}
$helper = @'
    private static async Task CloseOtherChatGptPagesAsync(IBrowserContext context, IPage selected)
    {
        var duplicates = context.Pages
            .Where(x => !x.IsClosed && !ReferenceEquals(x, selected))
            .ToArray();

        foreach (var candidate in duplicates)
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
                // Concurrently closing stale tabs are non-authoritative. The selected
                // canonical conversation remains the only page used for PCC semantics.
            }
        }
    }

'@
$host = $host.Insert($helperIndex, $helper)
Set-Content -Path $hostPath -Value $host -Encoding utf8 -NoNewline
Write-Host 'PATCHED: Close stale duplicate ChatGPT tabs in each PCC-owned browser context'

# -----------------------------------------------------------------------------
# D. Regression test: two DIFFERENT dispatch IDs submitted concurrently through the
#    same BrowserChatProvider must still produce max physical concurrency == 1.
# -----------------------------------------------------------------------------
$testContent = @'
using PCCExecutive.Browser;
using Xunit;

namespace PCCExecutive.Browser.Tests;

public sealed class SingleConversationSerialSendV8BuildTests
{
    [Fact]
    public async Task Different_dispatches_never_physically_submit_in_parallel()
    {
        var registry = new InMemoryBrowserRuntimeRegistry();
        var left = Runtime("runtime-left", "agent-left", "1", "task-left", "conversation-left");
        var right = Runtime("runtime-right", "agent-right", "2", "task-right", "conversation-right");
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

        var results = await Task.WhenAll(
            provider.SendAsync(left.RuntimeId, Request(left, "dispatch-left")),
            provider.SendAsync(right.RuntimeId, Request(right, "dispatch-right")));

        Assert.All(results, x => Assert.Equal(BrowserDispatchOutcome.Submitted, x.Outcome));
        Assert.Equal(1, adapter.MaxConcurrentSubmissions);
        Assert.Equal(2, adapter.EnterCount);
    }

    private static BrowserDispatchRequest Request(BrowserRuntimeRecord runtime, string dispatchId) =>
        new(dispatchId, runtime.ProjectRunId, runtime.LogicalAgentId, runtime.TaskId!, runtime.ConversationIdentity!, runtime.ProviderConversationIdentity!, "serial-send-v8", null, runtime.WorkerSlotId);

    private static BrowserRuntimeRecord Runtime(string runtimeId, string logicalAgentId, string workerSlotId, string taskId, string conversationId) =>
        new()
        {
            RuntimeId = runtimeId,
            ProjectRunId = "project-run-v8",
            LogicalAgentId = logicalAgentId,
            WorkerSlotId = workerSlotId,
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
        SemanticDetection<InputState>.Create(InputState.Ready, .95, "serial-v8", "input:ready"),
        SemanticDetection<GenerationState>.Create(GenerationState.Idle, .95, "serial-v8", "generation:idle"),
        SemanticDetection<AuthState>.Create(AuthState.Authenticated, .95, "serial-v8", "auth:authenticated"),
        SemanticDetection<ConversationMatch>.Create(ConversationMatch.Match, .95, "serial-v8", "conversation:match"),
        SemanticDetection<PageHealth>.Create(PageHealth.Healthy, .95, "serial-v8", "health:healthy"),
        ResponseCompleteness.None,
        0,
        null,
        DateTimeOffset.UtcNow,
        "serial-v8");

    private sealed class ConcurrentPhysicalAdapter : IPhysicalSubmitAuthorizationAdapter
    {
        private int _concurrent;
        private int _maxConcurrent;
        private int _enterCount;

        public string AdapterVersion => "serial-v8";
        public int MaxConcurrentSubmissions => Volatile.Read(ref _maxConcurrent);
        public int EnterCount => Volatile.Read(ref _enterCount);

        public Task<ChatGptSemanticSnapshot> InspectAsync(BrowserRuntimeRecord runtime, BrowserDispatchExpectation expectation, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Healthy());
        }

        public Task<AdapterSubmissionResult> SubmitAsync(BrowserRuntimeRecord runtime, BrowserDispatchExpectation expectation, string prompt, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Direct SubmitAsync is not authorized by this regression test.");

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
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(OwnershipProof.Proven(runtime.RuntimeId));
        }
    }
}
'@
Set-Content -Path $testPath -Value $testContent -Encoding utf8 -NoNewline
Write-Host 'PATCHED: Added different-dispatch physical-serialization regression test'

# Build-time transformation assertions.
$finalDispatch = Read-Normalized $dispatchPath
$finalHost = Read-Normalized $hostPath
$finalGateway = Read-Normalized $gatewayPath
if (-not $finalDispatch.Contains('_serializedSendGate', [StringComparison]::Ordinal)) { throw 'V8_ASSERTION_FAILED: serialized lane missing.' }
if (-not $finalDispatch.Contains('_minimumSerializedSendInterval', [StringComparison]::Ordinal)) { throw 'V8_ASSERTION_FAILED: pacing missing.' }
if (-not $finalDispatch.Contains('_serializedSendGate.Release()', [StringComparison]::Ordinal)) { throw 'V8_ASSERTION_FAILED: serialized lane release missing.' }
if (-not $finalHost.Contains('private static async Task CloseOtherChatGptPagesAsync', [StringComparison]::Ordinal)) { throw 'V8_ASSERTION_FAILED: duplicate-tab cleanup missing.' }
if (-not $finalGateway.Contains('Math.Max(15, settings.BaseDispatchIntervalSeconds)', [StringComparison]::Ordinal)) { throw 'V8_ASSERTION_FAILED: production 15-second floor missing.' }
if (-not $finalGateway.Contains('TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(30)', [StringComparison]::Ordinal)) { throw 'V8_ASSERTION_FAILED: conservative rate-limit backoff missing.' }
Write-Host 'SINGLE_CONVERSATION_SERIAL_SEND_V8C_FIX_APPLIED'
