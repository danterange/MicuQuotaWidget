using MicuQuotaWidget.Core.Models;

namespace MicuQuotaWidget.Core.Services;

/// <summary>
/// Defines a read-only Micu balance query that can be replaced with a deterministic fake in tests.
/// </summary>
public interface IMicuBalanceClient
{
    /// <summary>
    /// Queries the configured Micu endpoint and returns a normalized balance snapshot.
    /// </summary>
    Task<MicuBalanceSnapshot> GetBalanceAsync(CancellationToken cancellationToken);
}
