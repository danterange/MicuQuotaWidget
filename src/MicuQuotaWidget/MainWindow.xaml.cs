using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MicuQuotaWidget.Services;
using MicuQuotaWidget.ViewModels;

namespace MicuQuotaWidget;

/// <summary>
/// Hosts the compact draggable widget shell while keeping balance state and network work in its view model.
/// </summary>
public partial class MainWindow : Window
{
    private readonly IApiKeyStore _apiKeyStore;
    private readonly IWidgetSettingsStore _settingsStore;
    private readonly MainWindowViewModel _viewModel;
    private bool _isApplicationExitRequested;
    private SettingsWindow? _settingsWindow;

    /// <summary>
    /// Creates the main window with explicitly injected local services so UI tests can avoid real credentials and networking.
    /// </summary>
    public MainWindow(
        MainWindowViewModel viewModel,
        IWidgetSettingsStore settingsStore,
        IApiKeyStore apiKeyStore)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _apiKeyStore = apiKeyStore ?? throw new ArgumentNullException(nameof(apiKeyStore));
        DataContext = _viewModel;

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
    }

    /// <summary>
    /// Starts the first balance request only after WPF has created the window and its dispatcher.
    /// </summary>
    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.StartAsync();
        }
        catch (ObjectDisposedException)
        {
            // A rapid open-close can dispose the view model before the Loaded continuation reaches the polling startup path.
        }
    }

    /// <summary>
    /// Stops polling before WPF destroys the window so an in-flight query cannot update a closed UI.
    /// </summary>
    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _viewModel.Dispose();
    }

    /// <summary>
    /// 将普通关闭请求转换为隐藏到通知区域；只有应用的显式退出路径才能释放轮询和窗口资源。
    /// </summary>
    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_isApplicationExitRequested)
        {
            return;
        }

        e.Cancel = true;
        HideToTray();
    }

    /// <summary>
    /// 由托盘菜单或双击图标切换浮窗，恢复时确保窗口回到正常状态并获得键盘焦点。
    /// </summary>
    public void ToggleVisibilityFromTray()
    {
        if (_isApplicationExitRequested)
        {
            return;
        }

        if (IsVisible)
        {
            HideToTray();
            return;
        }

        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>
    /// 由托盘菜单复用刷新命令，保留 ViewModel 对重复刷新和异常反馈的统一处理。
    /// </summary>
    public void RefreshFromTray()
    {
        if (!_isApplicationExitRequested && _viewModel.RefreshCommand.CanExecute(null))
        {
            _viewModel.RefreshCommand.Execute(null);
        }
    }

    /// <summary>
    /// 由托盘菜单复用既有置顶命令，避免维护与 ToggleButton 脱节的第二份状态。
    /// </summary>
    public void ToggleTopmostFromTray()
    {
        if (!_isApplicationExitRequested && _viewModel.ToggleTopmostCommand.CanExecute(null))
        {
            _viewModel.ToggleTopmostCommand.Execute(null);
        }
    }

    /// <summary>
    /// 从托盘进入设置前恢复主窗口，并复用单一设置对话框以防止嵌套模态窗口。
    /// </summary>
    public void OpenSettingsFromTray()
    {
        if (_isApplicationExitRequested)
        {
            return;
        }

        if (!IsVisible)
        {
            Show();
            WindowState = WindowState.Normal;
        }

        Activate();
        OpenSettings();
    }

    /// <summary>
    /// 标记受控退出后关闭窗口，使 Closing 处理器不再将退出请求改写为隐藏动作。
    /// </summary>
    public void CloseForApplicationExit()
    {
        if (_isApplicationExitRequested)
        {
            return;
        }

        _isApplicationExitRequested = true;
        Close();
    }

    /// <summary>
    /// 从可见设置按钮打开配置对话框，不让 API Key 处理进入主余额卡片。
    /// </summary>
    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        OpenSettings();
    }

    /// <summary>
    /// 在主窗口拥有者下只显示一个设置对话框，使托盘和卡片入口共享同一模态与凭据边界。
    /// </summary>
    private void OpenSettings()
    {
        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.Activate();
            return;
        }

        var window = new SettingsWindow(_settingsStore, _apiKeyStore, _viewModel)
        {
            Owner = this
        };

        _settingsWindow = window;

        try
        {
            window.ShowDialog();
        }
        finally
        {
            _settingsWindow = null;
        }
    }

    /// <summary>
    /// Moves the borderless widget only from non-interactive header space so buttons remain reliably clickable.
    /// </summary>
    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || IsInteractiveElement(e.OriginalSource as DependencyObject))
        {
            return;
        }

        DragMove();
    }

    /// <summary>
    /// 将可见的关闭图标变为隐藏到通知区域，避免用户误以为关闭浮窗会终止托盘服务。
    /// </summary>
    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        HideToTray();
    }

    /// <summary>
    /// 隐藏当前浮窗但保留 Application、轮询和通知区域入口，使余额服务继续驻留后台。
    /// </summary>
    private void HideToTray()
    {
        if (IsVisible)
        {
            Hide();
        }
    }

    /// <summary>
    /// Detects controls that own mouse input so the drag behavior does not steal clicks from an accessible interaction.
    /// </summary>
    private static bool IsInteractiveElement(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is System.Windows.Controls.Primitives.ButtonBase
                or System.Windows.Controls.Primitives.TextBoxBase
                or PasswordBox
                or System.Windows.Controls.ComboBox)
            {
                return true;
            }

            element = VisualTreeHelper.GetParent(element);
        }

        return false;
    }
}
