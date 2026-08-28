using System.Windows.Input;

namespace PCCExecutive.App.Presentation;

public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => execute(parameter);
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class AsyncRelayCommand(
    Func<object?, CancellationToken, Task> execute,
    Func<object?, bool>? canExecute = null,
    Action<Exception>? onError = null) : ICommand, IDisposable
{
    private bool _isRunning;
    private CancellationTokenSource? _cts;

    public event EventHandler? CanExecuteChanged;
    public bool IsRunning => _isRunning;
    public bool CanExecute(object? parameter) => !_isRunning && (canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        _isRunning = true;
        _cts = new CancellationTokenSource();
        RaiseCanExecuteChanged();
        try { await execute(parameter, _cts.Token).ConfigureAwait(true); }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
        catch (Exception ex) { onError?.Invoke(ex); }
        finally
        {
            _cts.Dispose();
            _cts = null;
            _isRunning = false;
            RaiseCanExecuteChanged();
        }
    }

    public void Cancel() => _cts?.Cancel();
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    public void Dispose() { _cts?.Cancel(); _cts?.Dispose(); _cts = null; }
}
