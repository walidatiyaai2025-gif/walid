[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

function Read-Normalized([string]$path) {
    (Get-Content $path -Raw).Replace("`r`n", "`n")
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
    if ($count -ne 1) { throw "PATCH_CONTRACT_MISMATCH: $Description expected 1 literal match, found $count in $Path" }
    Set-Content -Path $Path -Value $text.Replace($Old, $New) -Encoding utf8 -NoNewline
    Write-Host "PATCHED: $Description"
}

$dispatchPath = Join-Path $repoRoot 'src/PCCExecutive.Browser/DispatchAndResilience.cs'
$hostPath = Join-Path $repoRoot 'src/PCCExecutive.Browser/PlaywrightChromeRuntimeHost.cs'
$pagePolicyPath = Join-Path $repoRoot 'src/PCCExecutive.Browser/ChatGptPageSelectionPolicy.cs'
$gatewayPath = Join-Path $repoRoot 'src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs'
$testPath = Join-Path $repoRoot 'tests/PCCExecutive.Browser.Tests/SingleConversationSerialSendV8BuildTests.cs'

# -----------------------------------------------------------------------------
# 1) One canonical physical-send lane for the entire BrowserChatProvider.
#    Manager repair, Manager review, and every Worker all pass through the same
#    semaphore. Production also enforces a minimum interval between attempts.
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
Replace-ExactlyOnce $dispatchPath $oldFields $newFields 'Add one provider-wide serialized send lane'

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
Replace-ExactlyOnce $dispatchPath $oldCtor $newCtor 'Make production send pacing configurable without slowing deterministic tests'

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

        // A single BrowserChatProvider owns Manager + all Worker physical submissions.
        // Serialize the complete preflight/fill/final-authorization/Enter boundary so two
        // chats can never be filled or submitted concurrently from this PCC process.
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

            // Re-check the global gate after pacing; a rate limit may have been observed by
            // another semantic probe while this request was waiting for the canonical lane.
            gate = _globalGate.Snapshot;
            if (gate.IsPaused) return new(request.DispatchId, BrowserDispatchOutcome.NotSent, DispatchState.Prepared, "GLOBAL_SEND_PAUSED", new[] { gate.Reason ?? "global-pause" });

            var runtime = await _runtimes.GetAsync(runtimeId, cancellationToken).ConfigureAwait(false);
'@
Replace-ExactlyOnce $dispatchPath $oldSendStart $newSendStart 'Serialize all Manager/Worker send attempts and enforce inter-send pacing'

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
Replace-ExactlyOnce $dispatchPath $oldSendTail $newSendTail 'Release the provider-wide serialized send lane on every outcome'

# -----------------------------------------------------------------------------
# 2) Browser replacement must reopen the existing provider conversation, not /
#    (NEW). V7 recovery was preserving ProviderConversationIdentity in the
#    registry but LaunchAsync always opened https://chatgpt.com/, which could
#    create repeated fresh tabs during recovery and keep WrongChatGuard failing.
# -----------------------------------------------------------------------------
$pagePolicy = Read-Normalized $pagePolicyPath
if ($pagePolicy.Contains('BuildLaunchTarget(', [StringComparison]::Ordinal)) {
    throw 'PATCH_CONTRACT_MISMATCH: BuildLaunchTarget is already present.'
}
$policyAnchor = '    public static bool TryGetConversationIdentity(string? value, out string identity)'
$policyIndex = $pagePolicy.IndexOf($policyAnchor, [StringComparison]::Ordinal)
if ($policyIndex -lt 0) { throw 'PATCH_CONTRACT_MISMATCH: TryGetConversationIdentity anchor missing.' }
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
Write-Host 'PATCHED: Stable provider conversation identities now resolve to an exact ChatGPT launch URL'

Replace-ExactlyOnce $hostPath `
    '        var chrome = _chromeLocator.LocateChrome();' `
    "        var chrome = _chromeLocator.LocateChrome();`n        var launchTarget = ChatGptPageSelectionPolicy.BuildLaunchTarget(request.ProviderConversationIdentity);" `
    'Resolve the exact existing provider conversation before launching Chrome'

Replace-ExactlyOnce $hostPath `
    '        startInfo.ArgumentList.Add("https://chatgpt.com/");' `
    '        startInfo.ArgumentList.Add(launchTarget);' `
    'Launch replacement Chrome directly into the bound conversation instead of NEW'

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
Replace-ExactlyOnce $hostPath $oldLaunchSelection $newLaunchSelection 'Select the exact bound conversation during process launch/replacement'

Replace-ExactlyOnce $hostPath `
    '                await page.GotoAsync("https://chatgpt.com/", new PageGotoOptions' `
    '                await page.GotoAsync(launchTarget, new PageGotoOptions' `
    'Navigate fallback page to the exact bound conversation'

Replace-ExactlyOnce $hostPath `
    "            }`n            var contextIdentity = Guid.NewGuid().ToString(\"N\");" `
    "            }`n            await CloseOtherChatGptPagesAsync(context, page).ConfigureAwait(false);`n            var contextIdentity = Guid.NewGuid().ToString(\"N\");" `
    'Close duplicate ChatGPT tabs after selecting the canonical launch page'

Replace-ExactlyOnce $hostPath `
    "        var page = recoveryPages[recoveryPageIndex];`n        _connections[runtime.RuntimeId] = new Connection(process, browser, page, runtime.ContextIdentity ?? Guid.NewGuid().ToString(\"N\"), runtime.ProfilePath);" `
    "        var page = recoveryPages[recoveryPageIndex];`n        await CloseOtherChatGptPagesAsync(context, page).ConfigureAwait(false);`n        _connections[runtime.RuntimeId] = new Connection(process, browser, page, runtime.ContextIdentity ?? Guid.NewGuid().ToString(\"N\"), runtime.ProfilePath);" `
    'Close duplicate ChatGPT tabs after recovery selects the exact provider conversation'

$host = Read-Normalized $hostPath
$helperAnchor = '    private async Task<IPlaywright> GetPlaywrightAsync(CancellationToken cancellationToken)'
$helperIndex = $host.IndexOf($helperAnchor, [StringComparison]::Ordinal)
if ($helperIndex -lt 0) { throw 'PATCH_CONTRACT_MISMATCH: GetPlaywrightAsync anchor missing.' }
if ($host.Contains('CloseOtherChatGptPagesAsync(', [StringComparison]::Ordinal) -and
    ([regex]::Matches($host, 'CloseOtherChatGptPagesAsync\(')).Count -gt 2) {
    throw 'PATCH_CONTRACT_MISMATCH: duplicate ChatGPT page cleanup helper already exists.'
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
                // The selected canonical page remains authoritative; a concurrently closing
                // duplicate tab is harmless and must not trigger another browser replacement.
            }
        }
    }

'@
$host = $host.Insert($helperIndex, $closeHelper)
Set-Content -Path $hostPath -Value $host -Encoding utf8 -NoNewline
Write-Host 'PATCHED: PCC-owned Manager/Worker profiles retain one canonical ChatGPT tab'

# -----------------------------------------------------------------------------
# 3) Production pacing and conservative repeated rate-limit cooldown.
#    Unit tests keep the optional interval at zero; the real app uses >=15 sec.
# -----------------------------------------------------------------------------
Replace-ExactlyOnce $gatewayPath `
    '            var browserProvider = new PCCExecutive.Browser.BrowserChatProvider(registry, adapter, store, new WrongChatGuard(), sendGate, ownership);' `
    '            var browserProvider = new PCCExecutive.Browser.BrowserChatProvider(registry, adapter, store, new WrongChatGuard(), sendGate, ownership, minimumSerializedSendInterval: TimeSpan.FromSeconds(Math.Max(15, settings.BaseDispatchIntervalSeconds)));' `
    'Enforce at least 15 seconds between production Browser send attempts'

Replace-ExactlyOnce $gatewayPath `
    '                ? new ConservativeCooldownPolicy().GetCooldown(Math.Max(1, _runtimeHealthRetryCount))' `
    '                ? new ConservativeCooldownPolicy(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(30)).GetCooldown(Math.Max(1, _runtimeHealthRetryCount))' `
    'Use 2m/4m/8m adaptive cooldown instead of rapid retries after a provider rate limit'

# -----------------------------------------------------------------------------
# 4) Build-time regression tests. They exist only in the V8 exact-head workflow,
#    so older general workflows are not made dependent on a build-time patch.
# -----------------------------------------------------------------------------
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
    public void Launch_target_reuses_the_exact_existing_provider_conversation(string? providerIdentity, string expected)
        => Assert.Equal(expected, ChatGptPageSelectionPolicy.BuildLaunchTarget(providerIdentity));

    [Fact]
    public async Task Different_dispatches_cannot_physically_submit_concurrently()
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

        var leftSend = provider.SendAsync(left.RuntimeId, Request(left, "dispatch-left"));
        var rightSend = provider.SendAsync(right.RuntimeId, Request(right, "dispatch-right"));
        var results = await Task.WhenAll(leftSend, rightSend);

        Assert.All(results, x => Assert.Equal(BrowserDispatchOutcome.Submitted, x.Outcome));
        Assert.Equal(1, adapter.MaxConcurrentSubmissions);
        Assert.Equal(2, adapter.EnterCount);
    }

    private static BrowserDispatchRequest Request(BrowserRuntimeRecord runtime, string dispatchId) =>
        new(dispatchId, runtime.ProjectRunId, runtime.LogicalAgentId, runtime.TaskId!, runtime.ConversationIdentity!, runtime.ProviderConversationIdentity!, "serial-send-v8", null, runtime.WorkerSlotId);

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
            => throw new InvalidOperationException("Direct submit must not be used in this test.");

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
Write-Host 'PATCHED: Added V8 build-time regression tests for exact conversation reuse + serialized physical send'

