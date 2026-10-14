using MicuQuotaWidget.Core.Models;
using MicuQuotaWidget.Core.Services;
using MicuQuotaWidget.Services;
using MicuQuotaWidget.ViewModels;

namespace MicuQuotaWidget.UiTests;

/// <summary>
/// Covers user-visible query states and shutdown behavior without contacting an external Micu account.
/// </summary>
public sealed class MainWindowViewModelTests
{
    /// <summary>
    /// Verifies that an unconfigured widget provides a clear setup action instead of pretending that the balance is zero.
    /// </summary>
    [Fact]
    public async Task RefreshAsyncWhenKeyIsMissingShowsSetupRecoveryState()
    {
        using var viewModel = CreateViewModel(apiKey: null, new SnapshotClientFactory());

        await viewModel.RefreshAsync();

        Assert.Equal("--", viewModel.BalanceText);
        Assert.Equal("尚未配置 API Key", viewModel.StatusText);
        Assert.Equal("等待设置", viewModel.LastUpdatedText);
    }

    /// <summary>
    /// Verifies that a provider timeout is reported as a retryable service condition rather than a local settings corruption.
    /// </summary>
    [Fact]
    public async Task RefreshAsyncWhenProviderTimesOutShowsTimeoutRecoveryState()
    {
        using var viewModel = CreateViewModel("demo-key", new ExceptionClientFactory(new TaskCanceledException()));

        await viewModel.RefreshAsync();

        Assert.Equal("查询超时", viewModel.StatusText);
        Assert.Contains("超过 5 秒", viewModel.DetailText, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that a controlled in-flight request exposes loading feedback until the snapshot becomes available.
    /// </summary>
    [Fact]
    public async Task RefreshAsyncWhileRequestIsInFlightShowsLoadingStateThenSuccess()
    {
        var client = new ControlledClient();
        using var viewModel = CreateViewModel("demo-key", new SingleClientFactory(client));
        Task refreshTask = viewModel.RefreshAsync();

        await client.Started.Task;

        Assert.True(viewModel.IsRefreshing);
        Assert.Equal("正在查询余额…", viewModel.StatusText);

        client.Complete(new MicuBalanceSnapshot(12.34m, "USD", DateTimeOffset.UtcNow, "Demo"));
        await refreshTask;

        Assert.False(viewModel.IsRefreshing);
        Assert.Equal("$ 12.34", viewModel.BalanceText);
        Assert.Equal("查询成功 · Demo", viewModel.StatusText);
    }

    /// <summary>
    /// Verifies that the command path changes the same topmost state as the mouse-bound toggle control.
    /// </summary>
    [Fact]
    public void ToggleTopmostCommandChangesTopmostState()
    {
        using var viewModel = CreateViewModel("demo-key", new SnapshotClientFactory());

        viewModel.ToggleTopmostCommand.Execute(null);

        Assert.False(viewModel.IsTopmost);
        Assert.Equal("未置顶", viewModel.TopmostText);
    }

    /// <summary>
    /// Verifies that closing the view model cancels an active request without surfacing an ObjectDisposedException to the caller.
    /// </summary>
    [Fact]
    public async Task DisposeWhileRefreshIsInFlightCancelsCleanly()
    {
        var client = new ControlledClient();
        using var viewModel = CreateViewModel("demo-key", new SingleClientFactory(client));
        Task refreshTask = viewModel.RefreshAsync();

        await client.Started.Task;
        viewModel.Dispose();

        await refreshTask;
        Assert.False(viewModel.IsRefreshing);
    }

    /// <summary>
    /// Verifies that a settings save queues one fresh query and prevents a late result from the previous account configuration from replacing it.
    /// </summary>
    [Fact]
    public async Task RefreshAfterSettingsChangedAsyncQueuesNewConfigurationAndDiscardsOldResult()
    {
        var oldClient = new ControlledClient();
        var newSnapshot = new MicuBalanceSnapshot(88.88m, "USD", DateTimeOffset.UtcNow, "New configuration");
        var factory = new QueuedClientFactory(oldClient, new SnapshotClient(newSnapshot));
        var settingsStore = new MutableSettingsStore(MicuEndpointKind.MicuApi);
        using var viewModel = new MainWindowViewModel(settingsStore, new FixedApiKeyStore("demo-key"), factory);
        Task oldRefresh = viewModel.RefreshAsync();

        await oldClient.Started.Task;
        settingsStore.CurrentSettings = new WidgetSettings(MicuEndpointKind.MicuSubscription);
        Task savedSettingsRefresh = viewModel.RefreshAfterSettingsChangedAsync();
        oldClient.Complete(new MicuBalanceSnapshot(1.23m, "USD", DateTimeOffset.UtcNow, "Old configuration"));

        await oldRefresh;
        await savedSettingsRefresh;

        Assert.Equal("$ 88.88", viewModel.BalanceText);
        Assert.Equal("查询成功 · New configuration", viewModel.StatusText);
        Assert.Equal(2, factory.CreatedClientCount);
        Assert.Equal(new[] { MicuEndpointKind.MicuApi, MicuEndpointKind.MicuSubscription }, factory.RequestedEndpointKinds);
    }

    /// <summary>
    /// Verifies that a failure from an old in-flight request cannot overwrite the successful state queued for a newly saved configuration.
    /// </summary>
    [Fact]
    public async Task RefreshAfterSettingsChangedAsyncDiscardsOldFailureState()
    {
        var oldClient = new ControlledClient();
        var newSnapshot = new MicuBalanceSnapshot(66.66m, "USD", DateTimeOffset.UtcNow, "New configuration");
        var factory = new QueuedClientFactory(oldClient, new SnapshotClient(newSnapshot));
        var settingsStore = new MutableSettingsStore(MicuEndpointKind.MicuApi);
        using var viewModel = new MainWindowViewModel(settingsStore, new FixedApiKeyStore("demo-key"), factory);
        Task oldRefresh = viewModel.RefreshAsync();

        await oldClient.Started.Task;
        settingsStore.CurrentSettings = new WidgetSettings(MicuEndpointKind.MicuSubscription);
        Task savedSettingsRefresh = viewModel.RefreshAfterSettingsChangedAsync();
        oldClient.Fail(new System.Net.Http.HttpRequestException("Synthetic old configuration failure."));

        await oldRefresh;
        await savedSettingsRefresh;

        Assert.Equal("$ 66.66", viewModel.BalanceText);
        Assert.Equal("查询成功 · New configuration", viewModel.StatusText);
        Assert.Equal(new[] { MicuEndpointKind.MicuApi, MicuEndpointKind.MicuSubscription }, factory.RequestedEndpointKinds);
    }

    /// <summary>
    /// Creates a view model backed by in-memory test services so no test reads local settings or Credential Manager.
    /// </summary>
    private static MainWindowViewModel CreateViewModel(string? apiKey, IMicuBalanceClientFactory clientFactory)
    {
        return new MainWindowViewModel(
            new FixedSettingsStore(),
            new FixedApiKeyStore(apiKey),
            clientFactory);
    }

    /// <summary>
    /// Supplies a stable current-Micu endpoint mode for view-model state tests.
    /// </summary>
    private sealed class FixedSettingsStore : IWidgetSettingsStore
    {
        /// <summary>
        /// Returns the current Micu API mode without file access.
        /// </summary>
        public Task<WidgetSettings> LoadAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new WidgetSettings(MicuEndpointKind.MicuApi));
        }

        /// <summary>
        /// Completes immediately because test cases do not verify persistence in this layer.
        /// </summary>
        public Task SaveAsync(WidgetSettings settings, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Exposes a mutable endpoint mode so a test can model the committed configuration change performed by the settings dialog.
    /// </summary>
    private sealed class MutableSettingsStore : IWidgetSettingsStore
    {
        /// <summary>
        /// Creates an in-memory settings store with an initial endpoint mode.
        /// </summary>
        public MutableSettingsStore(MicuEndpointKind endpointKind)
        {
            CurrentSettings = new WidgetSettings(endpointKind);
        }

        /// <summary>
        /// Gets or sets the settings returned by the next refresh without involving the file system.
        /// </summary>
        public WidgetSettings CurrentSettings { get; set; }

        /// <summary>
        /// Returns the current in-memory endpoint mode.
        /// </summary>
        public Task<WidgetSettings> LoadAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CurrentSettings);
        }

        /// <summary>
        /// Replaces the in-memory endpoint mode for settings-dialog coordination tests.
        /// </summary>
        public Task SaveAsync(WidgetSettings settings, CancellationToken cancellationToken = default)
        {
            CurrentSettings = settings;
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Supplies an optional synthetic key without interacting with the user's Windows credential vault.
    /// </summary>
    private sealed class FixedApiKeyStore : IApiKeyStore
    {
        private readonly string? _apiKey;

        /// <summary>
        /// Creates a test key store with either a synthetic key or an intentionally missing configuration.
        /// </summary>
        public FixedApiKeyStore(string? apiKey)
        {
            _apiKey = apiKey;
        }

        /// <summary>
        /// Returns the configured synthetic key.
        /// </summary>
        public string? ReadApiKey()
        {
            return _apiKey;
        }

        /// <summary>
        /// Rejects unexpected persistence because UI-state tests must remain entirely in memory.
        /// </summary>
        public void SaveApiKey(string apiKey)
        {
            throw new NotSupportedException();
        }

        /// <summary>
        /// Rejects unexpected deletion because UI-state tests must remain entirely in memory.
        /// </summary>
        public void DeleteApiKey()
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>
    /// Creates a new immediate-success client for ordinary state tests.
    /// </summary>
    private sealed class SnapshotClientFactory : IMicuBalanceClientFactory
    {
        /// <summary>
        /// Returns a deterministic balance client without considering its input because test data has no secret value.
        /// </summary>
        public IMicuBalanceClient Create(MicuEndpointKind endpointKind, string apiKey)
        {
            return new SnapshotClient();
        }
    }

    /// <summary>
    /// Creates the same client instance so a test can control when its request completes or cancels.
    /// </summary>
    private sealed class SingleClientFactory : IMicuBalanceClientFactory
    {
        private readonly IMicuBalanceClient _client;

        /// <summary>
        /// Saves the caller-provided controllable client instance.
        /// </summary>
        public SingleClientFactory(IMicuBalanceClient client)
        {
            _client = client;
        }

        /// <summary>
        /// Returns the caller-provided client without creating network resources.
        /// </summary>
        public IMicuBalanceClient Create(MicuEndpointKind endpointKind, string apiKey)
        {
            return _client;
        }
    }

    /// <summary>
    /// Returns client instances in call order so a test can model an old in-flight configuration followed by the saved configuration.
    /// </summary>
    private sealed class QueuedClientFactory : IMicuBalanceClientFactory
    {
        private readonly Queue<IMicuBalanceClient> _clients;

        /// <summary>
        /// Saves a deterministic sequence of clients used by consecutive refreshes.
        /// </summary>
        public QueuedClientFactory(params IMicuBalanceClient[] clients)
        {
            _clients = new Queue<IMicuBalanceClient>(clients);
        }

        /// <summary>
        /// Gets the number of client instances supplied to the view model.
        /// </summary>
        public int CreatedClientCount { get; private set; }

        /// <summary>
        /// Gets endpoint modes requested by successive refreshes, proving the queued refresh used the newly saved mode.
        /// </summary>
        public List<MicuEndpointKind> RequestedEndpointKinds { get; } = [];

        /// <summary>
        /// Returns the next preconfigured client and fails loudly if an unexpected extra query occurs.
        /// </summary>
        public IMicuBalanceClient Create(MicuEndpointKind endpointKind, string apiKey)
        {
            if (_clients.Count == 0)
            {
                throw new InvalidOperationException("The view model created an unexpected extra balance client.");
            }

            CreatedClientCount++;
            RequestedEndpointKinds.Add(endpointKind);
            return _clients.Dequeue();
        }
    }

    /// <summary>
    /// Creates a client that reports a predefined exception for failure-state rendering tests.
    /// </summary>
    private sealed class ExceptionClientFactory : IMicuBalanceClientFactory
    {
        private readonly Exception _exception;

        /// <summary>
        /// Saves the expected failure that a created client will return.
        /// </summary>
        public ExceptionClientFactory(Exception exception)
        {
            _exception = exception;
        }

        /// <summary>
        /// Returns an exception-producing client without reaching the network.
        /// </summary>
        public IMicuBalanceClient Create(MicuEndpointKind endpointKind, string apiKey)
        {
            return new ExceptionClient(_exception);
        }
    }

    /// <summary>
    /// Provides an immediate successful snapshot for the toggle and shell tests.
    /// </summary>
    private sealed class SnapshotClient : IMicuBalanceClient
    {
        private readonly MicuBalanceSnapshot _snapshot;

        /// <summary>
        /// Creates a deterministic snapshot client using either the standard demo amount or a caller-provided response.
        /// </summary>
        public SnapshotClient(MicuBalanceSnapshot? snapshot = null)
        {
            _snapshot = snapshot ?? new MicuBalanceSnapshot(12.34m, "USD", DateTimeOffset.UtcNow, "Demo");
        }

        /// <summary>
        /// Returns a deterministic demonstration snapshot.
        /// </summary>
        public Task<MicuBalanceSnapshot> GetBalanceAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(_snapshot);
        }
    }

    /// <summary>
    /// Produces a controlled failure for retry-state testing.
    /// </summary>
    private sealed class ExceptionClient : IMicuBalanceClient
    {
        private readonly Exception _exception;

        /// <summary>
        /// Saves the failure that will be surfaced asynchronously.
        /// </summary>
        public ExceptionClient(Exception exception)
        {
            _exception = exception;
        }

        /// <summary>
        /// Returns the configured failure through the asynchronous query contract.
        /// </summary>
        public Task<MicuBalanceSnapshot> GetBalanceAsync(CancellationToken cancellationToken)
        {
            return Task.FromException<MicuBalanceSnapshot>(_exception);
        }
    }

    /// <summary>
    /// Delays completion until a test releases a snapshot or disposes the view model through its cancellation token.
    /// </summary>
    private sealed class ControlledClient : IMicuBalanceClient
    {
        private readonly TaskCompletionSource<MicuBalanceSnapshot> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Signals that a query has started so the test can inspect the loading state.
        /// </summary>
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Waits for explicit completion while honoring the cancellation token provided by the view model lifecycle.
        /// </summary>
        public Task<MicuBalanceSnapshot> GetBalanceAsync(CancellationToken cancellationToken)
        {
            Started.TrySetResult();

            return _completion.Task.WaitAsync(cancellationToken);
        }

        /// <summary>
        /// Completes the controlled request with a deterministic snapshot.
        /// </summary>
        public void Complete(MicuBalanceSnapshot snapshot)
        {
            _completion.TrySetResult(snapshot);
        }

        /// <summary>
        /// Completes the controlled request with a failure that represents an old configuration's late error.
        /// </summary>
        public void Fail(Exception exception)
        {
            _completion.TrySetException(exception);
        }
    }
}
