using System.Windows.Input;

namespace MicuQuotaWidget.Infrastructure;

/// <summary>
/// Exposes a synchronous view-model action through WPF commanding without moving keyboard handling into code-behind.
/// </summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    /// <summary>
    /// Creates a command with an optional availability predicate.
    /// </summary>
    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    /// <summary>
    /// Requests that WPF re-evaluate whether the command is currently available.
    /// </summary>
    public event EventHandler? CanExecuteChanged;

    /// <summary>
    /// Determines whether the command is available without depending on the command parameter.
    /// </summary>
    public bool CanExecute(object? parameter)
    {
        return _canExecute?.Invoke() ?? true;
    }

    /// <summary>
    /// Executes the synchronous action associated with a button or input binding.
    /// </summary>
    public void Execute(object? parameter)
    {
        _execute();
    }

    /// <summary>
    /// Signals WPF after view-model state changes may affect availability.
    /// </summary>
    public void NotifyCanExecuteChanged()
    {
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
