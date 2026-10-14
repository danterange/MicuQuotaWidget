using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MicuQuotaWidget;
using MicuQuotaWidget.Core.Models;
using MicuQuotaWidget.Core.Services;
using MicuQuotaWidget.Services;
using MicuQuotaWidget.ViewModels;

namespace MicuQuotaWidget.UiTests;

/// <summary>
/// Exercises the WPF shell on an STA thread without requiring a real Micu account, credential, or network response.
/// </summary>
public sealed class MainWindowUiTests
{
    /// <summary>
    /// 验证缩小后的主窗口不出现在任务栏，并保留置顶、键盘命令和可访问控件。
    /// </summary>
    [Fact]
    public async Task MainWindowUsesCompactAccessibleAlwaysOnTopShell()
    {
        await StaTestThread.RunAsync(async () =>
        {
            var viewModel = new MainWindowViewModel(
                new FakeSettingsStore(),
                new FakeApiKeyStore(),
                new FakeBalanceClientFactory());
            var window = new MainWindow(viewModel, new FakeSettingsStore(), new FakeApiKeyStore());

            try
            {
                await viewModel.RefreshAsync();
                window.Show();
                window.ApplyTemplate();
                window.UpdateLayout();

                Assert.Equal(302d, window.Width);
                Assert.Equal(184d, window.Height);
                Assert.True(window.Topmost);
                Assert.False(window.ShowInTaskbar);
                var contentRoot = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
                var refreshButton = Assert.IsType<System.Windows.Controls.Button>(window.FindName("RefreshButton"));
                var settingsButton = Assert.IsType<System.Windows.Controls.Button>(window.FindName("SettingsButton"));
                var hideButton = Assert.IsType<System.Windows.Controls.Button>(window.FindName("HideButton"));

                var topmostToggle = Assert.IsType<System.Windows.Controls.Primitives.ToggleButton>(window.FindName("TopmostToggle"));
                Assert.Equal("切换窗口置顶", AutomationProperties.GetName(topmostToggle));
                Assert.Equal("隐藏到通知区域", AutomationProperties.GetName(hideButton));
                AssertControlFitsContent(contentRoot, topmostToggle);
                AssertControlFitsContent(contentRoot, hideButton);
                AssertControlFitsContent(contentRoot, settingsButton);
                AssertControlFitsContent(contentRoot, refreshButton);
                Assert.Contains(window.InputBindings.OfType<KeyBinding>(), binding => binding.Key == Key.R && binding.Modifiers == ModifierKeys.Control);
                Assert.Contains(window.InputBindings.OfType<KeyBinding>(), binding => binding.Key == Key.T && binding.Modifiers == ModifierKeys.Control);
                CaptureIfRequested(window);
            }
            finally
            {
                viewModel.Dispose();
                window.CloseForApplicationExit();
            }
        });
    }

