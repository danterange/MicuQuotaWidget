using MicuQuotaWidget.Core.Models;

namespace MicuQuotaWidget.Services;

/// <summary>
/// Stores only non-secret local widget choices; API keys remain in Windows Credential Manager.
/// </summary>
public sealed record WidgetSettings
{
    /// <summary>
    /// Creates settings with the current Micu API contract selected by default.
    /// </summary>
    public WidgetSettings()
        : this(MicuEndpointKind.MicuApi)
    {
    }

    /// <summary>
    /// Creates settings for a specific fixed endpoint mode.
    /// </summary>
    public WidgetSettings(MicuEndpointKind endpointKind)
    {
        EndpointKind = endpointKind;
    }

    /// <summary>
    /// Gets the non-secret Micu account contract selected in the settings dialog.
    /// </summary>
    public MicuEndpointKind EndpointKind { get; init; }
}
