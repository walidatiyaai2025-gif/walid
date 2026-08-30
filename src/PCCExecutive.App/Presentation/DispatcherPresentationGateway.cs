using System.Windows.Threading;

namespace PCCExecutive.App.Presentation;

/// <summary>
/// Marshals runtime snapshot notifications back to the WPF dispatcher.
/// The integrated runtime deliberately uses ConfigureAwait(false); WPF bindings must never
/// observe those notifications on a worker thread.
/// </summary>
public sealed class DispatcherPresentationGateway : IPccExecutivePresentationGateway, IDisposable
{
    private readonly IPccExecutivePresentationGateway _inner;
    private readonly Dispatcher _dispatcher;
    private bool _disposed;

    public DispatcherPresentationGateway(IPccExecutivePresentationGateway inner, Dispatcher dispatcher)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _inner.SnapshotChanged += OnInnerSnapshotChanged;
    }

    public RuntimeSnapshot Snapshot => _inner.Snapshot;

    public event EventHandler<RuntimeSnapshot>? SnapshotChanged;

    public bool CanExecute(UiAction action, string? targetId = null) =>
        _inner.CanExecute(action, targetId);

    public Task ExecuteAsync(UiAction action, string? targetId = null, CancellationToken cancellationToken = default) =>
        _inner.ExecuteAsync(action, targetId, cancellationToken);

    private void OnInnerSnapshotChanged(object? sender, RuntimeSnapshot snapshot)
    {
        if (_disposed) return;

        if (_dispatcher.CheckAccess())
        {
            SnapshotChanged?.Invoke(this, snapshot);
            return;
        }

        _ = _dispatcher.BeginInvoke(
            DispatcherPriority.DataBind,
            new Action(() =>
            {
                if (!_disposed) SnapshotChanged?.Invoke(this, snapshot);
            }));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _inner.SnapshotChanged -= OnInnerSnapshotChanged;
    }
}
