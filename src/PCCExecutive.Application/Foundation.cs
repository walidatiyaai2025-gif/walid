using System.Security.Cryptography;
using System.Text;
using PCCExecutive.Domain;

namespace PCCExecutive.Application;

public sealed record AgentRequest(ProjectRunId ProjectRunId, LogicalAgentId LogicalAgentId, ConversationId ConversationId, DispatchId DispatchId, string Content, string ContentHash, WorkerSlotId? WorkerSlotId = null, TaskId? TaskId = null, WaveId? WaveId = null, string? ProviderConversationId = null);
public sealed record DurableDispatchCorrelation(ProjectRunId ProjectRunId, LogicalAgentId LogicalAgentId, WorkerSlotId? WorkerSlotId, TaskId TaskId, WaveId WaveId, ConversationId LogicalConversationId, string ProviderConversationId, string ContentHash);
public interface ICanonicalDispatchReservationService
{
    Task<Dispatch> ReserveOrRecoverAsync(DurableDispatchCorrelation correlation, CancellationToken cancellationToken = default);
}
public static class CanonicalDispatchIdentity
{
    public static DispatchId Create(DurableDispatchCorrelation correlation) => new(StableGuid(string.Join("|",
        "dispatch-v2",
        correlation.ProjectRunId,
        correlation.LogicalAgentId,
        correlation.WorkerSlotId?.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "MANAGER",
        correlation.TaskId,
        correlation.WaveId,
        correlation.LogicalConversationId,
        Normalize(correlation.ProviderConversationId),
        Normalize(correlation.ContentHash))));

    public static TaskId StableTask(ProjectRunId runId, string runtimeTaskId) => new(StableGuid($"runtime-task:{runId}:{Normalize(runtimeTaskId)}"));
    public static WaveId StableWave(ProjectRunId runId, string runtimeTaskId) => new(StableGuid($"runtime-wave:{runId}:{Normalize(runtimeTaskId)}"));

    private static string Normalize(string value) => string.IsNullOrWhiteSpace(value) ? "UNKNOWN" : value.Trim();
    private static Guid StableGuid(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        var guid = bytes[..16];
        guid[6] = (byte)((guid[6] & 0x0f) | 0x50);
        guid[8] = (byte)((guid[8] & 0x3f) | 0x80);
        return new Guid(guid);
    }
}
public sealed record AgentResult(DispatchId DispatchId, bool Accepted, bool IsGenerating, bool IsComplete, bool IsUncertain, string? Response, string? ProviderEvidence, string? ErrorCode);
public sealed record ProviderHealth(bool IsAvailable, bool IsAuthenticated, bool RequiresAttention, string? State, string? Evidence);
public interface IAgentProvider
{
    AgentProviderKind Kind { get; }
    Task<ProviderHealth> ProbeAsync(CancellationToken cancellationToken = default);
    Task<AgentResult> SendAsync(AgentRequest request, CancellationToken cancellationToken = default);
}
public interface IServiceRegistry
{
    void AddSingleton<TService, TImplementation>() where TService : class where TImplementation : class, TService;
    void AddSingleton<TService>(TService instance) where TService : class;
    void AddTransient<TService, TImplementation>() where TService : class where TImplementation : class, TService;
}
public interface IApplicationModule { void Register(IServiceRegistry services, PccExecutiveOptions options); }
public sealed record PccExecutiveOptions
{
    public AgentProviderKind DefaultProvider { get; init; } = AgentProviderKind.BrowserChat;
    public bool OpenAiApiEnabled { get; init; } = false;
    public int MaxActiveWorkers { get; init; } = 5;
    public TimeSpan BaseDispatchInterval { get; init; } = TimeSpan.FromSeconds(10);
    public bool AdaptivePacingEnabled { get; init; } = true;
    public void Validate()
    {
        if (MaxActiveWorkers is < 0 or > 5) throw new InvalidOperationException("MaxActiveWorkers must be between 0 and 5.");
        if (BaseDispatchInterval < TimeSpan.Zero) throw new InvalidOperationException("BaseDispatchInterval cannot be negative.");
        if (DefaultProvider == AgentProviderKind.OpenAiApi && !OpenAiApiEnabled) throw new InvalidOperationException("OpenAI API cannot be the default while API integration is disabled.");
    }
}
public interface IApplicationVersion { string Current { get; } }
public sealed class ApplicationVersion : IApplicationVersion { public const string ProductVersion = "0.1.0"; public string Current => ProductVersion; }
public interface ICompletedTaskIndex { bool IsCompleted(TaskId taskId); bool ContainsFingerprint(string fingerprint); }
public interface IWorkerHandoffValidator { HandoffValidationResult Validate(WorkerTask task, WorkerHandoff handoff); }
public sealed record HandoffValidationResult(bool IsValid, IReadOnlyList<string> Errors);
public interface IDispatchIdempotencyStore
{
    Task<Dispatch?> FindEquivalentAsync(ProjectRunId projectRunId, LogicalAgentId logicalAgentId, string contentHash, CancellationToken cancellationToken = default);
    Task ReserveAsync(Dispatch dispatch, CancellationToken cancellationToken = default);
}
public interface IDispatchReconciliationService { Task<DispatchReconciliationResult> ReconcileAsync(Dispatch dispatch, CancellationToken cancellationToken = default); }
public sealed record DispatchReconciliationResult(DispatchState ResolvedState, string Evidence, bool SafeToCreateRetry);
public interface IAttentionSink { Task RaiseAsync(AttentionRequest request, CancellationToken cancellationToken = default); }

