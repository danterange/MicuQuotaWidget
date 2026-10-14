using System.Windows;
using System.Windows.Controls;
using MicuQuotaWidget.Core.Models;
using MicuQuotaWidget.Services;
using MicuQuotaWidget.ViewModels;

namespace MicuQuotaWidget;

/// <summary>
/// Collects the small amount of user configuration required to query a balance without exposing the saved key in the UI.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly IApiKeyStore _apiKeyStore;
    private readonly SettingsSaveCoordinator _saveCoordinator;
    private readonly IWidgetSettingsStore _settingsStore;
    private readonly MainWindowViewModel _mainViewModel;

    /// <summary>
    /// Creates a settings dialog using the same injected stores as the main widget.
    /// </summary>
    public SettingsWindow(
        IWidgetSettingsStore settingsStore,
        IApiKeyStore apiKeyStore,
        MainWindowViewModel mainViewModel)
    {
        InitializeComponent();
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _apiKeyStore = apiKeyStore ?? throw new ArgumentNullException(nameof(apiKeyStore));
        _saveCoordinator = new SettingsSaveCoordinator(_settingsStore, _apiKeyStore);
        _mainViewModel = mainViewModel ?? throw new ArgumentNullException(nameof(mainViewModel));

        Loaded += SettingsWindow_Loaded;
    }

    /// <summary>
    /// Loads only the endpoint mode; the stored API key is deliberately never copied back into the password field.
    /// </summary>
    private async void SettingsWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            WidgetSettings settings = await _settingsStore.LoadAsync();
            EndpointKindComboBox.SelectedIndex = settings.EndpointKind == MicuEndpointKind.MicuSubscription ? 1 : 0;
            ValidationTextBlock.Text = _apiKeyStore.ReadApiKey() is null ? "尚未保存 API Key。" : "已保存 API Key；留空即可保留。";
        }
        catch
        {
            ValidationTextBlock.Text = "无法读取本地设置；重新保存后可修复该配置。";
        }
    }

    /// <summary>
    /// Validates the minimum configuration, persists non-secret and secret data separately, then immediately refreshes the widget.
    /// </summary>
    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string? apiKey = string.IsNullOrWhiteSpace(ApiKeyPasswordBox.Password) ? null : ApiKeyPasswordBox.Password;
            SettingsSaveOutcome outcome = await _saveCoordinator.SaveAsync(new WidgetSettings(GetSelectedEndpointKind()), apiKey);

            switch (outcome)
            {
                case SettingsSaveOutcome.Saved:
                    await _mainViewModel.RefreshAfterSettingsChangedAsync();
                    DialogResult = true;
                    return;

                case SettingsSaveOutcome.MissingApiKey:
                    ValidationTextBlock.Text = "首次使用请填写 API Key。";
                    ApiKeyPasswordBox.Focus();
                    return;

                case SettingsSaveOutcome.CredentialSaveFailedRolledBack:
                    ValidationTextBlock.Text = "API Key 保存失败，查询方式已还原；请检查 Windows 凭据权限后重试。";
                    return;

                case SettingsSaveOutcome.CredentialSaveFailedAfterRecovery:
                    ValidationTextBlock.Text = "本地设置已重建，但 API Key 尚未保存；请检查 Windows 凭据权限后重试。";
                    return;

                case SettingsSaveOutcome.CredentialSaveFailedRollbackFailed:
                    ValidationTextBlock.Text = "API Key 保存失败，且无法自动还原查询方式；请重新打开设置确认当前方式后重试。";
                    return;

                case SettingsSaveOutcome.EndpointSaveFailed:
                    ValidationTextBlock.Text = "查询方式保存失败，请检查本地设置后重试。";
                    return;

                default:
                    throw new InvalidOperationException("未知的设置保存结果。");
            }
        }
        catch
        {
            ValidationTextBlock.Text = "保存失败，请检查 Windows 凭据权限或本地设置后重试。";
        }
    }

    /// <summary>
    /// Closes the dialog without mutating endpoint mode or the current Windows credential.
    /// </summary>
    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    /// <summary>
    /// Maps the selected UI item to a closed enum so users cannot direct the client to arbitrary hosts.
    /// </summary>
    private MicuEndpointKind GetSelectedEndpointKind()
    {
        return (EndpointKindComboBox.SelectedItem as ComboBoxItem)?.Tag as string == "MicuSubscription"
            ? MicuEndpointKind.MicuSubscription
            : MicuEndpointKind.MicuApi;
    }
}
