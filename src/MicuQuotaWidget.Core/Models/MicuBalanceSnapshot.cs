namespace MicuQuotaWidget.Core.Models;

/// <summary>
/// Represents one successfully parsed, read-only balance response without retaining the API key or raw provider payload.
/// </summary>
public sealed record MicuBalanceSnapshot
{
    /// <summary>
    /// Creates a balance result after the endpoint-specific quota units have been converted to a display amount.
    /// </summary>
    public MicuBalanceSnapshot(decimal amount, string currency, DateTimeOffset queriedAt, string? planName)
    {
        Amount = Math.Max(0m, amount);
        Currency = currency;
        QueriedAt = queriedAt;
        PlanName = planName;
    }

    /// <summary>
    /// Gets the non-negative available amount suitable for display in the widget.
    /// </summary>
    public decimal Amount { get; }

    /// <summary>
    /// Gets the provider-reported or contract-default currency label.
    /// </summary>
    public string Currency { get; }

    /// <summary>
    /// Gets the UTC instant at which this snapshot was accepted by the local client.
    /// </summary>
    public DateTimeOffset QueriedAt { get; }

    /// <summary>
    /// Gets an optional human-readable plan name when the response provides one.
    /// </summary>
    public string? PlanName { get; }
}
