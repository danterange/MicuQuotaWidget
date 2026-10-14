using System.Windows;
using System.Windows.Threading;

namespace MicuQuotaWidget.UiTests;

/// <summary>
/// Runs a WPF assertion on one dedicated STA thread because WPF controls cannot be created on a default test runner thread.
/// </summary>
internal static class StaTestThread
{
    private static readonly SemaphoreSlim ActionGate = new(initialCount: 1, maxCount: 1);
    private static readonly object ApplicationLock = new();
    private static readonly ManualResetEventSlim ApplicationReady = new(initialState: false);
    private static Dispatcher? _dispatcher;
    private static Exception? _startupException;
    private static Thread? _thread;

    /// <summary>
    /// 将每个 WPF 断言排队到同一个常驻 STA Application，避免多个测试在同一 AppDomain 中重复创建 Application。
    /// </summary>
    public static Task RunAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        EnsureApplicationThread();

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            _dispatcher!.BeginInvoke(new Action(() => _ = ExecuteAsync(action, completion)));
        }
        catch (Exception exception)
        {
            completion.SetException(exception);
        }

        return completion.Task;
    }

    /// <summary>
    /// 创建一次可复用的 STA Dispatcher 和 Application；测试进程结束时由后台线程自然退出，避免在单个测试后关闭全局 WPF Application。
    /// </summary>
    private static void EnsureApplicationThread()
    {
        lock (ApplicationLock)
        {
            if (_thread is null)
            {
                _thread = new Thread(InitializeApplicationThread)
                {
                    IsBackground = true,
                    Name = "MicuQuotaWidget.UiTest"
                };

                _thread.SetApartmentState(ApartmentState.STA);
                _thread.Start();
            }
        }

        if (!ApplicationReady.Wait(TimeSpan.FromSeconds(15)))
        {
            throw new TimeoutException("WPF UI 测试 Application 未能在 15 秒内初始化。");
        }

        if (_startupException is not null)
        {
            throw new InvalidOperationException("无法初始化 WPF UI 测试 Application。", _startupException);
        }
    }

    /// <summary>
    /// 在专用 STA 线程上初始化资源并运行 Dispatcher 消息循环，使后续窗口测试共享同一 WPF 应用上下文。
    /// </summary>
    private static void InitializeApplicationThread()
    {
        try
        {
            _ = new MicuQuotaWidget.App
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };
            _dispatcher = Dispatcher.CurrentDispatcher;
        }
        catch (Exception exception)
        {
            _startupException = exception;
        }
        finally
        {
            ApplicationReady.Set();
        }

        if (_startupException is null)
        {
            Dispatcher.Run();
        }
    }

    /// <summary>
    /// 串行执行单个测试委托并把异常传回调用方，避免异步测试在共享 Dispatcher 上交错改变窗口状态。
    /// </summary>
    private static async Task ExecuteAsync(Func<Task> action, TaskCompletionSource completion)
    {
        await ActionGate.WaitAsync();

        try
        {
            await action();
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
        finally
        {
            ActionGate.Release();
        }
    }
}