public sealed record WaveValidationIssue(string Code, string Message, TaskId? TaskId = null, TaskId? OtherTaskId = null);
public sealed record WaveValidationResult(bool IsValid, IReadOnlyList<WaveValidationIssue> Issues);
public static class TaskFingerprint
{
    public static string Create(string objective, TaskScope scope, IEnumerable<TaskId>? dependencies = null)
    {
        var normalized = string.Join("|", Normalize(objective), Normalize(scope.Repository), string.Join(",", scope.Paths.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)), string.Join(",", scope.Components.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)), string.Join(",", scope.ExclusiveResources.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)), string.Join(",", (dependencies ?? []).Select(x => x.ToString()).OrderBy(x => x, StringComparer.Ordinal)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }
    private static string Normalize(string value) => string.Join(' ', value.Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
public sealed class WorkerSlotPolicy
{
    public const int MaximumActiveWorkers = 5;
    public void EnsureValidActiveCount(int activeWorkers) { if (activeWorkers is < 0 or > MaximumActiveWorkers) throw new InvalidOperationException($"Active Worker count must be between 0 and {MaximumActiveWorkers}."); }
    public void EnsureWaveTaskCount(int taskCount) { if (taskCount is < 0 or > MaximumActiveWorkers) throw new InvalidOperationException($"A Manager wave may contain 0..{MaximumActiveWorkers} tasks."); }
}
public sealed class ScopeOverlapDetector
{
    public bool Overlaps(TaskScope left, TaskScope right)
    {
        if (!string.Equals(left.Repository, right.Repository, StringComparison.OrdinalIgnoreCase)) return false;
        if (left.ExclusiveResources.Overlaps(right.ExclusiveResources) || left.Components.Overlaps(right.Components)) return true;
        foreach (var a in left.Paths) foreach (var b in right.Paths) if (PathOverlaps(a, b)) return true;
        return false;
    }
    private static bool PathOverlaps(string a, string b) { var left = NormalizePath(a); var right = NormalizePath(b); return left.Equals(right, StringComparison.OrdinalIgnoreCase) || left.StartsWith(right + "/", StringComparison.OrdinalIgnoreCase) || right.StartsWith(left + "/", StringComparison.OrdinalIgnoreCase); }
    private static string NormalizePath(string path) => path.Trim().Replace('\\', '/').Trim('/');
}
public sealed class DependencyValidator
{
    public IReadOnlyList<WaveValidationIssue> Validate(IReadOnlyList<WorkerTask> tasks, ICompletedTaskIndex completed)
    {
        var issues = new List<WaveValidationIssue>(); var byId = tasks.ToDictionary(x => x.Id);
        foreach (var task in tasks) foreach (var dependency in task.Dependencies)
        {
            if (dependency == task.Id) { issues.Add(new("SELF_DEPENDENCY", "Task cannot depend on itself.", task.Id, dependency)); continue; }
            if (!byId.ContainsKey(dependency) && !completed.IsCompleted(dependency)) issues.Add(new("MISSING_DEPENDENCY", "Dependency is neither in this wave nor already completed.", task.Id, dependency));
        }
        DetectCycles(tasks, issues); return issues;
    }
    private static void DetectCycles(IReadOnlyList<WorkerTask> tasks, List<WaveValidationIssue> issues)
    {
        var byId = tasks.ToDictionary(x => x.Id); var visiting = new HashSet<TaskId>(); var visited = new HashSet<TaskId>();
        bool Visit(TaskId id)
        {
            if (visited.Contains(id)) return false; if (!visiting.Add(id)) return true;
            if (byId.TryGetValue(id, out var task)) foreach (var dependency in task.Dependencies.Where(byId.ContainsKey)) if (Visit(dependency)) { issues.Add(new("DEPENDENCY_CYCLE", "Dependency cycle detected.", id, dependency)); return true; }
            visiting.Remove(id); visited.Add(id); return false;
        }
        foreach (var id in byId.Keys) if (Visit(id)) break;
    }
}
public sealed class WaveValidator
{
    private readonly WorkerSlotPolicy _slotPolicy; private readonly ScopeOverlapDetector _overlapDetector; private readonly DependencyValidator _dependencyValidator;
    public WaveValidator(WorkerSlotPolicy? slotPolicy = null, ScopeOverlapDetector? overlapDetector = null, DependencyValidator? dependencyValidator = null) { _slotPolicy = slotPolicy ?? new WorkerSlotPolicy(); _overlapDetector = overlapDetector ?? new ScopeOverlapDetector(); _dependencyValidator = dependencyValidator ?? new DependencyValidator(); }
    public WaveValidationResult Validate(WavePlan plan, ICompletedTaskIndex completed)
    {
        var issues = new List<WaveValidationIssue>();
        try { _slotPolicy.EnsureWaveTaskCount(plan.Tasks.Count); } catch (InvalidOperationException ex) { issues.Add(new("WORKER_LIMIT", ex.Message)); }
        foreach (var group in plan.Tasks.GroupBy(x => x.Id).Where(x => x.Count() > 1)) issues.Add(new("DUPLICATE_TASK_ID", "Task ID appears more than once in the wave.", group.Key));
        foreach (var group in plan.Tasks.GroupBy(x => x.Fingerprint, StringComparer.OrdinalIgnoreCase).Where(x => x.Count() > 1)) { var pair = group.Take(2).ToArray(); issues.Add(new("DUPLICATE_TASK_FINGERPRINT", "Duplicate task fingerprint detected.", pair[0].Id, pair[1].Id)); }
        foreach (var task in plan.Tasks.Where(x => completed.ContainsFingerprint(x.Fingerprint))) issues.Add(new("ALREADY_COMPLETED", "Task fingerprint is already completed.", task.Id));
        for (var i = 0; i < plan.Tasks.Count; i++) for (var j = i + 1; j < plan.Tasks.Count; j++) if (_overlapDetector.Overlaps(plan.Tasks[i].Scope, plan.Tasks[j].Scope)) issues.Add(new("OVERLAPPING_SCOPE", "Worker task scopes overlap and cannot run in parallel.", plan.Tasks[i].Id, plan.Tasks[j].Id));
        issues.AddRange(_dependencyValidator.Validate(plan.Tasks, completed)); return new(issues.Count == 0, issues);
    }
}

public sealed record CompletionEvaluation(VerifiedCompletion Verified, ProjectCompletionMode Mode, IReadOnlyList<string> BlockingGateNames);
public sealed class CompletionEngine
{
    public CompletionEvaluation Evaluate(IReadOnlyList<CompletionGate> gates, IReadOnlyList<Blocker> blockers)
    {
        if (gates.Count == 0) return new(new VerifiedCompletion(0), ProjectCompletionMode.Active, []);
        var applicable = gates.Where(x => x.State != GateState.NotApplicable).ToArray(); var totalWeight = applicable.Sum(x => x.Weight); var earned = applicable.Sum(x => x.Weight * Credit(x.State)); var raw = totalWeight <= 0 ? 100m : decimal.Round(earned / totalWeight * 100m, 2);
        var blockingGates = gates.Where(x => x.Mandatory && x.State is not (GateState.Pass or GateState.NotApplicable)).Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var hasExternalBlocker = blockers.Any(x => x.External) || gates.Any(x => x.Mandatory && x.State == GateState.BlockedExternal);
        if (blockingGates.Length > 0 && raw >= 99m) raw = 98.99m;
        var verified = new VerifiedCompletion(Math.Clamp(raw, 0m, 100m));
        if (hasExternalBlocker && verified.Percent < 99m) return new(verified, ProjectCompletionMode.Blocked, blockingGates);
        if (verified.Percent == 100m && blockingGates.Length == 0 && blockers.Count == 0) return new(verified, ProjectCompletionMode.VerifiedComplete, blockingGates);
        if (verified.Percent >= 99m) return new(verified, ProjectCompletionMode.ClosureMode, blockingGates);
        return new(verified, ProjectCompletionMode.Active, blockingGates);
    }
    private static decimal Credit(GateState state) => state switch { GateState.Pass => 1m, GateState.Partial => 0.5m, _ => 0m };
}
public sealed class LoopGuardService
{
    public LoopAssessment Analyze(IReadOnlyList<LoopSnapshot> snapshots, int repetitionThreshold = 3, decimal negligibleProgress = 0.25m)
    {
        if (repetitionThreshold < 2) throw new ArgumentOutOfRangeException(nameof(repetitionThreshold));
        if (snapshots.Count < repetitionThreshold) return new(LoopGuardLevel.Normal, []);
        var window = snapshots.TakeLast(repetitionThreshold).ToArray(); var signals = new List<LoopSignal>();
        AddRepeated(signals, LoopSignalType.RepeatedTaskFingerprint, window.Select(x => x.TaskFingerprints), repetitionThreshold);
        AddRepeated(signals, LoopSignalType.RepeatedBlocker, window.Select(x => x.BlockerFingerprints), repetitionThreshold);
        AddRepeated(signals, LoopSignalType.UnchangedSourceOrEvidence, window.Select(x => x.SourceEvidenceFingerprints), repetitionThreshold);
        AddRepeated(signals, LoopSignalType.RepeatedFailedCheck, window.Select(x => x.FailedCheckFingerprints), repetitionThreshold);
        AddRepeated(signals, LoopSignalType.RepeatedManagerReassignment, window.Select(x => x.ManagerReassignmentFingerprints), repetitionThreshold);
        var progressDelta = window[^1].VerifiedCompletion.Percent - window[0].VerifiedCompletion.Percent; if (progressDelta <= negligibleProgress) signals.Add(new(LoopSignalType.NegligibleProgress, $"delta:{progressDelta:0.##}", repetitionThreshold));
        var level = signals.Count switch { 0 => LoopGuardLevel.Normal, 1 => LoopGuardLevel.Watch, 2 => LoopGuardLevel.Stagnating, _ => LoopGuardLevel.LoopDetected }; return new(level, signals);
    }
    private static void AddRepeated(List<LoopSignal> signals, LoopSignalType type, IEnumerable<IReadOnlySet<string>> sets, int repetitionThreshold)
    {
        var materialized = sets.ToArray(); if (materialized.Length == 0) return; var common = new HashSet<string>(materialized[0], StringComparer.OrdinalIgnoreCase); foreach (var set in materialized.Skip(1)) common.IntersectWith(set); foreach (var fingerprint in common.Where(x => !string.IsNullOrWhiteSpace(x))) signals.Add(new(type, fingerprint, repetitionThreshold));
    }
}
