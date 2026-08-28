using PCCExecutive.Browser;

namespace PCCExecutive.Infrastructure;

public interface INewSendPausePort
{
    Task PauseNewSendsAsync(string reason, CancellationToken cancellationToken = default);
    Task ResumeNewSendsAsync(string reason, CancellationToken cancellationToken = default);
}

public sealed class BrowserNewSendPausePort : INewSendPausePort
{
    private readonly GlobalBrowserSendGate _gate;

    public BrowserNewSendPausePort(GlobalBrowserSendGate gate) => _gate = gate;

    public Task PauseNewSendsAsync(string reason, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _gate.Apply(
            new ResilienceDecision(
                ChatGptResilienceState.Paused,
                FaultScope.Global,
                PauseUnsafeNewSends: true,
                RequiresHumanAction: false,
                Reason: reason),
            DateTimeOffset.UtcNow);
        return Task.CompletedTask;
    }

    public Task ResumeNewSendsAsync(string reason, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _gate.TryResume(DateTimeOffset.UtcNow, reason);
        return Task.CompletedTask;
    }
}
