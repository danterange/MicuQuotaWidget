using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MicuQuotaWidget;
using MicuQuotaWidget.Core.Models;
using MicuQuotaWidget.Core.Services;
using MicuQuotaWidget.Services;
using MicuQuotaWidget.ViewModels;

namespace MicuQuotaWidget.UiTests;

/// <summary>
/// 验证设置对话框在真实 WPF 布局中始终显示关键输入、校验提示和底部操作，不依赖真实凭据或网络。
/// </summary>
public sealed class SettingsWindowUiTests
{
    /// <summary>
    /// 验证默认和较长错误提示下的设置窗口不会把保存操作裁切到可见区域外。
    /// </summary>
    [Fact]
    public async Task SettingsWindowKeepsConfigurationControlsVisibleWithoutClipping()
    {
        await StaTestThread.RunAsync(() =>
        {
            var settingsStore = new FakeSettingsStore();
            var apiKeyStore = new FakeApiKeyStore();
            using var viewModel = new MainWindowViewModel(
                settingsStore,
                apiKeyStore,
                new FakeBalanceClientFactory());
            var window = new SettingsWindow(settingsStore, apiKeyStore, viewModel);

            try
            {
                window.Show();
                window.ApplyTemplate();
                window.UpdateLayout();

                var contentRoot = Assert.IsType<Grid>(window.Content);
                var apiKeyInput = FindDescendant<PasswordBox>(window, control => AutomationProperties.GetName(control) == "Micu API Key");
                var cancelButton = FindDescendant<Button>(window, control => AutomationProperties.GetName(control) == "取消设置");
                var saveButton = FindDescendant<Button>(window, control => AutomationProperties.GetName(control) == "保存 Micu 设置并刷新余额");
                var validationText = FindDescendant<TextBlock>(window, control => AutomationProperties.GetLiveSetting(control) == AutomationLiveSetting.Polite);
                var formScroller = Assert.IsType<ScrollViewer>(window.FindName("SettingsScrollViewer"));

                Assert.NotNull(apiKeyInput);
                Assert.NotNull(cancelButton);
                Assert.NotNull(saveButton);
                Assert.NotNull(validationText);
                Assert.Equal(SizeToContent.Height, window.SizeToContent);
                Assert.Equal(ResizeMode.CanResize, window.ResizeMode);
                Assert.False(window.ShowInTaskbar);
                Assert.Equal(ScrollBarVisibility.Auto, formScroller.VerticalScrollBarVisibility);
                Assert.Equal(ScrollBarVisibility.Disabled, formScroller.HorizontalScrollBarVisibility);
                Assert.True(cancelButton!.IsCancel);
                Assert.True(saveButton!.IsDefault);

                validationText.Text = "API Key 保存失败，且无法自动还原查询方式；请重新打开设置确认当前方式后重试。";
                window.UpdateLayout();

                AssertControlIsFullyVisible(contentRoot, apiKeyInput!);
                AssertControlIsFullyVisible(contentRoot, cancelButton!);
                AssertControlIsFullyVisible(contentRoot, saveButton!);
                AssertControlIsFullyVisible(contentRoot, validationText!);
                CaptureIfRequested(window);
            }
            finally
            {
                window.Close();
            }

            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// 在当前窗口视觉树中查找满足条件的控件，使布局测试不依赖内部 x:Name。
    /// </summary>
    private static T? FindDescendant<T>(DependencyObject parent, Func<T, bool> predicate)
        where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);

            if (child is T typedChild && predicate(typedChild))
            {
                return typedChild;
            }

            T? nestedMatch = FindDescendant(child, predicate);

            if (nestedMatch is not null)
            {
                return nestedMatch;
            }
        }

        return null;
    }

