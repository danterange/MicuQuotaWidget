using System.Windows;
using System.Windows.Threading;

namespace MicuQuotaWidget.UiTests;

/// <summary>
/// Runs a WPF assertion on one dedicated STA thread because WPF controls cannot be created on a default test runner thread.
/// </summary>
internal static class StaTestThread
{
    /// <summary>
    /// Creates a temporary WPF application context, executes the assertion, and preserves any failure for the async test caller.
    /// </summary>
    public static Task RunAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => _ = ExecuteAsync(action, completion))
        {
            IsBackground = true,
            Name = "MicuQuotaWidget.UiTest"
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return completion.Task;
    }

    /// <summary>
    /// Owns the STA application lifetime and forwards the original exception to xUnit without a dispatcher loop leak.
    /// </summary>
    private static async Task ExecuteAsync(Func<Task> action, TaskCompletionSource completion)
    {
        Application? application = null;

        try
        {
            application = new MicuQuotaWidget.App
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };
            await action();
            completion.SetResult();
        }
        catch (Exception exception)
        {
            completion.SetException(exception);
        }
        finally
        {
            application?.Shutdown();
            Dispatcher.CurrentDispatcher.InvokeShutdown();
        }
    }
}
