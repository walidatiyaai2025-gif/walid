$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Replace-IfPresent([string]$Path, [string]$Old, [string]$New) {
    $full = Join-Path $root $Path
    $text = [IO.File]::ReadAllText($full)
    if ($text.Contains($Old)) {
        [IO.File]::WriteAllText($full, $text.Replace($Old, $New), [Text.UTF8Encoding]::new($false))
        return $true
    }
    return $false
}

# Import the existing deterministic Integration/E2E acceptance assets.  This commit
# is a direct descendant of the runtime closure baseline and touches no product
# runtime source, so reusing it avoids building a second orchestration harness.
$acceptanceCommit = 'b24dbd14d8c46f2b1a8af4eb69f1cd88d033ec11'
git merge-base --is-ancestor $acceptanceCommit HEAD 2>$null
if ($LASTEXITCODE -ne 0) {
    git cherry-pick $acceptanceCommit
    if ($LASTEXITCODE -ne 0) { throw "Failed to import final acceptance commit $acceptanceCommit" }
}

# Final Browser boundary: all semantic/wrong-chat checks and fresh PCC ownership
# proof must succeed before the caller is allowed to persist its pre-submit domain
# fence.  The callback then runs immediately before Browser ledger reservation and
# Enter, making the durable domain Dispatch and Browser Dispatch share one stable id.
$browser = 'src/PCCExecutive.Browser/DispatchAndResilience.cs'
[void](Replace-IfPresent $browser @'
    public async Task<BrowserDispatchResult> SendAsync(string runtimeId, BrowserDispatchRequest request, CancellationToken cancellationToken = default)
'@ @'
    public async Task<BrowserDispatchResult> SendAsync(string runtimeId, BrowserDispatchRequest request, CancellationToken cancellationToken = default, Func<CancellationToken, Task>? beforeSubmit = null)
'@)
[void](Replace-IfPresent $browser @'
        var proof = await _ownership.ProveAsync(runtime, cancellationToken).ConfigureAwait(false);
        if (!proof.IsProven) return new(request.DispatchId, BrowserDispatchOutcome.NotSent, DispatchState.Prepared, "PCC_OWNERSHIP_NOT_PROVEN", guard.Evidence.Append(proof.Reason).ToArray());
        var contentHash = request.ContentHash ?? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Prompt)));
'@ @'
        var proof = await _ownership.ProveAsync(runtime, cancellationToken).ConfigureAwait(false);
        if (!proof.IsProven) return new(request.DispatchId, BrowserDispatchOutcome.NotSent, DispatchState.Prepared, "PCC_OWNERSHIP_NOT_PROVEN", guard.Evidence.Append(proof.Reason).ToArray());
        if (beforeSubmit is not null) await beforeSubmit(cancellationToken).ConfigureAwait(false);
        var contentHash = request.ContentHash ?? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Prompt)));
'@)

