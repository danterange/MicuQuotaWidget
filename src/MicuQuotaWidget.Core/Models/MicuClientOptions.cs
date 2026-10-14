namespace MicuQuotaWidget.Core.Models;

/// <summary>
/// Holds a caller-supplied API key together with a fixed, allow-listed Micu endpoint mode.
/// The type intentionally does not accept arbitrary URLs so a compromised local setting cannot redirect a credential.
/// </summary>
public sealed class MicuClientOptions
{
    private static readonly Uri MicuApiBaseUri = new("https://www.micuapi.ai/", UriKind.Absolute);
    private static readonly Uri MicuSubscriptionBaseUri = new("https://sub.micuapi.ai/", UriKind.Absolute);

    /// <summary>
    /// Creates validated client options for one supported Micu account mode.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when no API key is supplied.</exception>
    public MicuClientOptions(MicuEndpointKind endpointKind, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("An API key is required for a balance query.", nameof(apiKey));
        }

        EndpointKind = endpointKind;
        ApiKey = apiKey.Trim();
    }

    /// <summary>
    /// Gets the fixed query contract selected by the user.
    /// </summary>
    public MicuEndpointKind EndpointKind { get; }

    /// <summary>
    /// Gets the in-memory API key used only to build an Authorization header for the allow-listed host.
    /// </summary>
    public string ApiKey { get; }

    /// <summary>
    /// Gets the fixed HTTPS host associated with the selected endpoint mode.
    /// </summary>
    public Uri BaseUri => EndpointKind switch
    {
        MicuEndpointKind.MicuApi => MicuApiBaseUri,
        MicuEndpointKind.MicuSubscription => MicuSubscriptionBaseUri,
        _ => throw new InvalidOperationException("Unsupported Micu endpoint mode.")
    };
}
