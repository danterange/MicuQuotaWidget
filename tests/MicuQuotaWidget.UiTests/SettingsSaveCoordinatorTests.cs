using MicuQuotaWidget.Core.Models;
using MicuQuotaWidget.Services;

namespace MicuQuotaWidget.UiTests;

/// <summary>
/// Verifies the settings-save transaction outcomes that the dialog maps to recovery guidance for the user.
/// </summary>
public sealed class SettingsSaveCoordinatorTests
{
    /// <summary>
    /// Confirms that a malformed non-secret settings file can be overwritten when a valid key is supplied.
    /// </summary>
    [Fact]
    public async Task SaveAsyncWhenPreviousSettingsAreMalformedRecoversWithNewConfiguration()
    {
        var settingsStore = new SequencedSettingsStore(loadIsMalformed: true);
        var apiKeyStore = new RecordingApiKeyStore(existingKey: null);
        var coordinator = new SettingsSaveCoordinator(settingsStore, apiKeyStore);

        SettingsSaveOutcome outcome = await coordinator.SaveAsync(new WidgetSettings(MicuEndpointKind.MicuSubscription), "new-key");

        Assert.Equal(SettingsSaveOutcome.Saved, outcome);
        Assert.Equal(MicuEndpointKind.MicuSubscription, settingsStore.CurrentSettings.EndpointKind);
        Assert.Equal("new-key", apiKeyStore.SavedKey);
    }

    /// <summary>
    /// Confirms that a credential failure restores the prior readable endpoint mode before reporting a retryable failure.
    /// </summary>
    [Fact]
    public async Task SaveAsyncWhenCredentialWriteFailsRestoresPreviousEndpoint()
    {
        var settingsStore = new SequencedSettingsStore(new WidgetSettings(MicuEndpointKind.MicuApi));
        var apiKeyStore = new ThrowingApiKeyStore(existingKey: "old-key");
        var coordinator = new SettingsSaveCoordinator(settingsStore, apiKeyStore);

        SettingsSaveOutcome outcome = await coordinator.SaveAsync(new WidgetSettings(MicuEndpointKind.MicuSubscription), "new-key");

        Assert.Equal(SettingsSaveOutcome.CredentialSaveFailedRolledBack, outcome);
        Assert.Equal(MicuEndpointKind.MicuApi, settingsStore.CurrentSettings.EndpointKind);
        Assert.Equal(
            new[] { MicuEndpointKind.MicuSubscription, MicuEndpointKind.MicuApi },
            settingsStore.SavedEndpointKinds);
    }

    /// <summary>
    /// Confirms that a credential failure after replacing a malformed settings file reports the recovered endpoint state without claiming a rollback occurred.
    /// </summary>
    [Fact]
    public async Task SaveAsyncWhenCredentialWriteFailsAfterRecoveryReportsRecoveryState()
    {
        var settingsStore = new SequencedSettingsStore(loadIsMalformed: true);
        var apiKeyStore = new ThrowingApiKeyStore(existingKey: "old-key");
        var coordinator = new SettingsSaveCoordinator(settingsStore, apiKeyStore);

        SettingsSaveOutcome outcome = await coordinator.SaveAsync(new WidgetSettings(MicuEndpointKind.MicuSubscription), "new-key");

        Assert.Equal(SettingsSaveOutcome.CredentialSaveFailedAfterRecovery, outcome);
        Assert.Equal(MicuEndpointKind.MicuSubscription, settingsStore.CurrentSettings.EndpointKind);
    }

    /// <summary>
    /// Confirms that a failed rollback reports the exact partial-save condition instead of implying the old endpoint was restored.
    /// </summary>
    [Fact]
    public async Task SaveAsyncWhenCredentialRollbackFailsReportsPartialEndpointChange()
    {
        var settingsStore = new SequencedSettingsStore(
            new WidgetSettings(MicuEndpointKind.MicuApi),
            null,
            new System.IO.IOException("Synthetic rollback failure."));
        var apiKeyStore = new ThrowingApiKeyStore(existingKey: "old-key");
        var coordinator = new SettingsSaveCoordinator(settingsStore, apiKeyStore);

        SettingsSaveOutcome outcome = await coordinator.SaveAsync(new WidgetSettings(MicuEndpointKind.MicuSubscription), "new-key");

        Assert.Equal(SettingsSaveOutcome.CredentialSaveFailedRollbackFailed, outcome);
        Assert.Equal(MicuEndpointKind.MicuSubscription, settingsStore.CurrentSettings.EndpointKind);
    }

