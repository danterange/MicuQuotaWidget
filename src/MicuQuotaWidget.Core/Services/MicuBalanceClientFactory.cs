using MicuQuotaWidget.Core.Models;

namespace MicuQuotaWidget.Core.Services;

/// <summary>
/// Binds the application's shared HTTP transport to endpoint-specific Micu client instances.
/// </summary>
public sealed class MicuBalanceClientFactory : IMicuBalanceClientFactory
{
    private readonly HttpClient _httpClient;

    /// <summary>
    /// Creates a factory backed by an application-lifetime HTTP client to avoid socket exhaustion during polling.
    /// </summary>
    public MicuBalanceClientFactory(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    /// <summary>
    /// Creates a read-only client that will only contact the fixed host selected by <paramref name="endpointKind"/>.
    /// </summary>
    public IMicuBalanceClient Create(MicuEndpointKind endpointKind, string apiKey)
    {
        return new MicuBalanceClient(_httpClient, new MicuClientOptions(endpointKind, apiKey));
    }
}
