namespace MicuQuotaWidget.Core.Services;

/// <summary>
/// Represents a safe, user-actionable balance-query failure without preserving raw response text that might contain sensitive data.
/// </summary>
public class MicuApiException : Exception
{
    /// <summary>
    /// Creates a query failure with a concise diagnostic message suitable for a local status surface.
    /// </summary>
    public MicuApiException(string message)
        : base(message)
    {
    }
}