    /// <summary>
    /// 断言一个必须操作的控件在 WPF 客户区根布局内具有完整可点击边界，而非仅存在于被裁切的视觉树中。
    /// </summary>
    private static void AssertControlIsFullyVisible(FrameworkElement contentRoot, FrameworkElement control)
    {
        Assert.True(control.IsVisible, $"{AutomationProperties.GetName(control)} 必须可见。");
        Assert.True(control.ActualWidth > 0d && control.ActualHeight > 0d, $"{AutomationProperties.GetName(control)} 必须具有可交互尺寸。");
        Rect bounds = control.TransformToAncestor(contentRoot).TransformBounds(new Rect(control.RenderSize));

        Assert.True(bounds.Left >= -0.5d && bounds.Right <= contentRoot.ActualWidth + 0.5d, $"{AutomationProperties.GetName(control)} 不得横向裁切。");
        Assert.True(bounds.Top >= -0.5d && bounds.Bottom <= contentRoot.ActualHeight + 0.5d, $"{AutomationProperties.GetName(control)} 不得纵向裁切。");
    }

    /// <summary>
    /// 在质量门禁请求时渲染不含 API Key 的设置对话框截图，供人工核验完整性和中文排版。
    /// </summary>
    private static void CaptureIfRequested(Window window)
    {
        string? capturePath = Environment.GetEnvironmentVariable("MICU_SETTINGS_CAPTURE_PATH");

        if (string.IsNullOrWhiteSpace(capturePath))
        {
            return;
        }

        string? directoryPath = System.IO.Path.GetDirectoryName(capturePath);

        if (!string.IsNullOrWhiteSpace(directoryPath))
        {
            System.IO.Directory.CreateDirectory(directoryPath);
        }

        window.UpdateLayout();

        int pixelWidth = Math.Max(1, (int)Math.Ceiling(window.ActualWidth));
        int pixelHeight = Math.Max(1, (int)Math.Ceiling(window.ActualHeight));
        var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var stream = System.IO.File.Create(capturePath);
        encoder.Save(stream);

        Assert.True(new System.IO.FileInfo(capturePath).Length > 0, "设置窗口截图必须包含渲染像素。");
    }

    /// <summary>
    /// 为设置窗口提供只在内存中保存的查询方式，避免布局测试读取或写入用户文件。
    /// </summary>
    private sealed class FakeSettingsStore : IWidgetSettingsStore
    {
        /// <summary>
        /// 返回稳定的默认查询方式，使窗口初始化不依赖磁盘。
        /// </summary>
        public Task<WidgetSettings> LoadAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new WidgetSettings(MicuEndpointKind.MicuApi));
        }

        /// <summary>
        /// 接受测试中的保存请求而不产生本地副作用。
        /// </summary>
        public Task SaveAsync(WidgetSettings settings, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// 表示首次配置尚未保存 Key 的安全测试状态，不接触 Windows Credential Manager。
    /// </summary>
    private sealed class FakeApiKeyStore : IApiKeyStore
    {
        /// <summary>
        /// 始终返回空值，以覆盖首次配置时显示的帮助文案。
        /// </summary>
        public string? ReadApiKey()
        {
            return null;
        }

        /// <summary>
        /// 忽略测试 Key，确保 UI 测试不会把模拟值持久化。
        /// </summary>
        public void SaveApiKey(string apiKey)
        {
        }

        /// <summary>
        /// 提供无副作用删除路径以满足接口契约。
        /// </summary>
        public void DeleteApiKey()
        {
        }
    }

    /// <summary>
    /// 构造不会发起网络请求的客户端，保障窗口生命周期变化时测试仍离线可运行。
    /// </summary>
    private sealed class FakeBalanceClientFactory : IMicuBalanceClientFactory
    {
        /// <summary>
        /// 返回固定余额客户端；设置布局本身不调用该客户端。
        /// </summary>
        public IMicuBalanceClient Create(MicuEndpointKind endpointKind, string apiKey)
        {
            return new FakeBalanceClient();
        }
    }

    /// <summary>
    /// 提供确定性余额快照，避免测试依赖真实 Micu 服务。
    /// </summary>
    private sealed class FakeBalanceClient : IMicuBalanceClient
    {
        /// <summary>
        /// 返回演示快照以满足潜在的 ViewModel 初始化请求。
        /// </summary>
        public Task<MicuBalanceSnapshot> GetBalanceAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(new MicuBalanceSnapshot(12.34m, "USD", DateTimeOffset.UtcNow, "Demo"));
        }
    }
}
