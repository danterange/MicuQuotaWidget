namespace MicuQuotaWidget.Core.Services;

/// <summary>
/// Identifies a response that reached the expected host but cannot be parsed as the profile schema, allowing the documented billing fallback.
/// </summary>
public sealed class MicuSchemaException : MicuApiException
{
    /// <summary>
    /// Creates a parse-contract failure without retaining the provider's raw response body.
    /// </summary>
    public MicuSchemaException(string message)
        : base(message)
    {
    }
}
