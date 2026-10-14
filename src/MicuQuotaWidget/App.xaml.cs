using System.Net.Http;
using System.Windows;
using MicuQuotaWidget.Core.Services;
using MicuQuotaWidget.Services;
using MicuQuotaWidget.ViewModels;

namespace MicuQuotaWidget;

/// <summary>
/// Composes application-lifetime services so polling shares one HTTP transport and secrets never enter XAML or configuration files.
/// </summary>
public partial class App : System.Windows.Application, IDisposable
{
    private readonly HttpClient _httpClient = new(new HttpClientHandler
    {
        AllowAutoRedirect = false
    })
    {
        Timeout = System.Threading.Timeout.InfiniteTimeSpan
    };
    private MainWindow? _widgetWindow;
    private TrayIconService? _trayIconService;
    private bool _isDisposed;

    /// <summary>
    /// Initializes application resources for both normal WPF startup and STA-based UI tests that construct the app directly.
    /// </summary>
    public App()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Creates the main widget after the WPF dispatcher is available.
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var settingsStore = new JsonWidgetSettingsStore();
        var apiKeyStore = new WindowsCredentialStore();
        var clientFactory = new MicuBalanceClientFactory(_httpClient);
        var viewModel = new MainWindowViewModel(settingsStore, apiKeyStore, clientFactory);

        _widgetWindow = new MainWindow(viewModel, settingsStore, apiKeyStore);
        MainWindow = _widgetWindow;
        _trayIconService = new TrayIconService(
            ToggleWidgetFromTray,
            RefreshWidgetFromTray,
            ToggleTopmostFromTray,
            OpenSettingsFromTray,
            ExitFromTray);
        _widgetWindow.Show();
    }

    /// <summary>
    /// 在 WPF Dispatcher 上切换浮窗，保证通知区域回调不会跨线程更新窗口可见性。
    /// </summary>
    private void ToggleWidgetFromTray()
    {
        DispatchToWidget(widget => widget.ToggleVisibilityFromTray());
    }

    /// <summary>
    /// 在 WPF Dispatcher 上复用既有刷新命令，保持托盘和按钮的并发保护一致。
    /// </summary>
    private void RefreshWidgetFromTray()
    {
        DispatchToWidget(widget => widget.RefreshFromTray());
    }

    /// <summary>
    /// 在 WPF Dispatcher 上复用既有置顶命令，避免托盘菜单维护第二份窗口状态。
    /// </summary>
    private void ToggleTopmostFromTray()
    {
        DispatchToWidget(widget => widget.ToggleTopmostFromTray());
    }

    /// <summary>
    /// 从通知区域先恢复主窗口再打开设置，确保模态设置对话框始终具有可见 Owner。
    /// </summary>
    private void OpenSettingsFromTray()
    {
        DispatchToWidget(widget => widget.OpenSettingsFromTray());
    }

    /// <summary>
    /// 从唯一的显式退出入口关闭浮窗并结束 Application，避免普通关闭动作误退出后台托盘服务。
    /// </summary>
    private void ExitFromTray()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(ExitFromTray);
            return;
        }

        _trayIconService?.Dispose();
        _widgetWindow?.CloseForApplicationExit();
        Shutdown();
    }

    /// <summary>
    /// 安全调度托盘回调到仍存活的主窗口；应用退出期间静默忽略迟到的原生通知。
    /// </summary>
    private void DispatchToWidget(Action<MainWindow> action)
    {
        MainWindow? widgetWindow = _widgetWindow;

        if (_isDisposed || widgetWindow is null || widgetWindow.Dispatcher.HasShutdownStarted)
        {
            return;
        }

        _ = widgetWindow.Dispatcher.InvokeAsync(() => action(widgetWindow));
    }

    /// <summary>
    /// Disposes the shared transport only after every window has closed, preventing sockets from outliving the process.
    /// </summary>
    protected override void OnExit(ExitEventArgs e)
    {
        Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// Releases the application-owned HTTP transport when the WPF application reaches its terminal shutdown path.
    /// </summary>
    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _trayIconService?.Dispose();
        _trayIconService = null;
        _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }
}
