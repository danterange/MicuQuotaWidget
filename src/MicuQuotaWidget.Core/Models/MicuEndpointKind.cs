namespace MicuQuotaWidget.Core.Models;

/// <summary>
/// Describes the two fixed Micu query contracts supported by the widget.
/// Values are kept internal to the local configuration and must not be sent as provider input.
/// </summary>
public enum MicuEndpointKind
{
    /// <summary>
    /// Uses the current Micu API host and falls back from the profile endpoint to OpenAI-compatible billing endpoints.
    /// </summary>
    MicuApi,

    /// <summary>
    /// Uses the legacy Micu Subscription host's dedicated usage endpoint.
    /// </summary>
    MicuSubscription
}
