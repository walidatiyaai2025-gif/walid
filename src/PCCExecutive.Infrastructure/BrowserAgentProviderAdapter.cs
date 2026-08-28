using System.Security.Cryptography;
using System.Text;
using PCCExecutive.Application;
using PCCExecutive.Browser;
using PCCExecutive.Domain;

namespace PCCExecutive.Infrastructure;

public sealed class BrowserAgentProviderAdapter : IAgentProvider
{
    private readonly IBrowserRuntimeRegistry _runtimes;
    private readonly PCCExecutive.Browser.BrowserChatProvider _provider;
    private readonly SqliteStateStore? _durableStore;
    private readonly IOwnershipProofService? _ownership;

    public BrowserAgentProviderAdapter(
        IBrowserRuntimeRegistry runtimes,
        PCCExecutive.Browser.BrowserChatProvider provider,
        IOwnershipProofService? ownership = null)
    {
        _runtimes = runtimes;
        _provider = provider;
        _durableStore = runtimes as SqliteStateStore;
        _ownership = ownership;
    }

    public AgentProviderKind Kind => AgentProviderKind.BrowserChat;

    public async Task<ProviderHealth> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var runtimes = await _runtimes.ListAsync(cancellationToken).ConfigureAwait(false);
        var active = runtimes.Where(x => !x.IsArchived && x.State is not BrowserSessionState.Killed and not BrowserSessionState.Archived).ToArray();
        if (active.Length == 0)
            return new(true, false, false, "NO_BOUND_BROWSER_RUNTIME", "BrowserChatProvider is configured as default; no PCC-owned runtime is bound yet.");

