namespace PCCExecutive.Application;

/// <summary>
/// Minimal orchestration boundary used by durable shutdown/update recovery to stop new Browser sends
/// without owning Browser mechanics. Implementations may pause/resume the existing dispatch gate.
/// </summary>
public interface INewSendPausePort
{
    Task PauseNewSendsAsync(string reason, CancellationToken cancellationToken = default);
    Task ResumeNewSendsAsync(string reason, CancellationToken cancellationToken = default);
}
