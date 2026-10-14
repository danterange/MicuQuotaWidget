using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace MicuQuotaWidget.Services;

/// <summary>
/// 在 Windows 通知区域托管小组件入口，提供显示、刷新、设置、置顶和显式退出操作而不依赖第三方托盘库。
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly Action _exitApplication;
    private readonly Icon _icon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Action _openSettings;
    private readonly Action _refreshWidget;
    private readonly Action _toggleTopmost;
    private readonly Action _toggleWidget;
    private bool _isDisposed;

    /// <summary>
    /// 创建可见的通知区域图标，并将菜单行为交由应用层回调以保持窗口生命周期集中管理。
    /// </summary>
    public TrayIconService(
        Action toggleWidget,
        Action refreshWidget,
        Action toggleTopmost,
        Action openSettings,
        Action exitApplication)
    {
        _toggleWidget = toggleWidget ?? throw new ArgumentNullException(nameof(toggleWidget));
        _refreshWidget = refreshWidget ?? throw new ArgumentNullException(nameof(refreshWidget));
        _toggleTopmost = toggleTopmost ?? throw new ArgumentNullException(nameof(toggleTopmost));
        _openSettings = openSettings ?? throw new ArgumentNullException(nameof(openSettings));
        _exitApplication = exitApplication ?? throw new ArgumentNullException(nameof(exitApplication));

        _menu = new Forms.ContextMenuStrip();
        _menu.Items.Add("显示 / 隐藏小组件", null, ToggleMenuItem_Click);
        _menu.Items.Add("立即刷新余额", null, RefreshMenuItem_Click);
        _menu.Items.Add("切换窗口置顶", null, ToggleTopmostMenuItem_Click);
        _menu.Items.Add("打开设置", null, OpenSettingsMenuItem_Click);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add("退出 MicuQuotaWidget", null, ExitMenuItem_Click);

        _icon = CreateTrayIcon();
        _notifyIcon = new Forms.NotifyIcon
        {
            ContextMenuStrip = _menu,
            Icon = _icon,
            Text = "Micu 余额小组件",
            Visible = true
        };
        _notifyIcon.DoubleClick += NotifyIcon_DoubleClick;
    }

    /// <summary>
    /// 释放原生托盘句柄和菜单资源，使应用退出后不会留下失效的通知区域图标。
    /// </summary>
    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _notifyIcon.DoubleClick -= NotifyIcon_DoubleClick;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _icon.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 切换主窗口的可见性，双击图标与菜单入口保持同一行为。
    /// </summary>
    private void NotifyIcon_DoubleClick(object? sender, EventArgs e)
    {
        ExecuteTrayAction(_toggleWidget);
    }

    /// <summary>
    /// 从上下文菜单切换主窗口，避免用户只能通过双击发现恢复入口。
    /// </summary>
    private void ToggleMenuItem_Click(object? sender, EventArgs e)
    {
        ExecuteTrayAction(_toggleWidget);
    }

    /// <summary>
    /// 从上下文菜单触发既有刷新命令，不复制余额查询逻辑或密钥处理。
    /// </summary>
    private void RefreshMenuItem_Click(object? sender, EventArgs e)
    {
        ExecuteTrayAction(_refreshWidget);
    }

    /// <summary>
    /// 从上下文菜单复用既有置顶切换命令，保持鼠标和键盘入口的一致性。
    /// </summary>
    private void ToggleTopmostMenuItem_Click(object? sender, EventArgs e)
    {
        ExecuteTrayAction(_toggleTopmost);
    }

    /// <summary>
    /// 先恢复浮窗再打开设置，确保设置对话框始终拥有可见的 WPF Owner。
    /// </summary>
    private void OpenSettingsMenuItem_Click(object? sender, EventArgs e)
    {
        ExecuteTrayAction(_openSettings);
    }

    /// <summary>
    /// 通过明确的菜单命令请求应用退出，避免关闭浮窗时意外终止后台托盘服务。
    /// </summary>
    private void ExitMenuItem_Click(object? sender, EventArgs e)
    {
        ExecuteTrayAction(_exitApplication);
    }

    /// <summary>
    /// 忽略 Dispose 之后可能迟到的原生通知，防止退出过程重新访问已释放的窗口状态。
    /// </summary>
    private void ExecuteTrayAction(Action action)
    {
        if (!_isDisposed)
        {
            action();
        }
    }

    /// <summary>
    /// 生成高对比度的绿色几何 Micu 标记，并克隆为托盘可安全持有的独立 Icon。
    /// </summary>
    private static Icon CreateTrayIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);

        using var background = new SolidBrush(Color.FromArgb(5, 150, 105));
        using var border = new Pen(Color.FromArgb(6, 95, 70), 1.5f);
        using var mark = new Pen(Color.White, 3f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };

        graphics.FillEllipse(background, 2, 2, 28, 28);
        graphics.DrawEllipse(border, 2.75f, 2.75f, 26.5f, 26.5f);
        graphics.DrawLines(mark, new[]
        {
            new PointF(8.5f, 23f),
            new PointF(12f, 9f),
            new PointF(16f, 17f),
            new PointF(20f, 9f),
            new PointF(23.5f, 23f)
        });

        IntPtr iconHandle = bitmap.GetHicon();

        try
        {
            using Icon icon = Icon.FromHandle(iconHandle);
            return (Icon)icon.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(iconHandle);
        }
    }

    /// <summary>
    /// 包装调用方拥有的 HICON 释放逻辑，避免每次启动动态生成托盘图标时泄漏 GDI 资源。
    /// </summary>
    private static class NativeMethods
    {
        /// <summary>
        /// 销毁调用方拥有的 HICON；从句柄克隆 Icon 后不再需要保留原始 Win32 资源。
        /// </summary>
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyIcon(IntPtr iconHandle);
    }
}
