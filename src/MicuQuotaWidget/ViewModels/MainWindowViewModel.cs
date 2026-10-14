using System.Globalization;
using MicuQuotaWidget.Core.Models;
using MicuQuotaWidget.Core.Services;
using MicuQuotaWidget.Infrastructure;
using MicuQuotaWidget.Services;

namespace MicuQuotaWidget.ViewModels;

/// <summary>
/// Coordinates the compact widget state, secure local configuration, and a non-overlapping 10-second balance polling loop.
/// </summary>
public sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    private readonly IApiKeyStore _apiKeyStore;
    private readonly IMicuBalanceClientFactory _clientFactory;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly IWidgetSettingsStore _settingsStore;

    private string? _balanceText = "--";
    private Task? _disposeTask;
    private string _statusText = "正在准备查询…";
    private string _detailText = "小组件会在配置完成后每 10 秒自动刷新。";
    private string _lastUpdatedText = "尚未查询";
    private bool _isRefreshing;
    private bool _isTopmost = true;
    private bool _isDisposed;
    private Task? _pollingTask;
    private long _configurationRevision;

    /// <summary>
    /// Creates the state model with abstractions that keep credential and network behavior independently testable.
    /// </summary>
    public MainWindowViewModel(
        IWidgetSettingsStore settingsStore,
        IApiKeyStore apiKeyStore,
        IMicuBalanceClientFactory clientFactory)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _apiKeyStore = apiKeyStore ?? throw new ArgumentNullException(nameof(apiKeyStore));
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));

        RefreshCommand = new AsyncRelayCommand(RefreshFromCommandAsync, () => !IsRefreshing, HandleUnexpectedCommandFailure);
        ToggleTopmostCommand = new RelayCommand(ToggleTopmost);
    }

    /// <summary>
    /// Gets the formatted balance value; it remains at the last known value during a failed refresh.
    /// </summary>
    public string? BalanceText
    {
        get => _balanceText;
        private set => SetProperty(ref _balanceText, value);
    }

    /// <summary>
    /// Gets the concise live status shown beneath the balance.
    /// </summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>
    /// Gets the fuller local recovery hint exposed as a tooltip rather than cluttering the compact card.
    /// </summary>
    public string DetailText
    {
        get => _detailText;
        private set => SetProperty(ref _detailText, value);
    }

    /// <summary>
    /// Gets the locally formatted timestamp of the most recently accepted balance result.
    /// </summary>
    public string LastUpdatedText
    {
        get => _lastUpdatedText;
        private set => SetProperty(ref _lastUpdatedText, value);
    }

    /// <summary>
    /// Gets whether a single balance request is currently in flight.
    /// </summary>
    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set
        {
            if (SetProperty(ref _isRefreshing, value))
            {
                RefreshCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// Gets or sets whether the WPF window should stay above normal application windows.
    /// </summary>
    public bool IsTopmost
    {
        get => _isTopmost;
        set
        {
            if (SetProperty(ref _isTopmost, value))
            {
                OnPropertyChanged(nameof(TopmostText));
            }
        }
    }

    /// <summary>
    /// Gets the short human-readable state for the visible topmost toggle.
    /// </summary>
    public string TopmostText => IsTopmost ? "已置顶" : "未置顶";

    /// <summary>
    /// Gets the manually triggered balance refresh command used by both the button and Ctrl+R.
    /// </summary>
    public AsyncRelayCommand RefreshCommand { get; }

    /// <summary>
    /// Gets the topmost toggle command used by Ctrl+T without custom key-event code.
    /// </summary>
    public RelayCommand ToggleTopmostCommand { get; }

    /// <summary>
    /// Performs an immediate refresh and starts the single periodic loop once the main window is loaded.
    /// </summary>
    public async Task StartAsync()
    {
        ThrowIfDisposed();
        await RefreshAsync(_lifetimeCancellation.Token).ConfigureAwait(true);

        if (_isDisposed || _pollingTask is not null)
        {
            return;
        }

        _pollingTask = PollAsync(_lifetimeCancellation.Token);
    }

    /// <summary>
    /// Refreshes the balance once, suppressing concurrent ticks and preserving the last successful result on failure.
    /// </summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await RefreshCoreAsync(waitForExistingRefresh: false, cancellationToken: cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// Marks local account settings as changed and waits for any older request to finish before querying the newly saved configuration.
    /// </summary>
    public async Task RefreshAfterSettingsChangedAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        System.Threading.Interlocked.Increment(ref _configurationRevision);
        await RefreshCoreAsync(waitForExistingRefresh: true, cancellationToken: cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// Performs a refresh under either non-blocking polling semantics or a settings-save queue, discarding late results from an older configuration revision.
    /// </summary>
    private async Task RefreshCoreAsync(bool waitForExistingRefresh, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        using CancellationTokenSource linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token, cancellationToken);
        CancellationToken effectiveCancellationToken = linkedCancellation.Token;
        bool gateEntered = false;
        long refreshConfigurationRevision = -1;

        try
        {
            if (waitForExistingRefresh)
            {
                await _refreshGate.WaitAsync(effectiveCancellationToken).ConfigureAwait(true);
                gateEntered = true;
            }
            else
            {
                gateEntered = await _refreshGate.WaitAsync(0, effectiveCancellationToken).ConfigureAwait(true);
            }

            if (!gateEntered)
            {
                return;
            }

            IsRefreshing = true;
            StatusText = "正在查询余额…";
            DetailText = "正在使用安全的只读请求查询 Micu 余额。";
            refreshConfigurationRevision = System.Threading.Interlocked.Read(ref _configurationRevision);

            WidgetSettings settings = await _settingsStore.LoadAsync(effectiveCancellationToken).ConfigureAwait(true);
            string? apiKey = _apiKeyStore.ReadApiKey();

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                StatusText = "尚未配置 API Key";
                DetailText = "请打开设置，输入仅用于 Micu 余额查询的 API Key。";
                LastUpdatedText = "等待设置";
                return;
            }

            IMicuBalanceClient client = _clientFactory.Create(settings.EndpointKind, apiKey);
            MicuBalanceSnapshot snapshot = await client.GetBalanceAsync(effectiveCancellationToken).ConfigureAwait(true);

            if (refreshConfigurationRevision != System.Threading.Interlocked.Read(ref _configurationRevision))
            {
                // Settings were saved while this request was in flight; the queued settings refresh owns the visible state.
                return;
            }

            BalanceText = FormatBalance(snapshot);
            StatusText = string.IsNullOrWhiteSpace(snapshot.PlanName) ? "查询成功" : $"查询成功 · {snapshot.PlanName}";
            DetailText = "余额已从 Micu 只读接口更新。";
            LastUpdatedText = $"更新于 {snapshot.QueriedAt.LocalDateTime:HH:mm:ss}";
        }
        catch (OperationCanceledException) when (effectiveCancellationToken.IsCancellationRequested)
        {
            // Closing the widget is an expected cancellation path and must not replace a valid balance with an error.
        }
        catch (OperationCanceledException)
        {
            if (IsCurrentConfigurationRevision(refreshConfigurationRevision))
            {
                SetRecoverableFailure("查询超时", "余额服务超过 5 秒未响应，请稍后点击刷新重试。上次成功余额会被保留。");
            }
        }
        catch (MicuApiException)
        {
            if (IsCurrentConfigurationRevision(refreshConfigurationRevision))
            {
                SetRecoverableFailure("余额查询失败", "请检查 API Key、账号权限或稍后点击刷新重试。");
            }
        }
        catch (System.Net.Http.HttpRequestException)
        {
            if (IsCurrentConfigurationRevision(refreshConfigurationRevision))
            {
                SetRecoverableFailure("网络连接失败", "请检查网络连接后点击刷新重试。上次成功余额会被保留。");
            }
        }
        catch (Exception)
        {
            if (IsCurrentConfigurationRevision(refreshConfigurationRevision))
            {
                SetRecoverableFailure("本地设置不可用", "请打开设置检查查询方式，或重新保存配置后重试。");
            }
        }
        finally
        {
            if (gateEntered)
            {
                IsRefreshing = false;
                _refreshGate.Release();
            }
        }
    }

    /// <summary>
    /// Stops the periodic timer when the window closes; the current network request receives the same cancellation signal.
    /// </summary>
    public void StopPolling()
    {
        _lifetimeCancellation.Cancel();
    }

    /// <summary>
    /// Cancels all pending work and schedules resource disposal only after polling and any in-flight manual refresh leave the synchronization gate.
    /// </summary>
    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        StopPolling();
        _disposeTask ??= DrainAndDisposeAsync();
    }

    /// <summary>
    /// Invokes the public refresh path from the asynchronous WPF command without exposing command plumbing to callers.
    /// </summary>
    private Task RefreshFromCommandAsync()
    {
        return RefreshAsync();
    }

    /// <summary>
    /// Flips the topmost setting through one command so mouse and keyboard paths remain behaviorally identical.
    /// </summary>
    private void ToggleTopmost()
    {
        IsTopmost = !IsTopmost;
    }

    /// <summary>
    /// Runs one timer-backed refresh every ten seconds and exits silently when the owning window cancels it.
    /// </summary>
    private async Task PollAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(PollInterval);

            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(true))
            {
                if (_isDisposed)
                {
                    return;
                }

                await RefreshAsync(cancellationToken).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Window shutdown deliberately stops periodic work without showing a failure state.
        }
        catch (ObjectDisposedException) when (_isDisposed)
        {
            // Disposal can race with a dispatcher tick; the closed window must not surface that expected shutdown detail.
        }
    }

    /// <summary>
    /// Waits for all scheduled polling work and the refresh gate before disposing cancellation and synchronization primitives.
    /// </summary>
    private async Task DrainAndDisposeAsync()
    {
        try
        {
            if (_pollingTask is not null)
            {
                await _pollingTask.ConfigureAwait(false);
            }

            await _refreshGate.WaitAsync().ConfigureAwait(false);
            _refreshGate.Release();
        }
        catch (ObjectDisposedException)
        {
            // Repeated shutdown is harmless; a previous drain owns the final cleanup.
            return;
        }
        finally
        {
            _refreshGate.Dispose();
            _lifetimeCancellation.Dispose();
        }
    }

    /// <summary>
    /// Preserves any last successful balance while supplying a consistent recovery message for failed refreshes.
    /// </summary>
    private void SetRecoverableFailure(string status, string detail)
    {
        StatusText = status;
        DetailText = detail;

        if (BalanceText == "--")
        {
            LastUpdatedText = "尚未取得可用余额";
        }
    }

    /// <summary>
    /// Determines whether an in-flight request still belongs to the latest saved account configuration before it may mutate visible state.
    /// </summary>
    private bool IsCurrentConfigurationRevision(long refreshConfigurationRevision)
    {
        return refreshConfigurationRevision == System.Threading.Interlocked.Read(ref _configurationRevision);
    }

    /// <summary>
    /// Converts an unexpected command-boundary exception into a recoverable UI state instead of allowing async void to reach the WPF dispatcher.
    /// </summary>
    private void HandleUnexpectedCommandFailure(Exception exception)
    {
        SetRecoverableFailure("本地查询异常", "小组件遇到未预期问题，请关闭后重新打开，或检查设置后重试。");
    }

    /// <summary>
    /// Formats the provider-normalized amount with stable tabular digits to avoid layout jitter during polling.
    /// </summary>
    private static string FormatBalance(MicuBalanceSnapshot snapshot)
    {
        string amount = snapshot.Amount.ToString("N2", CultureInfo.InvariantCulture);

        return string.Equals(snapshot.Currency, "USD", StringComparison.OrdinalIgnoreCase)
            ? $"$ {amount}"
            : $"{snapshot.Currency} {amount}";
    }

    /// <summary>
    /// Prevents background callbacks from using a view model after its window has closed and disposed its synchronization gate.
    /// </summary>
    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
    }
}
