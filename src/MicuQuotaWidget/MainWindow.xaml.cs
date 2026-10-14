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
    /// Opens the configuration dialog without placing API-key handling in the main balance surface.
    /// </summary>
    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(_settingsStore, _apiKeyStore, _viewModel)
        {
            Owner = this
        };

        window.ShowDialog();
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
    /// Closes the widget through its visible custom title-bar control.
    /// </summary>
    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
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
                or ComboBox)
            {
                return true;
            }

            element = VisualTreeHelper.GetParent(element);
        }

        return false;
    }
}
