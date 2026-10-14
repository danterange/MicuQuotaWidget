using MicuQuotaWidget.Core.Models;

namespace MicuQuotaWidget.Core.Services;

/// <summary>
/// Creates a short-lived query client while allowing the application to share one long-lived <see cref="HttpClient"/>.
/// </summary>
public interface IMicuBalanceClientFactory
{
    /// <summary>
    /// Creates a client for the selected Micu endpoint mode and the caller's in-memory key.
    /// </summary>
    IMicuBalanceClient Create(MicuEndpointKind endpointKind, string apiKey);
}
