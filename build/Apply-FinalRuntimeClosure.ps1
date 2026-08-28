$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$utf8 = [Text.UTF8Encoding]::new($false)

function Read-Text([string]$Path) {
    $full = Join-Path $root $Path
    if (-not (Test-Path $full)) { throw "Required file missing: $Path" }
    return [IO.File]::ReadAllText($full)
}
function Write-Text([string]$Path, [string]$Text) {
    [IO.File]::WriteAllText((Join-Path $root $Path), $Text, $utf8)
}
function Replace-Exact([string]$Path, [string]$Old, [string]$New, [string]$Marker) {
    $text = Read-Text $Path
    if ($text.Contains($Marker)) { return }
    if (-not $text.Contains($Old)) { throw "Patch anchor missing in ${Path}: $Marker" }
    Write-Text $Path ($text.Replace($Old, $New))
}
function Insert-Before([string]$Path, [string]$Anchor, [string]$Block, [string]$Marker) {
    $text = Read-Text $Path
    if ($text.Contains($Marker)) { return }
    if (-not $text.Contains($Anchor)) { throw "Insert anchor missing in ${Path}: $Marker" }
    Write-Text $Path ($text.Replace($Anchor, $Block + [Environment]::NewLine + [Environment]::NewLine + $Anchor))
}
function Require-Text([string]$Path, [string]$Needle) {
    $text = Read-Text $Path
    if (-not $text.Contains($Needle)) { throw "Required invariant missing from ${Path}: $Needle" }
    return $text
}

# 1. Acceptance correlation: worker requests carry the runtime's canonical slot; manager remains null.
$harnessPath = 'tests/PCCExecutive.Browser.Acceptance/AcceptanceHarness.cs'
$harness = Read-Text $harnessPath
if (-not $harness.Contains('prompt, null, runtime.WorkerSlotId);')) {
    $old = '            prompt);'
    if (-not $harness.Contains($old)) { throw 'Acceptance Request() anchor missing.' }
    $harness = $harness.Replace($old, '            prompt, null, runtime.WorkerSlotId);')
    Write-Text $harnessPath $harness
}