$adapter = 'src/PCCExecutive.Infrastructure/BrowserAgentProviderAdapter.cs'
[void](Replace-IfPresent $adapter @'
        var effectiveDispatchId = request.DispatchId;
        PCCExecutive.Domain.Dispatch? domainDispatch = null;
        AutonomousDispatchJournal? journal = null;
'@ @'
        var effectiveDispatchId = request.DispatchId;
        PCCExecutive.Domain.Dispatch? domainDispatch = null;
        AutonomousDispatchJournal? journal = null;
        Func<CancellationToken, Task>? beforeSubmit = null;
'@)
[void](Replace-IfPresent $adapter @'
                await journal.SaveAsync(domainDispatch, cancellationToken).ConfigureAwait(false);
            }
        }

        var browserRequest = new BrowserDispatchRequest(
'@ @'
                var prepared = domainDispatch;
                beforeSubmit = ct => journal.SaveAsync(prepared, ct);
            }
        }

        var browserRequest = new BrowserDispatchRequest(
'@)
[void](Replace-IfPresent $adapter @'
        var result = await _provider.SendAsync(runtime.RuntimeId, browserRequest, cancellationToken).ConfigureAwait(false);
'@ @'
        var result = await _provider.SendAsync(runtime.RuntimeId, browserRequest, cancellationToken, beforeSubmit).ConfigureAwait(false);
'@)

# Every crash-consistent operation persists the same merged dispatch view; callers
# can no longer overwrite the orchestration checkpoint with Dispatches=[] while a
# durable standalone dispatch fence exists.
$crashStore = 'src/PCCExecutive.Infrastructure/CrashConsistentOrchestrationStore.cs'
[void](Replace-IfPresent $crashStore @'
    private async Task<DurableCommitResult> CommitCoreAsync(OrchestrationRecoverySnapshot snapshot, string operationKind, string idempotencyKey, ICrashFaultInjector faultInjector, long? expectedRevision, CancellationToken cancellationToken)
    {
        await _schema.InitializeMetadataAsync(cancellationToken).ConfigureAwait(false);
'@ @'
    private async Task<DurableCommitResult> CommitCoreAsync(OrchestrationRecoverySnapshot snapshot, string operationKind, string idempotencyKey, ICrashFaultInjector faultInjector, long? expectedRevision, CancellationToken cancellationToken)
    {
        snapshot = await DispatchMergedOrchestrationStateStore.MergeAsync(_store, snapshot, cancellationToken).ConfigureAwait(false);
        await _schema.InitializeMetadataAsync(cancellationToken).ConfigureAwait(false);
'@)

# Browser unit fixtures carry the exact WorkerSlot binding.
$browserTests = 'tests/PCCExecutive.Browser.Tests/BrowserRuntimeTests.cs'
[void](Replace-IfPresent $browserTests 'new BrowserDispatchRequest("dispatch-1",runtime.ProjectRunId,runtime.LogicalAgentId,runtime.TaskId!,runtime.ConversationIdentity!,runtime.ProviderConversationIdentity!,"prompt")' 'new BrowserDispatchRequest("dispatch-1",runtime.ProjectRunId,runtime.LogicalAgentId,runtime.TaskId!,runtime.ConversationIdentity!,runtime.ProviderConversationIdentity!,"prompt",null,runtime.WorkerSlotId)')
[void](Replace-IfPresent $browserTests 'new BrowserDispatchRequest("new-dispatch", runtime.ProjectRunId, runtime.LogicalAgentId, runtime.TaskId!, runtime.ConversationIdentity!, "NEW", "prompt")' 'new BrowserDispatchRequest("new-dispatch", runtime.ProjectRunId, runtime.LogicalAgentId, runtime.TaskId!, runtime.ConversationIdentity!, "NEW", "prompt", null, runtime.WorkerSlotId)')
[void](Replace-IfPresent $browserTests 'private static BrowserDispatchExpectation Expectation(BrowserRuntimeRecord r)=>new(r.ProjectRunId,r.LogicalAgentId,r.TaskId!,r.ConversationIdentity!,r.ProviderConversationIdentity!);' 'private static BrowserDispatchExpectation Expectation(BrowserRuntimeRecord r)=>new(r.ProjectRunId,r.LogicalAgentId,r.TaskId!,r.ConversationIdentity!,r.ProviderConversationIdentity!,r.WorkerSlotId);')

# Acceptance fixtures use an explicit deterministic ownership authority.  Production
# code remains fail-closed and still requires the real IOwnershipProofService.
$harness = 'tests/PCCExecutive.Browser.Acceptance/AcceptanceHarness.cs'
[void](Replace-IfPresent $harness @'
    private readonly List<AcceptanceTrace> _trace = [];

    public ControlledBrowserAcceptanceHarness()
    {
        _provider = new BrowserChatProvider(_registry, _adapter, _ledger, new WrongChatGuard(), _globalGate);
    }

    public InMemoryBrowserRuntimeRegistry Registry => _registry;
'@ @'
    private readonly List<AcceptanceTrace> _trace = [];
    private readonly IOwnershipProofService _ownership = new AcceptanceOwnershipProofService();

    public ControlledBrowserAcceptanceHarness()
    {
        _provider = new BrowserChatProvider(_registry, _adapter, _ledger, new WrongChatGuard(), _globalGate, _ownership);
    }

    public InMemoryBrowserRuntimeRegistry Registry => _registry;
'@)
[void](Replace-IfPresent $harness @'
    public GlobalBrowserSendGate GlobalGate => _globalGate;
'@ @'
    public GlobalBrowserSendGate GlobalGate => _globalGate;
    public IOwnershipProofService Ownership => _ownership;
'@)
$harnessText = [IO.File]::ReadAllText((Join-Path $root $harness))
if (-not $harnessText.Contains('sealed class AcceptanceOwnershipProofService')) {
    $harnessText += @'

internal sealed class AcceptanceOwnershipProofService : IOwnershipProofService
{
    public Task<OwnershipProof> ProveAsync(BrowserRuntimeRecord runtime, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(OwnershipProof.Proven(runtime.RuntimeId));
    }
}
'@
    [IO.File]::WriteAllText((Join-Path $root $harness), $harnessText, [Text.UTF8Encoding]::new($false))
}

$acceptanceTests = 'tests/PCCExecutive.Browser.Acceptance/DeterministicBrowserAcceptanceTests.cs'
$acceptanceText = [IO.File]::ReadAllText((Join-Path $root $acceptanceTests))
$acceptanceText = $acceptanceText.Replace('new BrowserChatProvider(harness.Registry, harness.Adapter, harness.Ledger, new WrongChatGuard(), harness.GlobalGate)', 'new BrowserChatProvider(harness.Registry, harness.Adapter, harness.Ledger, new WrongChatGuard(), harness.GlobalGate, harness.Ownership)')
[IO.File]::WriteAllText((Join-Path $root $acceptanceTests), $acceptanceText, [Text.UTF8Encoding]::new($false))

Write-Host 'Durable pre-submit fence, snapshot dispatch merge, explicit acceptance ownership, and final acceptance assets applied.'