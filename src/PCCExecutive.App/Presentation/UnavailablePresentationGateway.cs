namespace PCCExecutive.App.Presentation;

/// <summary>
/// Honest gateway retained for tests/design-time use. It never reports runtime success
/// and rejects every operational action with an explicit reason.
/// </summary>
public sealed class UnavailablePresentationGateway : IPccExecutivePresentationGateway
{
    private readonly string _reason;

    public UnavailablePresentationGateway(string? reason = null) =>
        _reason = reason ?? "PCC Executive runtime contracts are not bound.";

    public RuntimeSnapshot Snapshot => RuntimeSnapshot.Unbound with
    {
        RuntimeStatus = _reason,
        ProjectResolutionMessage = _reason
    };

    public event EventHandler<RuntimeSnapshot>? SnapshotChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(UiAction action, string? targetId = null) => false;

    public string? DisabledReason(UiAction action, string? targetId = null) => _reason;

    public Task ExecuteAsync(UiAction action, string? targetId = null, CancellationToken cancellationToken = default) =>
        Task.FromException(new InvalidOperationException($"Cannot execute {action}: {_reason}"));
}
