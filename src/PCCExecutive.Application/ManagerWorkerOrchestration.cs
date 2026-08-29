using System.Security.Cryptography;
using System.Text;
using PCCExecutive.Domain;

namespace PCCExecutive.Application;

public sealed record WorkerExecutionBinding(WorkerSlotId SlotId, LogicalAgentId LogicalAgentId, ConversationId ConversationId, string? ProviderConversationId = null);
public sealed record WorkerDispatchOutcome(WorkerTask Task, WorkerExecutionBinding Binding, AgentResult Result);
public sealed record WaveDispatchResult(WavePlan Plan, WaveValidationResult Validation, IReadOnlyList<WorkerDispatchOutcome> Dispatches)
{
    public bool IsAccepted => Validation.IsValid;
    public bool HasUncertainDispatch => Dispatches.Any(x => x.Result.IsUncertain);
}
public sealed record ManagerWaveReview(
    WaveId WaveId,
    ManagerEstimate ManagerEstimate,
    IReadOnlyList<WorkerHandoff> AcceptedHandoffs,
    IReadOnlyList<string> RejectedHandoffs,
    IReadOnlyList<EvidenceRecord> Evidence,
    IReadOnlyList<string> Blockers,
    string ConsolidatedSummary);

public sealed class WorkerHandoffValidator : IWorkerHandoffValidator
{
    public HandoffValidationResult Validate(WorkerTask task, WorkerHandoff handoff)
    {
        var errors = new List<string>();
        if (task.Id != handoff.TaskId) errors.Add("TASK_ID_MISMATCH");
        if (string.IsNullOrWhiteSpace(handoff.Status)) errors.Add("STATUS_REQUIRED");
        if (handoff.Changed is null) errors.Add("CHANGED_REQUIRED");
        if (handoff.Validation is null || handoff.Validation.Count == 0) errors.Add("VALIDATION_REQUIRED");
        if (string.IsNullOrWhiteSpace(handoff.NextAction)) errors.Add("NEXT_ACTION_REQUIRED");
        if (string.Equals(handoff.Status, "DONE", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(handoff.Head)) errors.Add("DONE_REQUIRES_EXACT_HEAD");
        return new(errors.Count == 0, errors);
    }
}

public sealed class ManagerWorkerOrchestrator
{
    private readonly IAgentProvider _provider;
    private readonly WaveValidator _waveValidator;
    private readonly IWorkerHandoffValidator _handoffValidator;
    private readonly ICanonicalDispatchReservationService? _dispatchReservations;
    private readonly TimeSpan _baseDispatchInterval;

    public ManagerWorkerOrchestrator(
        IAgentProvider provider,
        WaveValidator? waveValidator = null,
        IWorkerHandoffValidator? handoffValidator = null,
        TimeSpan? baseDispatchInterval = null,
        ICanonicalDispatchReservationService? dispatchReservations = null)
    {
        _provider = provider;
        _waveValidator = waveValidator ?? new WaveValidator();
        _handoffValidator = handoffValidator ?? new WorkerHandoffValidator();
        _dispatchReservations = dispatchReservations;
        _baseDispatchInterval = baseDispatchInterval ?? TimeSpan.FromSeconds(10);
        if (_baseDispatchInterval < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(baseDispatchInterval));
    }

    public async Task<WaveDispatchResult> DispatchWaveAsync(
        ProjectRunId projectRunId,
        WavePlan plan,
        IReadOnlyList<WorkerExecutionBinding> bindings,
        ICompletedTaskIndex completed,
        CancellationToken cancellationToken = default)
    {
        var validation = _waveValidator.Validate(plan, completed);
        if (!validation.IsValid) return new(plan, validation, Array.Empty<WorkerDispatchOutcome>());
        if (bindings.Count < plan.Tasks.Count) throw new InvalidOperationException("Each Worker task requires a logical-agent/conversation binding.");
        if (bindings.Take(plan.Tasks.Count).Select(x => x.SlotId).Distinct().Count() != plan.Tasks.Count) throw new InvalidOperationException("Worker slot bindings must be unique.");

        var results = new List<WorkerDispatchOutcome>(plan.Tasks.Count);
        for (var i = 0; i < plan.Tasks.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (i > 0 && _baseDispatchInterval > TimeSpan.Zero)
                await Task.Delay(_baseDispatchInterval, cancellationToken).ConfigureAwait(false);

            var task = plan.Tasks[i];
            var binding = bindings[i];
            var content = BuildWorkerPrompt(task, binding);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
            var providerConversationId = binding.ProviderConversationId ?? binding.ConversationId.ToString();
            var correlation = new DurableDispatchCorrelation(projectRunId, binding.LogicalAgentId, binding.SlotId, task.Id, plan.WaveId, binding.ConversationId, providerConversationId, hash);
            var dispatchId = CanonicalDispatchIdentity.Create(correlation);
            if (_dispatchReservations is not null)
                dispatchId = (await _dispatchReservations.ReserveOrRecoverAsync(correlation, cancellationToken).ConfigureAwait(false)).Id;
            var request = new AgentRequest(projectRunId, binding.LogicalAgentId, binding.ConversationId, dispatchId, content, hash, binding.SlotId, task.Id, plan.WaveId, providerConversationId);
            var result = await _provider.SendAsync(request, cancellationToken).ConfigureAwait(false);
            results.Add(new(task, binding, result));

            // Uncertain delivery is a reconciliation stop, never an automatic retry.
            if (result.IsUncertain) break;
        }

        return new(plan, validation, results);
    }

    public ManagerWaveReview Reconcile(
        WavePlan plan,
        IReadOnlyList<WorkerHandoff> handoffs,
        IReadOnlyList<EvidenceRecord> evidence)
    {
        var byTask = handoffs.GroupBy(x => x.TaskId).ToDictionary(x => x.Key, x => x.OrderByDescending(h => h.ReceivedAt).First());
        var accepted = new List<WorkerHandoff>();
        var rejected = new List<string>();
        var blockers = new List<string>();

        foreach (var task in plan.Tasks)
        {
            if (!byTask.TryGetValue(task.Id, out var handoff))
            {
                rejected.Add($"{task.Id}:HANDOFF_MISSING");
                continue;
            }

            var validation = _handoffValidator.Validate(task, handoff);
            if (!validation.IsValid)
            {
                rejected.Add($"{task.Id}:{string.Join(',', validation.Errors)}");
                continue;
            }

            accepted.Add(handoff);
            if (!string.IsNullOrWhiteSpace(handoff.Blocker)) blockers.Add($"{task.Id}:{handoff.Blocker}");
        }

        blockers.AddRange(plan.Blockers.Select(x => $"{x.Code}:{x.Description}"));
        var summary = $"Wave {plan.WaveId}: {accepted.Count}/{plan.Tasks.Count} handoffs accepted; {evidence.Count} evidence records; {blockers.Count} blockers; {rejected.Count} rejected/missing handoffs.";
        return new(plan.WaveId, plan.ManagerEstimate, accepted, rejected, evidence, blockers, summary);
    }

    private static string BuildWorkerPrompt(WorkerTask task, WorkerExecutionBinding binding) =>
        $"TASK: {task.Id}\nWORKER_SLOT: {binding.SlotId}\nOBJECTIVE: {task.Objective}\nSCOPE_REPOSITORY: {task.Scope.Repository}\nSCOPE_PATHS: {string.Join(',', task.Scope.Paths)}\nACCEPTANCE: {string.Join(" | ", task.AcceptanceCriteria)}\nOUTPUT: TASK, STATUS, HEAD, CHANGED, VALIDATION, BLOCKER, NEXT_ACTION";
}
