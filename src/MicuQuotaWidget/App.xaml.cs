using System.Net.Http;
using System.Windows;
using MicuQuotaWidget.Core.Services;
using MicuQuotaWidget.Services;
using MicuQuotaWidget.ViewModels;

namespace MicuQuotaWidget;

/// <summary>
/// Composes application-lifetime services so polling shares one HTTP transport and secrets never enter XAML or configuration files.
/// </summary>
public partial class App : Application, IDisposable
{
    private readonly HttpClient _httpClient = new(new HttpClientHandler
    {
        AllowAutoRedirect = false
    })
    {
        Timeout = System.Threading.Timeout.InfiniteTimeSpan
    };
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

        var settingsStore = new JsonWidgetSettingsStore();
        var apiKeyStore = new WindowsCredentialStore();
        var clientFactory = new MicuBalanceClientFactory(_httpClient);
        var viewModel = new MainWindowViewModel(settingsStore, apiKeyStore, clientFactory);

        MainWindow = new MainWindow(viewModel, settingsStore, apiKeyStore);
        MainWindow.Show();
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
        _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }
}
