namespace MicuQuotaWidget.Services;

/// <summary>
/// Persists non-secret widget settings independently from the Windows credential store.
/// </summary>
public interface IWidgetSettingsStore
{
    /// <summary>
    /// Loads the saved endpoint mode or returns safe defaults when the widget has not been configured yet.
    /// </summary>
    Task<WidgetSettings> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists the endpoint mode using an atomic local-file replacement.
    /// </summary>
    Task SaveAsync(WidgetSettings settings, CancellationToken cancellationToken = default);
}
