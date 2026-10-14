namespace MicuQuotaWidget.Services;

/// <summary>
/// Coordinates the separately stored endpoint mode and API key so the settings dialog can give an exact recovery message for partial failures.
/// </summary>
public sealed class SettingsSaveCoordinator
{
    private readonly IApiKeyStore _apiKeyStore;
    private readonly IWidgetSettingsStore _settingsStore;

    /// <summary>
    /// Creates a coordinator backed by the same local settings and credential stores used by the widget.
    /// </summary>
    public SettingsSaveCoordinator(IWidgetSettingsStore settingsStore, IApiKeyStore apiKeyStore)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _apiKeyStore = apiKeyStore ?? throw new ArgumentNullException(nameof(apiKeyStore));
    }

    /// <summary>
    /// Saves the requested endpoint mode and optional new key, restoring a prior readable mode when credential persistence fails.
    /// A malformed settings file is intentionally replaceable so the UI's recovery instruction is actionable.
    /// </summary>
    public async Task<SettingsSaveOutcome> SaveAsync(WidgetSettings requestedSettings, string? newApiKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestedSettings);

        if (string.IsNullOrWhiteSpace(newApiKey) && _apiKeyStore.ReadApiKey() is null)
        {
            return SettingsSaveOutcome.MissingApiKey;
        }

        WidgetSettings? previousSettings = null;

        try
        {
            previousSettings = await _settingsStore.LoadAsync(cancellationToken);
        }
        catch (InvalidOperationException)
        {
            // JsonWidgetSettingsStore reserves InvalidOperationException for a damaged or unsupported non-secret settings file.
        }

        try
        {
            await _settingsStore.SaveAsync(requestedSettings, cancellationToken);
        }
        catch
        {
            return SettingsSaveOutcome.EndpointSaveFailed;
        }

        if (string.IsNullOrWhiteSpace(newApiKey))
        {
            return SettingsSaveOutcome.Saved;
        }

        try
        {
            _apiKeyStore.SaveApiKey(newApiKey);
            return SettingsSaveOutcome.Saved;
        }
        catch
        {
            if (previousSettings is null)
            {
                return SettingsSaveOutcome.CredentialSaveFailedAfterRecovery;
            }

            try
            {
                await _settingsStore.SaveAsync(previousSettings, cancellationToken);
                return SettingsSaveOutcome.CredentialSaveFailedRolledBack;
            }
            catch
            {
                return SettingsSaveOutcome.CredentialSaveFailedRollbackFailed;
            }
        }
    }
}
