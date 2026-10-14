using System.Windows.Input;

namespace MicuQuotaWidget.Infrastructure;

/// <summary>
/// Runs one asynchronous UI action at a time so repeated refresh clicks cannot create overlapping view-model operations.
/// </summary>
public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _executeAsync;
    private readonly Func<bool>? _canExecute;
    private readonly Action<Exception>? _onUnexpectedFailure;
    private bool _isExecuting;

    /// <summary>
    /// Creates a command that serializes asynchronous execution, optionally reflects external availability, and reports unexpected failures to its owner.
    /// </summary>
    public AsyncRelayCommand(
        Func<Task> executeAsync,
        Func<bool>? canExecute = null,
        Action<Exception>? onUnexpectedFailure = null)
    {
        _executeAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
        _canExecute = canExecute;
        _onUnexpectedFailure = onUnexpectedFailure;
    }

    /// <summary>
    /// Requests that WPF re-evaluate the command after execution begins or ends.
    /// </summary>
    public event EventHandler? CanExecuteChanged;

    /// <summary>
    /// Allows a new execution only when the previous invocation has completed.
    /// </summary>
    public bool CanExecute(object? parameter)
    {
        return !_isExecuting && (_canExecute?.Invoke() ?? true);
    }

    /// <summary>
    /// Starts the asynchronous action and restores command availability even if it fails.
    /// </summary>
    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _isExecuting = true;
        NotifyCanExecuteChanged();

        try
        {
            await _executeAsync();
        }
        catch (OperationCanceledException)
        {
            // Cancellation is a normal lifecycle event for a closing widget and should not escape through async void.
        }
        catch (Exception exception)
        {
            _onUnexpectedFailure?.Invoke(exception);
        }
        finally
        {
            _isExecuting = false;
            NotifyCanExecuteChanged();
        }
    }

    /// <summary>
    /// Signals WPF after execution state changes.
    /// </summary>
    public void NotifyCanExecuteChanged()
    {
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