    /// <summary>
    /// Confirms that an endpoint persistence failure stops before attempting to mutate the existing credential.
    /// </summary>
    [Fact]
    public async Task SaveAsyncWhenEndpointWriteFailsDoesNotWriteCredential()
    {
        var settingsStore = new SequencedSettingsStore(
            new WidgetSettings(MicuEndpointKind.MicuApi),
            new System.IO.IOException("Synthetic endpoint save failure."));
        var apiKeyStore = new RecordingApiKeyStore(existingKey: "old-key");
        var coordinator = new SettingsSaveCoordinator(settingsStore, apiKeyStore);

        SettingsSaveOutcome outcome = await coordinator.SaveAsync(new WidgetSettings(MicuEndpointKind.MicuSubscription), "new-key");

        Assert.Equal(SettingsSaveOutcome.EndpointSaveFailed, outcome);
        Assert.Null(apiKeyStore.SavedKey);
        Assert.Equal(MicuEndpointKind.MicuApi, settingsStore.CurrentSettings.EndpointKind);
    }

    /// <summary>
    /// Provides ordered settings-store failures so each transactional branch can be asserted without disk access.
    /// </summary>
    private sealed class SequencedSettingsStore : IWidgetSettingsStore
    {
        private readonly Queue<Exception?> _saveExceptions;
        private readonly bool _loadIsMalformed;

        /// <summary>
        /// Creates a store with a current endpoint and optional ordered failures for save calls.
        /// </summary>
        public SequencedSettingsStore(WidgetSettings? initialSettings = null, params Exception?[] saveExceptions)
        {
            CurrentSettings = initialSettings ?? new WidgetSettings(MicuEndpointKind.MicuApi);
            _saveExceptions = new Queue<Exception?>(saveExceptions);
        }

        /// <summary>
        /// Creates a store whose initial read simulates a malformed JSON settings file.
        /// </summary>
        public SequencedSettingsStore(bool loadIsMalformed)
        {
            CurrentSettings = new WidgetSettings(MicuEndpointKind.MicuApi);
            _loadIsMalformed = loadIsMalformed;
            _saveExceptions = new Queue<Exception?>();
        }

        /// <summary>
        /// Gets the endpoint mode that currently survived the simulated persistence sequence.
        /// </summary>
        public WidgetSettings CurrentSettings { get; private set; }

        /// <summary>
        /// Gets every endpoint mode submitted to persistence in order.
        /// </summary>
        public List<MicuEndpointKind> SavedEndpointKinds { get; } = [];

        /// <summary>
        /// Returns the current endpoint or simulates the damaged-file branch used by the coordinator.
        /// </summary>
        public Task<WidgetSettings> LoadAsync(CancellationToken cancellationToken = default)
        {
            if (_loadIsMalformed)
            {
                throw new InvalidOperationException("Synthetic malformed settings file.");
            }

            return Task.FromResult(CurrentSettings);
        }

        /// <summary>
        /// Saves an endpoint unless the next synthetic failure models a file-system write problem.
        /// </summary>
        public Task SaveAsync(WidgetSettings settings, CancellationToken cancellationToken = default)
        {
            Exception? exception = _saveExceptions.Count > 0 ? _saveExceptions.Dequeue() : null;

            if (exception is not null)
            {
                throw exception;
            }

            CurrentSettings = settings;
            SavedEndpointKinds.Add(settings.EndpointKind);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Stores a successful synthetic key write without using Windows Credential Manager.
    /// </summary>
    private sealed class RecordingApiKeyStore : IApiKeyStore
    {
        private readonly string? _existingKey;

        /// <summary>
        /// Creates a store with an optional pre-existing key.
        /// </summary>
        public RecordingApiKeyStore(string? existingKey)
        {
            _existingKey = existingKey;
        }

        /// <summary>
        /// Gets the key saved during the current test, if any.
        /// </summary>
        public string? SavedKey { get; private set; }

        /// <summary>
        /// Returns the existing synthetic key before a new write occurs.
        /// </summary>
        public string? ReadApiKey()
        {
            return _existingKey;
        }

        /// <summary>
        /// Records a synthetic successful key save.
        /// </summary>
        public void SaveApiKey(string apiKey)
        {
            SavedKey = apiKey;
        }

        /// <summary>
        /// Completes a no-op delete for this in-memory test double.
        /// </summary>
        public void DeleteApiKey()
        {
        }
    }

    /// <summary>
    /// Simulates a Credential Manager write failure while still reporting an existing key for first-use validation.
    /// </summary>
    private sealed class ThrowingApiKeyStore : IApiKeyStore
    {
        private readonly string? _existingKey;

        /// <summary>
        /// Creates a failing store with an optional existing credential.
        /// </summary>
        public ThrowingApiKeyStore(string? existingKey)
        {
            _existingKey = existingKey;
        }

        /// <summary>
        /// Returns the existing synthetic key.
        /// </summary>
        public string? ReadApiKey()
        {
            return _existingKey;
        }

        /// <summary>
        /// Throws a deterministic credential-write error for rollback testing.
        /// </summary>
        public void SaveApiKey(string apiKey)
        {
            throw new InvalidOperationException("Synthetic Credential Manager write failure.");
        }

        /// <summary>
        /// Completes a no-op delete because this test double never persists a credential.
        /// </summary>
        public void DeleteApiKey()
        {
        }
    }
}