    /// <summary>
    /// 验证关闭主卡片只会隐藏到通知区域，托盘操作仍能恢复窗口，而显式退出才真正关闭窗口。
    /// </summary>
    [Fact]
    public async Task MainWindowHidesToTrayAndRestoresWithoutShowingInTaskbar()
    {
        await StaTestThread.RunAsync(() =>
        {
            var viewModel = new MainWindowViewModel(
                new FakeSettingsStore(),
                new FakeApiKeyStore(),
                new FakeBalanceClientFactory());
            var window = new MainWindow(viewModel, new FakeSettingsStore(), new FakeApiKeyStore());
            var hasClosedForExit = false;

            try
            {
                window.Show();
                window.UpdateLayout();
                var hideButton = Assert.IsType<System.Windows.Controls.Button>(window.FindName("HideButton"));

                hideButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

                Assert.False(window.IsVisible);
                Assert.False(window.ShowInTaskbar);

                window.ToggleVisibilityFromTray();

                Assert.True(window.IsVisible);
                Assert.False(window.ShowInTaskbar);

                window.Close();

                Assert.False(window.IsVisible);

                window.ToggleVisibilityFromTray();

                Assert.True(window.IsVisible);

                bool initialTopmost = window.Topmost;
                window.ToggleTopmostFromTray();

                Assert.NotEqual(initialTopmost, window.Topmost);

                window.CloseForApplicationExit();
                hasClosedForExit = true;
            }
            finally
            {
                if (!hasClosedForExit)
                {
                    window.CloseForApplicationExit();
                }

                viewModel.Dispose();
            }

            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// 验证缩小后的关键控件仍完整位于 WPF 客户区内，防止尺寸回归造成不可点击或被裁切的操作。
    /// </summary>
    private static void AssertControlFitsContent(FrameworkElement contentRoot, FrameworkElement control)
    {
        Assert.True(control.ActualWidth > 0d && control.ActualHeight > 0d, $"{AutomationProperties.GetName(control)} 必须具有可交互尺寸。");
        Rect bounds = control.TransformToAncestor(contentRoot).TransformBounds(new Rect(control.RenderSize));

        Assert.True(bounds.Left >= -0.5d && bounds.Right <= contentRoot.ActualWidth + 0.5d, $"{AutomationProperties.GetName(control)} 不得横向裁切。");
        Assert.True(bounds.Top >= -0.5d && bounds.Bottom <= contentRoot.ActualHeight + 0.5d, $"{AutomationProperties.GetName(control)} 不得纵向裁切。");
    }

    /// <summary>
    /// Renders a deterministic demonstration widget when the quality-gate script requests a screenshot for human visual review.
    /// </summary>
    private static void CaptureIfRequested(MainWindow window)
    {
        string? capturePath = Environment.GetEnvironmentVariable("MICU_WIDGET_CAPTURE_PATH");

        if (string.IsNullOrWhiteSpace(capturePath))
        {
            return;
        }

        string? directoryPath = System.IO.Path.GetDirectoryName(capturePath);

        if (!string.IsNullOrWhiteSpace(directoryPath))
        {
            System.IO.Directory.CreateDirectory(directoryPath);
        }

        if (!window.IsVisible)
        {
            window.Show();
        }
        window.UpdateLayout();

        int pixelWidth = Math.Max(1, (int)Math.Ceiling(window.ActualWidth));
        int pixelHeight = Math.Max(1, (int)Math.Ceiling(window.ActualHeight));
        var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using (System.IO.FileStream stream = System.IO.File.Create(capturePath))
        {
            encoder.Save(stream);
        }

        Assert.True(new System.IO.FileInfo(capturePath).Length > 0, "UI smoke screenshot must contain rendered pixels.");
    }

    /// <summary>
    /// Supplies a safe in-memory endpoint mode so the UI test never creates or reads a local settings file.
    /// </summary>
    private sealed class FakeSettingsStore : IWidgetSettingsStore
    {
        /// <summary>
        /// Returns a deterministic default mode for UI construction.
        /// </summary>
        public Task<WidgetSettings> LoadAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new WidgetSettings(MicuEndpointKind.MicuApi));
        }

        /// <summary>
        /// Accepts saves in memory because this UI test verifies layout rather than local storage.
        /// </summary>
        public Task SaveAsync(WidgetSettings settings, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Simulates a demo-only key store so screenshot capture never touches Windows Credential Manager.
    /// </summary>
    private sealed class FakeApiKeyStore : IApiKeyStore
    {
        /// <summary>
        /// Returns a synthetic key that is consumed only by the in-memory fake client.
        /// </summary>
        public string? ReadApiKey()
        {
            return "demo-key";
        }

        /// <summary>
        /// Accepts a key in the test double without persisting it.
        /// </summary>
        public void SaveApiKey(string apiKey)
        {
        }

        /// <summary>
        /// Completes a no-op delete for the test double.
        /// </summary>
        public void DeleteApiKey()
        {
        }
    }

    /// <summary>
    /// Provides an unused fake client factory to keep the main window composition independent of network behavior.
    /// </summary>
    private sealed class FakeBalanceClientFactory : IMicuBalanceClientFactory
    {
        /// <summary>
        /// Creates a fake client in case a future UI lifecycle change triggers a refresh during construction.
        /// </summary>
        public IMicuBalanceClient Create(MicuEndpointKind endpointKind, string apiKey)
        {
            return new FakeBalanceClient();
        }
    }

    /// <summary>
    /// Produces a deterministic demonstration balance without contacting a provider.
    /// </summary>
    private sealed class FakeBalanceClient : IMicuBalanceClient
    {
        /// <summary>
        /// Returns a fixed snapshot suitable for UI lifecycle tests.
        /// </summary>
        public Task<MicuBalanceSnapshot> GetBalanceAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(new MicuBalanceSnapshot(12.34m, "USD", DateTimeOffset.UtcNow, "Demo"));
        }
    }
}
