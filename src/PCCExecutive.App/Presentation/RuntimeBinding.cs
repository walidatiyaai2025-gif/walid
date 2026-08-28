namespace PCCExecutive.App.Presentation;

/// <summary>
/// Thin presentation-facing integration boundary. Implementations translate accepted
/// Application/PCC/GitHub/Browser/Updater contracts into semantic UI state; ViewModels
/// never own runtime mechanics.
/// </summary>
public interface IRuntimeBinding : IAsyncDisposable
{
    RuntimeSnapshot Current { get; }
    Task<RuntimeSnapshot> RefreshAsync(CancellationToken cancellationToken = default);
    bool CanExecute(UiAction action, string? targetId = null);
    string? DisabledReason(UiAction action, string? targetId = null);
    Task ExecuteAsync(UiAction action, string? targetId = null, CancellationToken cancellationToken = default);
}

public sealed class UnavailableRuntimeBinding : IRuntimeBinding
{
    private readonly string _reason;
    public UnavailableRuntimeBinding(string reason) => _reason = reason;
    public RuntimeSnapshot Current => RuntimeSnapshot.Unbound with
    {
        RuntimeStatus = "Runtime dependencies not integrated",
        CurrentExecutionFlow = _reason
    };

    public Task<RuntimeSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Current);
    }

    public bool CanExecute(UiAction action, string? targetId = null) => action == UiAction.Refresh;
    public string? DisabledReason(UiAction action, string? targetId = null) =>
        action == UiAction.Refresh ? null : _reason;

    public Task ExecuteAsync(UiAction action, string? targetId = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (action == UiAction.Refresh) return Task.CompletedTask;
        throw new InvalidOperationException($"{action} is disabled: {_reason}");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class OperationalPresentationGateway : IPccExecutivePresentationGateway, IAsyncDisposable
{
    private readonly IRuntimeBinding _binding;
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private RuntimeSnapshot _snapshot;
    private bool _busy;

    public OperationalPresentationGateway(IRuntimeBinding binding)
    {
        _binding = binding;
        _snapshot = binding.Current;
    }

    public RuntimeSnapshot Snapshot => _snapshot;
    public event EventHandler<RuntimeSnapshot>? SnapshotChanged;

    public bool CanExecute(UiAction action, string? targetId = null) =>
        !_busy && _binding.CanExecute(action, targetId);

    public string? DisabledReason(UiAction action, string? targetId = null) =>
        _busy ? "Another runtime command is still running." : _binding.DisabledReason(action, targetId);

    public async Task ExecuteAsync(UiAction action, string? targetId = null, CancellationToken cancellationToken = default)
    {
        if (!CanExecute(action, targetId))
            throw new InvalidOperationException(DisabledReason(action, targetId) ?? $"{action} is unavailable.");

        if (!await _commandGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Duplicate command invocation blocked while the previous command is still running.");

        _busy = true;
        var publish = false;
        try
        {
            await _binding.ExecuteAsync(action, targetId, cancellationToken).ConfigureAwait(false);
            _snapshot = await _binding.RefreshAsync(cancellationToken).ConfigureAwait(false);
            publish = true;
        }
        finally
        {
            _busy = false;
            _commandGate.Release();
        }

        if (publish)
            SnapshotChanged?.Invoke(this, _snapshot);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _snapshot = await _binding.RefreshAsync(cancellationToken).ConfigureAwait(false);
        SnapshotChanged?.Invoke(this, _snapshot);
    }

    public async ValueTask DisposeAsync()
    {
        await _binding.DisposeAsync().ConfigureAwait(false);
        _commandGate.Dispose();
    }
}