# Final transformation assertions.
$finalDispatch = Read-Normalized $dispatchPath
$finalHost = Read-Normalized $hostPath
$finalPolicy = Read-Normalized $pagePolicyPath
$finalGateway = Read-Normalized $gatewayPath
if (-not $finalDispatch.Contains('_serializedSendGate', [StringComparison]::Ordinal)) { throw 'V8_ASSERTION_FAILED: serialized send gate missing.' }
if (-not $finalDispatch.Contains('_minimumSerializedSendInterval', [StringComparison]::Ordinal)) { throw 'V8_ASSERTION_FAILED: send pacing missing.' }
if (-not $finalHost.Contains('startInfo.ArgumentList.Add(launchTarget);', [StringComparison]::Ordinal)) { throw 'V8_ASSERTION_FAILED: exact launch target not used.' }
if (-not $finalHost.Contains('CloseOtherChatGptPagesAsync(context, page)', [StringComparison]::Ordinal)) { throw 'V8_ASSERTION_FAILED: duplicate ChatGPT tab cleanup missing.' }
if (-not $finalPolicy.Contains('BuildLaunchTarget', [StringComparison]::Ordinal)) { throw 'V8_ASSERTION_FAILED: launch target resolver missing.' }
if (-not $finalGateway.Contains('Math.Max(15, settings.BaseDispatchIntervalSeconds)', [StringComparison]::Ordinal)) { throw 'V8_ASSERTION_FAILED: production pacing not wired.' }
if (-not $finalGateway.Contains('TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(30)', [StringComparison]::Ordinal)) { throw 'V8_ASSERTION_FAILED: conservative rate-limit cooldown not wired.' }
Write-Host 'SINGLE_CONVERSATION_SERIAL_SEND_V8_FIX_APPLIED'
