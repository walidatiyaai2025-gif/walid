namespace PCCExecutive.App.Presentation;

/// <summary>
/// Honest bootstrap gateway used only until the Integration Lead binds canonical runtime contracts.
/// It never reports browser/session/progress success and rejects all operational mutations.
/// </summary>
public sealed class UnavailablePresentationGateway : IPccExecutivePresentationGateway
{
    public RuntimeSnapshot Snapshot => RuntimeSnapshot.Unbound;

    public event EventHandler<RuntimeSnapshot>? SnapshotChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(UiAction action, string? targetId = null) => false;

    public Task ExecuteAsync(UiAction action, string? targetId = null, CancellationToken cancellationToken = default) =>
        Task.FromException(new InvalidOperationException(
            $"Cannot execute {action}: PCC Executive runtime contracts are not bound."));
}