# 2. Uncertain-send reconciliation must never dereference missing/unknown evidence.
$resiliencePath = 'src/PCCExecutive.Browser/ResilienceHardening.cs'
$resilience = Read-Text $resiliencePath
$resilience = $resilience.Replace(
'    public async Task<SendReconciliationResult> ReconcileAsync(string runtimeId, DispatchLedgerEntry dispatch, CancellationToken cancellationToken = default)',
'    public async Task<SendReconciliationResult> ReconcileAsync(string runtimeId, DispatchLedgerEntry? dispatch, CancellationToken cancellationToken = default)')
if (-not $resilience.Contains('DISPATCH_EVIDENCE_MISSING_NO_AUTOMATIC_RESEND')) {
    $anchor = '        if (dispatch.State != DispatchState.SubmittedUnknown)'
    $replacement = @'
        if (dispatch is null)
            return new(SendReconciliationState.CannotDetermine, RetrySafety.NotSafe, "DISPATCH_EVIDENCE_MISSING_NO_AUTOMATIC_RESEND", Array.Empty<string>());
        if (dispatch.State != DispatchState.SubmittedUnknown)
'@.TrimEnd()
    if (-not $resilience.Contains($anchor)) { throw 'Uncertain-send null guard anchor missing.' }
    $resilience = $resilience.Replace($anchor, $replacement)
}
if (-not $resilience.Contains('PROBE_RETURNED_NULL_NO_AUTOMATIC_RESEND')) {
    $oldEvidence = '        var evidence = await _probe.InspectDispatchAsync(runtimeId, dispatch.DispatchId, dispatch.ContentHash, cancellationToken).ConfigureAwait(false);'
    $newEvidence = @'
        ConversationDispatchEvidence? evidence;
        try
        {
            evidence = await _probe.InspectDispatchAsync(runtimeId, dispatch.DispatchId, dispatch.ContentHash, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return new(SendReconciliationState.CannotDetermine, RetrySafety.NotSafe, "PROBE_FAILED_NO_AUTOMATIC_RESEND", new[] { $"probe-error:{ex.GetType().Name}" });
        }
        if (evidence is null)
            return new(SendReconciliationState.CannotDetermine, RetrySafety.NotSafe, "PROBE_RETURNED_NULL_NO_AUTOMATIC_RESEND", Array.Empty<string>());
        var semanticEvidence = evidence.Evidence ?? Array.Empty<string>();
'@.TrimEnd()
    if (-not $resilience.Contains($oldEvidence)) { throw 'Uncertain-send evidence anchor missing.' }
    $resilience = $resilience.Replace($oldEvidence, $newEvidence)
    $resilience = $resilience.Replace('"RESPONSE_PRESENT_NO_RETRY", evidence.Evidence);', '"RESPONSE_PRESENT_NO_RETRY", semanticEvidence);')
    $resilience = $resilience.Replace('"GENERATION_IN_PROGRESS_NO_RETRY", evidence.Evidence);', '"GENERATION_IN_PROGRESS_NO_RETRY", semanticEvidence);')
    $resilience = $resilience.Replace('"MESSAGE_PRESENT_NO_RETRY", evidence.Evidence);', '"MESSAGE_PRESENT_NO_RETRY", semanticEvidence);')
    $resilience = $resilience.Replace('"MESSAGE_ABSENCE_PROVEN_SAFE_RETRY", evidence.Evidence);', '"MESSAGE_ABSENCE_PROVEN_SAFE_RETRY", semanticEvidence);')
    $resilience = $resilience.Replace('"CANNOT_DETERMINE_NO_AUTOMATIC_RESEND", evidence.Evidence);', '"CANNOT_DETERMINE_NO_AUTOMATIC_RESEND", semanticEvidence);')
}
Write-Text $resiliencePath $resilience

# 3. Physical Enter authorization contract.
$contractsPath = 'src/PCCExecutive.Browser/BrowserContracts.cs'
$contracts = Read-Text $contractsPath
if (-not $contracts.Contains('IPhysicalSubmitAuthorizationAdapter')) {
    $anchor = 'public interface IDispatchLedger'
    $block = @'
public sealed record PreEnterAuthorizationDecision(bool Authorized, string Reason, IReadOnlyList<string> Evidence);

public interface IPhysicalSubmitAuthorizationAdapter : IChatGptBrowserAdapter
{
    Task<AdapterSubmissionResult> SubmitAuthorizedAsync(
        BrowserRuntimeRecord runtime,
        BrowserDispatchExpectation expectation,
        string prompt,
        Func<CancellationToken, Task<PreEnterAuthorizationDecision>> authorizeBeforeEnter,
        CancellationToken cancellationToken = default);
}
'@.TrimEnd()
    if (-not $contracts.Contains($anchor)) { throw 'Physical-submit contract anchor missing.' }
    $contracts = $contracts.Replace($anchor, $block + [Environment]::NewLine + [Environment]::NewLine + $anchor)
    Write-Text $contractsPath $contracts
}

# 4. Provider re-reads the runtime and proves every correlation plus ownership after Fill.
$dispatchPath = 'src/PCCExecutive.Browser/DispatchAndResilience.cs'
$dispatch = Read-Text $dispatchPath
if (-not $dispatch.Contains('public static class FinalPreEnterAuthorization')) {
    $anchor = 'public sealed class BrowserChatProvider'
    $block = @'
public static class FinalPreEnterAuthorization
{
    public static async Task<PreEnterAuthorizationDecision> AuthorizeAsync(
        IBrowserRuntimeRegistry runtimes,
        IOwnershipProofService ownership,
        string runtimeId,
        BrowserDispatchExpectation expected,
        CancellationToken cancellationToken = default)
    {
        var current = await runtimes.GetAsync(runtimeId, cancellationToken).ConfigureAwait(false);
        if (current is null) return Deny("FINAL_RUNTIME_NOT_FOUND");
        if (!StringComparer.Ordinal.Equals(current.ProjectRunId, expected.ProjectRunId)) return Deny("FINAL_PROJECT_RUN_MISMATCH");
        if (!StringComparer.Ordinal.Equals(current.LogicalAgentId, expected.LogicalAgentId)) return Deny("FINAL_LOGICAL_AGENT_MISMATCH");
        if (!StringComparer.Ordinal.Equals(current.WorkerSlotId, expected.WorkerSlotId)) return Deny("FINAL_WORKER_SLOT_MISMATCH");
        if (!StringComparer.Ordinal.Equals(current.TaskId, expected.TaskId)) return Deny("FINAL_TASK_MISMATCH");
        if (!StringComparer.Ordinal.Equals(current.ConversationIdentity, expected.ConversationIdentity)) return Deny("FINAL_CONVERSATION_MISMATCH");
        if (!StringComparer.OrdinalIgnoreCase.Equals(current.ProviderConversationIdentity, expected.ProviderConversationIdentity)) return Deny("FINAL_PROVIDER_CONVERSATION_MISMATCH");
        var proof = await ownership.ProveAsync(current, cancellationToken).ConfigureAwait(false);
        if (!proof.IsProven) return new(false, "FINAL_PCC_OWNERSHIP_NOT_PROVEN", new[] { proof.Reason });
        return new(true, "FINAL_PRE_ENTER_AUTHORIZED", new[] { "project-run:match", "logical-agent:match", $"worker-slot:{expected.WorkerSlotId ?? "MANAGER"}", "task:match", "conversation:match", "provider-conversation:match", "ownership:proven" });

        PreEnterAuthorizationDecision Deny(string reason) => new(false, reason, new[] { $"runtime:{runtimeId}" });
    }
}
'@.TrimEnd()
    if (-not $dispatch.Contains($anchor)) { throw 'Final authorization insert anchor missing.' }
    $dispatch = $dispatch.Replace($anchor, $block + [Environment]::NewLine + [Environment]::NewLine + $anchor)
}
if (-not $dispatch.Contains('physicalAdapter.SubmitAuthorizedAsync')) {
    $oldSubmit = @'
        await _ledger.UpdateAsync(request.DispatchId, DispatchState.Submitting, cancellationToken: cancellationToken).ConfigureAwait(false);
        var submission = await _adapter.SubmitAsync(runtime, expected, request.Prompt, cancellationToken).ConfigureAwait(false);
'@.TrimEnd()
    $newSubmit = @'
        AdapterSubmissionResult submission;
        if (_adapter is IPhysicalSubmitAuthorizationAdapter physicalAdapter)
        {
            submission = await physicalAdapter.SubmitAuthorizedAsync(runtime, expected, request.Prompt, async ct =>
            {
                var authorization = await FinalPreEnterAuthorization.AuthorizeAsync(_runtimes, _ownership, runtimeId, expected, ct).ConfigureAwait(false);
                if (authorization.Authorized)
                    await _ledger.UpdateAsync(request.DispatchId, DispatchState.Submitting, "FINAL_PRE_ENTER_AUTHORIZED", ct).ConfigureAwait(false);
                return authorization;
            }, cancellationToken).ConfigureAwait(false);
            if (!submission.Triggered && string.Equals(submission.Reason, "PRE_ENTER_AUTHORIZATION_DENIED", StringComparison.Ordinal))
                return new(request.DispatchId, BrowserDispatchOutcome.NotSent, DispatchState.Prepared, submission.Reason, submission.Evidence);
        }
        else
        {
            await _ledger.UpdateAsync(request.DispatchId, DispatchState.Submitting, cancellationToken: cancellationToken).ConfigureAwait(false);
            submission = await _adapter.SubmitAsync(runtime, expected, request.Prompt, cancellationToken).ConfigureAwait(false);
        }
'@.TrimEnd()
    if (-not $dispatch.Contains($oldSubmit)) { throw 'BrowserChatProvider physical-submit anchor missing.' }
    $dispatch = $dispatch.Replace($oldSubmit, $newSubmit)
}
Write-Text $dispatchPath $dispatch

# 5. Playwright has no unguarded Enter path. Authorization runs after Fill and directly before Press.
$adapterPath = 'src/PCCExecutive.Browser/ChatGptBrowserAdapter.cs'
$chat = Read-Text $adapterPath
if (-not $chat.Contains('public sealed class PlaywrightChatGptBrowserAdapter : IChatGptBrowserAdapter, IPhysicalSubmitAuthorizationAdapter')) {`r`n    $chat = $chat.Replace('public sealed class PlaywrightChatGptBrowserAdapter : IChatGptBrowserAdapter', 'public sealed class PlaywrightChatGptBrowserAdapter : IChatGptBrowserAdapter, IPhysicalSubmitAuthorizationAdapter')`r`n}
if (-not $chat.Contains('PRE_ENTER_AUTHORIZATION_REQUIRED')) {
    $oldSignature = '    public async Task<AdapterSubmissionResult> SubmitAsync(BrowserRuntimeRecord runtime, BrowserDispatchExpectation expectation, string prompt, CancellationToken cancellationToken = default)'
    $newSignature = @'
    public Task<AdapterSubmissionResult> SubmitAsync(BrowserRuntimeRecord runtime, BrowserDispatchExpectation expectation, string prompt, CancellationToken cancellationToken = default) =>
        Task.FromResult(new AdapterSubmissionResult(false, false, false, "PRE_ENTER_AUTHORIZATION_REQUIRED", new[] { "submission:not-triggered", "physical-enter:fence-required" }));

    public async Task<AdapterSubmissionResult> SubmitAuthorizedAsync(
        BrowserRuntimeRecord runtime,
        BrowserDispatchExpectation expectation,
        string prompt,
        Func<CancellationToken, Task<PreEnterAuthorizationDecision>> authorizeBeforeEnter,
        CancellationToken cancellationToken = default)
'@.TrimEnd()
    if (-not $chat.Contains($oldSignature)) { throw 'Playwright SubmitAsync signature anchor missing.' }
    $chat = $chat.Replace($oldSignature, $newSignature)
}
if (-not $chat.Contains('var finalAuthorization = await authorizeBeforeEnter')) {
    $oldEnter = @'
            await composer.FillAsync(prompt).ConfigureAwait(false);
            triggered = true;
            await composer.PressAsync("Enter").ConfigureAwait(false);
'@.TrimEnd()
    $newEnter = @'
            await composer.FillAsync(prompt).ConfigureAwait(false);
            var finalAuthorization = await authorizeBeforeEnter(cancellationToken).ConfigureAwait(false);
            if (!finalAuthorization.Authorized)
                return new(false, false, false, "PRE_ENTER_AUTHORIZATION_DENIED", finalAuthorization.Evidence.Prepend(finalAuthorization.Reason).ToArray());
            triggered = true;
            await composer.PressAsync("Enter").ConfigureAwait(false);
'@.TrimEnd()
    if (-not $chat.Contains($oldEnter)) { throw 'Playwright Fill/Enter anchor missing.' }
    $chat = $chat.Replace($oldEnter, $newEnter)
}
Write-Text $adapterPath $chat

# 6. Acceptance-specific negative correlation and null semantic evidence tests.
$acceptanceTestsPath = 'tests/PCCExecutive.Browser.Acceptance/DeterministicBrowserAcceptanceTests.cs'
$tests = Read-Text $acceptanceTestsPath
if (-not $tests.Contains('Worker1_request_targeting_worker2_slot_is_blocked_before_submit')) {
    $anchor = '    [Fact]' + [Environment]::NewLine + '    public void Slow_worker_is_not_stuck_and_other_worker_can_remain_ready()'
    $block = @'
    [Fact]
    public async Task Worker1_request_targeting_worker2_slot_is_blocked_before_submit()
    {
        var harness = new ControlledBrowserAcceptanceHarness();
        await harness.CreateTopologyAsync(2);
        var worker1 = await harness.BindTaskAsync(new AcceptanceTask("task-slot-negative", 1, "slot", "scope"));
        harness.Adapter.SetSnapshot(worker1.RuntimeId, AcceptanceSnapshots.Healthy());
        harness.Adapter.SetSubmission(worker1.RuntimeId, new(true, true, false, "SHOULD_NOT_RUN", ["wrong-slot"]));
        var provider = new BrowserChatProvider(harness.Registry, harness.Adapter, harness.Ledger, new WrongChatGuard(), harness.GlobalGate, harness.Ownership);
        var request = harness.Request(worker1, "task-slot-negative", "dispatch-slot-negative", "slot") with { WorkerSlotId = "2" };

        var result = await provider.SendAsync(worker1.RuntimeId, request);

        Assert.Equal(BrowserDispatchOutcome.NotSent, result.Outcome);
        Assert.Equal("WORKER_SLOT_MISMATCH", result.Reason);
        Assert.False(harness.Adapter.SubmitCounts.ContainsKey(worker1.RuntimeId));
    }

    [Fact]
    public async Task Null_uncertain_evidence_fails_safe_without_retry_authorization()
    {
        var reconciler = new UncertainSendReconciler(new NullConversationProbe());
        var dispatch = new DispatchLedgerEntry("dispatch-null-evidence", "hash", DispatchState.SubmittedUnknown, DateTimeOffset.UtcNow);

        var result = await reconciler.ReconcileAsync("worker-runtime", dispatch);

        Assert.Equal(SendReconciliationState.CannotDetermine, result.State);
        Assert.Equal(RetrySafety.NotSafe, result.RetrySafety);
        Assert.Equal("PROBE_RETURNED_NULL_NO_AUTOMATIC_RESEND", result.Reason);
    }

'@
    if (-not $tests.Contains($anchor)) { throw 'Acceptance negative-test insertion anchor missing.' }
    $tests = $tests.Replace($anchor, $block + $anchor)
    Write-Text $acceptanceTestsPath $tests
}

$doublesPath = 'tests/PCCExecutive.Browser.Acceptance/AcceptanceTestDoubles.cs'
$doubles = Read-Text $doublesPath
if (-not $doubles.Contains('public sealed class NullConversationProbe')) {
    $anchor = 'public sealed class PartialCapturePort'
    $block = @'
public sealed class NullConversationProbe : IConversationEvidenceProbe
{
    public Task<ConversationDispatchEvidence> InspectDispatchAsync(string runtimeId, string dispatchId, string contentHash, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<ConversationDispatchEvidence>(null!);
    }
}

'@
    if (-not $doubles.Contains($anchor)) { throw 'Null probe insertion anchor missing.' }
    $doubles = $doubles.Replace($anchor, $block + $anchor)
    Write-Text $doublesPath $doubles
}

# 7. Replace stale integration fence with a compile-safe physical-boundary test using the real provider contract.
$integrationFence = @'
using PCCExecutive.Browser;
using Xunit;

namespace PCCExecutive.Integration;

public sealed class FinalPreSubmitFenceTests
{
    [Fact]
    public async Task Tamper_after_fill_blocks_press_and_leaves_dispatch_prepared()
    {
        var registry = new InMemoryBrowserRuntimeRegistry();
        var ledger = new InMemoryDispatchLedger();
        var ownership = new AlwaysOwnershipProof();
        var runtime = Runtime("1");
        await registry.UpsertAsync(runtime);
        var adapter = new BoundaryAdapter(registry, runtime.RuntimeId, tamperWorkerSlotAfterFill: true);
        var provider = new BrowserChatProvider(registry, adapter, ledger, new WrongChatGuard(), new GlobalBrowserSendGate(), ownership);
        var request = Request(runtime, "1");

        var result = await provider.SendAsync(runtime.RuntimeId, request);
        var durable = await ledger.GetAsync(request.DispatchId);

        Assert.Equal(BrowserDispatchOutcome.NotSent, result.Outcome);
        Assert.Equal("PRE_ENTER_AUTHORIZATION_DENIED", result.Reason);
        Assert.Equal(1, adapter.FillCalls);
        Assert.Equal(0, adapter.PressCalls);
        Assert.Equal(new[] { "Fill", "Authorize" }, adapter.Steps);
        Assert.NotNull(durable);
        Assert.Equal(DispatchState.Prepared, durable!.State);
    }

    [Fact]
    public async Task Fresh_boundary_proof_occurs_after_fill_immediately_before_single_press()
    {
        var registry = new InMemoryBrowserRuntimeRegistry();
        var ledger = new InMemoryDispatchLedger();
        var runtime = Runtime("1");
        await registry.UpsertAsync(runtime);
        var adapter = new BoundaryAdapter(registry, runtime.RuntimeId, tamperWorkerSlotAfterFill: false);
        var provider = new BrowserChatProvider(registry, adapter, ledger, new WrongChatGuard(), new GlobalBrowserSendGate(), new AlwaysOwnershipProof());

        var result = await provider.SendAsync(runtime.RuntimeId, Request(runtime, "1"));

        Assert.Equal(BrowserDispatchOutcome.Submitted, result.Outcome);
        Assert.Equal(1, adapter.FillCalls);
        Assert.Equal(1, adapter.PressCalls);
        Assert.Equal(new[] { "Fill", "Authorize", "Press" }, adapter.Steps);
    }

    [Fact]
    public async Task Playwright_direct_submit_has_no_unguarded_enter_path()
    {
        var runtime = Runtime("1");
        var adapter = new PlaywrightChatGptBrowserAdapter(new NullPageProvider());
        var expected = new BrowserDispatchExpectation(runtime.ProjectRunId, runtime.LogicalAgentId, runtime.TaskId!, runtime.ConversationIdentity!, runtime.ProviderConversationIdentity!, runtime.WorkerSlotId);

        var result = await adapter.SubmitAsync(runtime, expected, "prompt");

        Assert.False(result.Triggered);
        Assert.Equal("PRE_ENTER_AUTHORIZATION_REQUIRED", result.Reason);
    }

    private static BrowserRuntimeRecord Runtime(string slot) => new()
    {
        RuntimeId = "runtime-final-boundary",
        ProjectRunId = "project-run",
        LogicalAgentId = "worker-agent",
        WorkerSlotId = slot,
        TaskId = "task",
        ProfilePath = "profile",
        CreatedByPcc = true,
        ConversationIdentity = "conversation",
        ProviderConversationIdentity = "provider-conversation",
        State = BrowserSessionState.Ready,
        LastHeartbeatAt = DateTimeOffset.UtcNow,
        LastActivityAt = DateTimeOffset.UtcNow,
        OwnershipNonce = "nonce"
    };

    private static BrowserDispatchRequest Request(BrowserRuntimeRecord runtime, string slot) =>
        new("dispatch-final-boundary", runtime.ProjectRunId, runtime.LogicalAgentId, runtime.TaskId!, runtime.ConversationIdentity!, runtime.ProviderConversationIdentity!, "prompt", null, slot);

    private sealed class AlwaysOwnershipProof : IOwnershipProofService
    {
        public Task<OwnershipProof> ProveAsync(BrowserRuntimeRecord runtime, CancellationToken cancellationToken = default) =>
            Task.FromResult(OwnershipProof.Proven(runtime.RuntimeId));
    }

    private sealed class NullPageProvider : IPlaywrightPageProvider
    {
        public Task<Microsoft.Playwright.IPage?> GetPageAsync(string runtimeId, CancellationToken cancellationToken = default) => Task.FromResult<Microsoft.Playwright.IPage?>(null);
    }

    private sealed class BoundaryAdapter(InMemoryBrowserRuntimeRegistry registry, string runtimeId, bool tamperWorkerSlotAfterFill) : IPhysicalSubmitAuthorizationAdapter
    {
        public string AdapterVersion => "boundary-test";
        public int FillCalls { get; private set; }
        public int PressCalls { get; private set; }
        public List<string> Steps { get; } = [];

        public Task<ChatGptSemanticSnapshot> InspectAsync(BrowserRuntimeRecord runtime, BrowserDispatchExpectation expectation, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatGptSemanticSnapshot(
                SemanticDetection<InputState>.Create(InputState.Ready, .99, AdapterVersion, "ready"),
                SemanticDetection<GenerationState>.Create(GenerationState.Idle, .99, AdapterVersion, "idle"),
                SemanticDetection<AuthState>.Create(AuthState.Authenticated, .99, AdapterVersion, "authenticated"),
                SemanticDetection<ConversationMatch>.Create(ConversationMatch.Match, .99, AdapterVersion, "conversation-match"),
                SemanticDetection<PageHealth>.Create(PageHealth.Healthy, .99, AdapterVersion, "healthy"),
                ResponseCompleteness.None, 0, null, DateTimeOffset.UtcNow, AdapterVersion));

        public Task<AdapterSubmissionResult> SubmitAsync(BrowserRuntimeRecord runtime, BrowserDispatchExpectation expectation, string prompt, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AdapterSubmissionResult(false, false, false, "PRE_ENTER_AUTHORIZATION_REQUIRED", ["physical-enter:fence-required"]));

        public async Task<AdapterSubmissionResult> SubmitAuthorizedAsync(BrowserRuntimeRecord runtime, BrowserDispatchExpectation expectation, string prompt, Func<CancellationToken, Task<PreEnterAuthorizationDecision>> authorizeBeforeEnter, CancellationToken cancellationToken = default)
        {
            FillCalls++;
            Steps.Add("Fill");
            if (tamperWorkerSlotAfterFill)
            {
                var current = await registry.GetAsync(runtimeId, cancellationToken) ?? throw new InvalidOperationException("runtime missing");
                await registry.UpsertAsync(current with { WorkerSlotId = "2" }, cancellationToken);
            }
            Steps.Add("Authorize");
            var authorization = await authorizeBeforeEnter(cancellationToken);
            if (!authorization.Authorized)
                return new AdapterSubmissionResult(false, false, false, "PRE_ENTER_AUTHORIZATION_DENIED", authorization.Evidence.Prepend(authorization.Reason).ToArray());
            Steps.Add("Press");
            PressCalls++;
            return new AdapterSubmissionResult(true, true, false, "SUBMISSION_PROVEN", ["press:triggered"]);
        }
    }
}
'@
Write-Text 'tests/PCCExecutive.Integration/FinalPreSubmitFenceTests.cs' $integrationFence

# Closure invariants: preserve previously landed P0s and assert the new physical boundary.
$browserText = Require-Text 'src/PCCExecutive.Browser/DispatchAndResilience.cs' 'FinalPreEnterAuthorization.AuthorizeAsync'
[void](Require-Text 'src/PCCExecutive.Browser/ChatGptBrowserAdapter.cs' 'var finalAuthorization = await authorizeBeforeEnter')
$chatText = Require-Text 'src/PCCExecutive.Browser/ChatGptBrowserAdapter.cs' 'await composer.FillAsync(prompt)'
$fillIndex = $chatText.IndexOf('await composer.FillAsync(prompt)', [StringComparison]::Ordinal)
$authIndex = $chatText.IndexOf('var finalAuthorization = await authorizeBeforeEnter', [StringComparison]::Ordinal)
$enterIndex = $chatText.IndexOf('await composer.PressAsync("Enter")', [StringComparison]::Ordinal)
if ($fillIndex -lt 0 -or $authIndex -le $fillIndex -or $enterIndex -le $authIndex) { throw 'Playwright physical boundary order must be Fill -> authorization -> Enter.' }
[void](Require-Text 'tests/PCCExecutive.Browser.Acceptance/AcceptanceHarness.cs' 'prompt, null, runtime.WorkerSlotId);')
[void](Require-Text 'src/PCCExecutive.Browser/ResilienceHardening.cs' 'PROBE_RETURNED_NULL_NO_AUTOMATIC_RESEND')
[void](Require-Text 'src/PCCExecutive.Infrastructure/CanonicalDispatchReservationService.cs' 'ReserveOrRecoverAsync')
$hostText = Require-Text 'src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs' '_dispatchReservations = new CanonicalDispatchReservationService(store);'
$workers = Require-Text 'src/PCCExecutive.Application/ManagerWorkerOrchestration.cs' '_dispatchReservations.ReserveOrRecoverAsync'
if ($hostText.Contains('DispatchId.New()')) { throw 'Unsafe Manager caller DispatchId.New() remains.' }
if ($workers.Contains('DispatchId.New()')) { throw 'Unsafe Worker caller DispatchId.New() remains.' }
[void](Require-Text 'src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs' 'startupRecovery.BeginStartupAsync(run.Id)')
[void](Require-Text 'src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs' 'startupRecovery.ReconstructAsync(run.Id)')
[void](Require-Text 'src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs' 'SafeShutdownCoordinator')
[void](Require-Text 'src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs' 'AutonomousConversationRolloverRuntime.Attach(gateway)')
[void](Require-Text 'src/PCCExecutive.App/Presentation/AutonomousConversationRolloverRuntime.cs' 'RepairInterruptedRolloversAsync')
[void](Require-Text 'src/PCCExecutive.App/Presentation/AutonomousConversationRolloverRuntime.cs' 'NormalizeActiveConversationTruthAsync')

Write-Host 'WorkerSlot correlation, uncertain-send fail-safe, and physical pre-Enter authorization closure applied and verified.'
