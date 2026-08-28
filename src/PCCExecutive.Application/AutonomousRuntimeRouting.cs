using System.Collections.Concurrent;

namespace PCCExecutive.Application;

public enum AutonomousRuntimeAction
{
    None,
    WaitForAutomation,
    RecoverBrowser,
    ResumeOrchestration,
    RequireHumanAttention,
}

public sealed record RuntimeRecoveryObservation(
    string RuntimeId,
    BrowserRecoveryState BrowserState,
    string? ReasonCode = null,
    string? ExactLocation = null,
    bool RecoveryInProgress = false,
    bool RecoveryPolicyExhausted = false,
    bool SafeToResume = false);

public sealed record HumanAttentionAction(
    string ReasonCode,
    string WhatHappened,
    string WhyAutomationCannotContinue,
    string ExactLocation,
    string RequiredAction);

public sealed record AutonomousRuntimeDecision(
    AutonomousRuntimeAction Action,
    GuidedExecutionEvaluation Guidance,
    string ReasonCode,
    string Explanation,
    HumanAttentionAction? Attention = null)
{
    public bool RequiresHumanAttention => Attention is not null;
}

/// <summary>Arbitrates the one next runtime action without duplicating prerequisite or browser recovery engines.</summary>
public sealed class AutonomousNextActionRouter(GuidedExecutionEvaluator evaluator)
{
    public AutonomousRuntimeDecision Route(GuidedRuntimeState runtime, RuntimeRecoveryObservation browser)
    {
        var guidance = evaluator.Evaluate(runtime);

        if (TryCreateHumanAttention(browser, out var attention))
            return new(AutonomousRuntimeAction.RequireHumanAttention, guidance, attention.ReasonCode,
                attention.WhatHappened, attention);

        if (browser.RecoveryInProgress || browser.BrowserState is BrowserRecoveryState.RecoveringRuntime or BrowserRecoveryState.DegradedEndpointStale)
            return new(AutonomousRuntimeAction.WaitForAutomation, guidance, "BROWSER_RECOVERY_IN_PROGRESS",
                "PCC Executive is automatically recovering its managed browser. No operator action is required.");

        if (browser.BrowserState is BrowserRecoveryState.RecoveryFailed && !browser.RecoveryPolicyExhausted)
            return new(AutonomousRuntimeAction.RecoverBrowser, guidance, "BROWSER_AUTO_RECOVERY_REQUIRED",
                "PCC Executive will recover or replace only the affected PCC-owned browser runtime.");

        if (browser.SafeToResume && guidance.NextAction.Kind == GuidedActionKind.Automatic)
            return new(AutonomousRuntimeAction.ResumeOrchestration, guidance, "SAFE_AUTO_RESUME",
                "Browser readiness is reconciled and autonomous orchestration can resume safely.");

        return new(AutonomousRuntimeAction.None, guidance, guidance.NextAction.ReasonCode, guidance.NextAction.Instruction);
    }

    private static bool TryCreateHumanAttention(RuntimeRecoveryObservation browser, out HumanAttentionAction attention)
    {
        var code = Normalize(browser.ReasonCode);
        var location = string.IsNullOrWhiteSpace(browser.ExactLocation) ? "01 Chrome" : browser.ExactLocation!;
        attention = code switch
        {
            "LOGIN_REQUIRED" or "CHROME_LOGIN_REQUIRED" => new("LOGIN_REQUIRED", "ChatGPT sign-in is required.",
                "PCC Executive cannot complete account sign-in.", location, "Open the PCC browser and complete sign-in."),
            "CAPTCHA" or "ACCOUNT_CHALLENGE" or "CHALLENGE" => new("ACCOUNT_CHALLENGE", "ChatGPT requires an account challenge.",
                "PCC Executive cannot complete a CAPTCHA or account challenge.", location, "Open the PCC browser and complete the challenge."),
            "MISSING_CREDENTIAL" or "MISSING_AUTHORITY" => new(code, "Required external access is missing.",
                "PCC Executive cannot grant credentials or authority to itself.", location, "Provide the required access, then return to PCC Executive."),
            "DESTRUCTIVE_APPROVAL" => new(code, "An irreversible action requires approval.",
                "PCC Executive is not allowed to approve irreversible actions for you.", location, "Review and approve or reject the action."),
            _ => null!,
        };

        return attention is not null;
    }

    private static string Normalize(string? value) => (value ?? string.Empty).Trim().ToUpperInvariant();
}

/// <summary>Prevents concurrent or repeated recovery attempts for the same logical runtime.</summary>
public sealed class RuntimeRecoveryLeaseCoordinator
{
    private readonly ConcurrentDictionary<string, byte> _active = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _completedFingerprints = new(StringComparer.Ordinal);

    public bool TryAcquire(string runtimeId, string recoveryFingerprint, out IDisposable? lease)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(recoveryFingerprint);
        lease = null;
        if (_completedFingerprints.TryGetValue(runtimeId, out var completed) && StringComparer.Ordinal.Equals(completed, recoveryFingerprint))
            return false;
        if (!_active.TryAdd(runtimeId, 0))
            return false;
        lease = new Lease(this, runtimeId, recoveryFingerprint);
        return true;
    }

    public void Forget(string runtimeId) => _completedFingerprints.TryRemove(runtimeId, out _);

    private sealed class Lease(RuntimeRecoveryLeaseCoordinator owner, string runtimeId, string fingerprint) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            owner._completedFingerprints[runtimeId] = fingerprint;
            owner._active.TryRemove(runtimeId, out _);
        }
    }
}
