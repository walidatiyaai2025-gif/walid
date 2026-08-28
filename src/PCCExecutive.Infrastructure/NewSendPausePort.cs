using PCCExecutive.Browser;

namespace PCCExecutive.Infrastructure;

public interface INewSendPausePort
{
    Task PauseNewSendsAsync(string reason, CancellationToken cancellationToken = default);
    Task ResumeNewSendsAsync(string reason, CancellationToken cancellationToken = default);
}

public sealed class BrowserNewSendPausePort : INewSendPausePort
{
    private enum PauseKind
    {
        Operator,
        StartupRecovery,
        RuntimeHealth,
        Rollover,
        Lifecycle,
        Generic
    }

    private readonly GlobalBrowserSendGate _gate;
    private readonly object _sync = new();
    private readonly Dictionary<PauseKind, string> _blockers = new();

    public BrowserNewSendPausePort(GlobalBrowserSendGate gate) => _gate = gate;

    public Task PauseNewSendsAsync(string reason, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            _blockers[ClassifyPause(reason)] = reason;
            ApplyEffectivePauseLocked();
        }
        return Task.CompletedTask;
    }

    public Task ResumeNewSendsAsync(string reason, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var resumedKind = ClassifyResume(reason);
            if (resumedKind == PauseKind.Operator && _blockers.ContainsKey(PauseKind.StartupRecovery))
            {
                ApplyEffectivePauseLocked();
                throw new InvalidOperationException("STARTUP_RECOVERY_REQUIRED: operator Resume AI cannot clear the startup Browser recovery fence.");
            }

            _blockers.Remove(resumedKind);
            if (_blockers.Count == 0)
            {
                _gate.TryResume(DateTimeOffset.UtcNow, reason);
                return Task.CompletedTask;
            }

            ApplyEffectivePauseLocked();
        }
        return Task.CompletedTask;
    }

    private void ApplyEffectivePauseLocked()
    {
        var reason = string.Join(" | ", _blockers
            .OrderBy(x => x.Key)
            .Select(x => $"{x.Key}:{x.Value}"));
        _gate.Apply(
            new ResilienceDecision(
                ChatGptResilienceState.Paused,
                FaultScope.Global,
                PauseUnsafeNewSends: true,
                RequiresHumanAction: false,
                Reason: reason),
            DateTimeOffset.UtcNow);
    }

    private static PauseKind ClassifyPause(string reason)
    {
        if (reason.StartsWith("Operator paused", StringComparison.Ordinal) ||
            reason.StartsWith("Restored persisted operator pause", StringComparison.Ordinal))
            return PauseKind.Operator;
        if (reason.StartsWith("STARTUP_BROWSER_RECONCILIATION:", StringComparison.Ordinal))
            return PauseKind.StartupRecovery;
        if (reason.Contains("ROLLOVER", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("Conversation rollover", StringComparison.OrdinalIgnoreCase))
            return PauseKind.Rollover;
        if (reason.StartsWith("SAFE_SHUTDOWN", StringComparison.Ordinal) ||
            reason.StartsWith("PRE_UPDATE_CHECKPOINT", StringComparison.Ordinal))
            return PauseKind.Lifecycle;
        if (reason.Contains("RATE", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("OFFLINE", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("LOGIN", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("CHALLENGE", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("health", StringComparison.OrdinalIgnoreCase))
            return PauseKind.RuntimeHealth;
        return PauseKind.Generic;
    }

    private static PauseKind ClassifyResume(string reason)
    {
        if (reason.StartsWith("Operator resumed AI", StringComparison.Ordinal))
            return PauseKind.Operator;
        if (reason.Contains("rollover", StringComparison.OrdinalIgnoreCase))
            return PauseKind.Rollover;
        if (reason.Contains("health", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("semantic", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("provider", StringComparison.OrdinalIgnoreCase))
            return PauseKind.RuntimeHealth;
        return PauseKind.Generic;
    }
}