        // Browser session state is inventory state, not semantic authentication proof.
        // Report configured/available here; actual authentication is proven by the
        // semantic adapter at the final send boundary.
        return new(true, false, false, "PCC_BROWSER_RUNTIME_PRESENT_AUTH_UNPROVEN", $"owned-runtime-count:{active.Length}");
    }

    public async Task<AgentResult> SendAsync(AgentRequest request, CancellationToken cancellationToken = default)
    {
        var runtimes = await _runtimes.ListAsync(cancellationToken).ConfigureAwait(false);
        var runtime = runtimes
            .Where(x => !x.IsArchived && x.State is not BrowserSessionState.Killed and not BrowserSessionState.Archived)
            .FirstOrDefault(x => StringComparer.Ordinal.Equals(x.ProjectRunId, request.ProjectRunId.ToString()) && StringComparer.Ordinal.Equals(x.LogicalAgentId, request.LogicalAgentId.ToString()));

        if (runtime is null)
            return NotSent(request.DispatchId, "owned-runtime:not-found", "BROWSER_RUNTIME_NOT_BOUND");

        if (string.IsNullOrWhiteSpace(runtime.TaskId) || string.IsNullOrWhiteSpace(runtime.ConversationIdentity) || string.IsNullOrWhiteSpace(runtime.ProviderConversationIdentity))
            return NotSent(request.DispatchId, $"runtime:{runtime.RuntimeId}", "BROWSER_DISPATCH_BINDING_INCOMPLETE");

        if (!StringComparer.Ordinal.Equals(runtime.ConversationIdentity, request.ConversationId.ToString()))
            return NotSent(request.DispatchId, $"runtime:{runtime.RuntimeId};conversation:mismatch", "WRONG_CONVERSATION_BINDING");

        if (request.TaskId is not null && !StringComparer.Ordinal.Equals(runtime.TaskId, request.TaskId.Value.ToString()))
            return NotSent(request.DispatchId, $"runtime:{runtime.RuntimeId};expected-task:{request.TaskId};actual-task:{runtime.TaskId}", "WRONG_TASK_BINDING");

        var expectedSlot = request.WorkerSlotId?.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!StringComparer.Ordinal.Equals(runtime.WorkerSlotId, expectedSlot))
            return NotSent(request.DispatchId, $"runtime:{runtime.RuntimeId};expected-slot:{expectedSlot ?? "MANAGER"};actual-slot:{runtime.WorkerSlotId ?? "MANAGER"}", "WRONG_WORKER_SLOT_BINDING");

        // Production composition uses SqliteStateStore. Refuse durable production
        // dispatch if the final ownership service was not provided; in-memory test
        // providers remain usable for isolated unit tests.
        if (_durableStore is not null)
        {
            if (_ownership is null)
                return NotSent(request.DispatchId, $"runtime:{runtime.RuntimeId}", "PCC_OWNERSHIP_PROOF_SERVICE_REQUIRED");
            var preflight = await _ownership.ProveAsync(runtime, cancellationToken).ConfigureAwait(false);
            if (!preflight.IsProven)
                return NotSent(request.DispatchId, $"runtime:{runtime.RuntimeId};ownership:{preflight.Reason}", "PCC_OWNERSHIP_NOT_PROVEN");
        }

        var effectiveDispatchId = request.DispatchId;
        PCCExecutive.Domain.Dispatch? domainDispatch = null;
        AutonomousDispatchJournal? journal = null;
        Func<CancellationToken, Task>? beforeSubmit = null;
        if (_durableStore is not null)
        {
            journal = new AutonomousDispatchJournal(_durableStore);
            var taskId = request.TaskId ?? new TaskId(StableGuid($"runtime-task:{request.ProjectRunId}:{runtime.TaskId}"));
            var waveId = request.WaveId ?? new WaveId(StableGuid($"runtime-wave:{request.ProjectRunId}:{runtime.TaskId}"));
            var existing = await journal.FindEquivalentAsync(request.ProjectRunId, request.LogicalAgentId, taskId, request.ConversationId, request.ContentHash, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                var reconciled = await journal.ReconcileAsync(existing, cancellationToken).ConfigureAwait(false);
                domainDispatch = reconciled.Dispatch;
                effectiveDispatchId = domainDispatch.Id;
                if (reconciled.IsUncertain)
                    return new(effectiveDispatchId, false, false, false, true, null, reconciled.Evidence, "SUBMITTED_UNKNOWN");
                if (reconciled.AlreadyAccepted)
                    return new(effectiveDispatchId, true, domainDispatch.State == PCCExecutive.Domain.DispatchState.GENERATING, domainDispatch.State == PCCExecutive.Domain.DispatchState.COMPLETED, false, null, reconciled.Evidence, null);
                if (!reconciled.SafeToSubmit)
                    return NotSent(effectiveDispatchId, reconciled.Evidence, $"DURABLE_DISPATCH_{domainDispatch.State}");
            }
            else
            {
                domainDispatch = new PCCExecutive.Domain.Dispatch(
                    effectiveDispatchId,
                    request.ProjectRunId,
                    waveId,
                    taskId,
                    request.LogicalAgentId,
                    request.ConversationId,
                    request.ContentHash,
                    DateTimeOffset.UtcNow,
                    PCCExecutive.Domain.DispatchState.PREPARED,
                    null,
                    null,
                    null,
                    null,
                    $"runtime-task:{runtime.TaskId};worker-slot:{expectedSlot ?? "MANAGER"}");
                var prepared = domainDispatch;
                beforeSubmit = ct => journal.SaveAsync(prepared, ct);
            }
        }

        var browserRequest = new BrowserDispatchRequest(
            effectiveDispatchId.ToString(),
            request.ProjectRunId.ToString(),
            request.LogicalAgentId.ToString(),
            runtime.TaskId,
            runtime.ConversationIdentity,
            runtime.ProviderConversationIdentity,
            request.Content,
            request.ContentHash,
            expectedSlot);

        var result = await _provider.SendAsync(runtime.RuntimeId, browserRequest, cancellationToken, beforeSubmit).ConfigureAwait(false);
        var evidence = string.Join(";", result.Evidence.Prepend(result.Reason));

        if (journal is not null && domainDispatch is not null)
        {
            var mapped = result.Outcome switch
            {
                BrowserDispatchOutcome.Submitted => result.State == PCCExecutive.Browser.DispatchState.Generating ? PCCExecutive.Domain.DispatchState.GENERATING : PCCExecutive.Domain.DispatchState.SUBMITTED,
                BrowserDispatchOutcome.SubmittedUnknown => PCCExecutive.Domain.DispatchState.SUBMITTED_UNKNOWN,
                BrowserDispatchOutcome.DuplicateBlocked => domainDispatch.State,
                _ when result.State == PCCExecutive.Browser.DispatchState.SafeRetry => PCCExecutive.Domain.DispatchState.PREPARED,
                _ => PCCExecutive.Domain.DispatchState.FAILED
            };
            domainDispatch = domainDispatch with
            {
                State = mapped,
                SubmittedAt = mapped is PCCExecutive.Domain.DispatchState.SUBMITTED or PCCExecutive.Domain.DispatchState.GENERATING ? DateTimeOffset.UtcNow : domainDispatch.SubmittedAt,
                ReconciliationEvidence = evidence
            };
            await journal.SaveAsync(domainDispatch, cancellationToken).ConfigureAwait(false);
        }

        return result.Outcome switch
        {
            BrowserDispatchOutcome.Submitted => new(effectiveDispatchId, true, result.State == PCCExecutive.Browser.DispatchState.Generating, false, false, null, evidence, null),
            BrowserDispatchOutcome.SubmittedUnknown => new(effectiveDispatchId, false, false, false, true, null, evidence, "SUBMITTED_UNKNOWN"),
            BrowserDispatchOutcome.DuplicateBlocked => new(effectiveDispatchId, false, false, false, false, null, evidence, "DUPLICATE_SEND_BLOCKED"),
            _ => new(effectiveDispatchId, false, false, false, false, null, evidence, result.Reason)
        };
    }

    private static AgentResult NotSent(DispatchId id, string evidence, string error) =>
        new(id, false, false, false, false, null, evidence, error);

    private static Guid StableGuid(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        var guid = bytes[..16];
        guid[6] = (byte)((guid[6] & 0x0f) | 0x50);
        guid[8] = (byte)((guid[8] & 0x3f) | 0x80);
        return new Guid(guid);
    }
}
