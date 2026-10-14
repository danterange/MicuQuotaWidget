using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using MicuQuotaWidget.Core.Models;

namespace MicuQuotaWidget.Core.Services;

/// <summary>
/// Queries only the Micu balance paths observed in the public MicuCodeLine implementations.
/// It deliberately normalizes provider payloads into a small model and never exposes raw response content to callers.
/// </summary>
public sealed class MicuBalanceClient : IMicuBalanceClient
{
    private const decimal BillingUsageUnitsPerDollar = 100m;
    private const int MaximumJsonPayloadBytes = 64 * 1024;
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(5);
    private const decimal NewApiQuotaUnitsPerDollar = 500_000m;

    private readonly HttpClient _httpClient;
    private readonly MicuClientOptions _options;
    private readonly TimeSpan _requestTimeout;

    /// <summary>
    /// Creates a balance client with a caller-owned shared transport, validated endpoint/key options, and a per-request deadline.
    /// </summary>
    public MicuBalanceClient(HttpClient httpClient, MicuClientOptions options, TimeSpan? requestTimeout = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _requestTimeout = requestTimeout ?? DefaultRequestTimeout;

        if (_requestTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(requestTimeout), "The request timeout must be greater than zero.");
        }
    }

    /// <summary>
    /// Retrieves a normalized balance snapshot, using the profile-to-billing fallback only for the current Micu API mode.
    /// </summary>
    public async Task<MicuBalanceSnapshot> GetBalanceAsync(CancellationToken cancellationToken)
    {
        return _options.EndpointKind switch
        {
            MicuEndpointKind.MicuApi => await GetMicuApiBalanceAsync(cancellationToken).ConfigureAwait(false),
            MicuEndpointKind.MicuSubscription => await GetSubscriptionBalanceAsync(cancellationToken).ConfigureAwait(false),
            _ => throw new MicuApiException("Unsupported Micu endpoint mode.")
        };
    }

    /// <summary>
    /// Attempts the current Micu profile endpoint first, then uses the OpenAI-compatible billing contract when quota fields are unavailable.
    /// </summary>
    private async Task<MicuBalanceSnapshot> GetMicuApiBalanceAsync(CancellationToken cancellationToken)
    {
        MicuBalanceSnapshot? profileSnapshot = await TryGetProfileBalanceAsync(cancellationToken).ConfigureAwait(false);

        return profileSnapshot ?? await GetBillingBalanceAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the optional profile endpoint without treating an unsupported or session-only response as a final balance failure.
    /// </summary>
    private async Task<MicuBalanceSnapshot?> TryGetProfileBalanceAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource requestCancellation = CreateRequestCancellation(cancellationToken);
        using HttpResponseMessage response = await SendGetAsync("api/user/self", requestCancellation.Token).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            if (CanFallbackFromProfile(response.StatusCode))
            {
                return null;
            }

            throw CreateHttpFailure(response.StatusCode);
        }

        try
        {
            using JsonDocument document = await ReadJsonAsync(response, requestCancellation.Token).ConfigureAwait(false);

            return TryParseProfileSnapshot(document.RootElement, out MicuBalanceSnapshot? snapshot) ? snapshot : null;
        }
        catch (MicuSchemaException)
        {
            // A non-JSON profile contract is treated like an unsupported profile endpoint and may still have a valid billing fallback.
            return null;
        }
    }

    /// <summary>
    /// Queries OpenAI-compatible billing endpoints and converts the provider's cent-style usage unit to dollars.
    /// </summary>
    private async Task<MicuBalanceSnapshot> GetBillingBalanceAsync(CancellationToken cancellationToken)
    {
        using JsonDocument subscription = await GetRequiredJsonAsync("v1/dashboard/billing/subscription", cancellationToken).ConfigureAwait(false);
        decimal hardLimit = GetRequiredDecimal(subscription.RootElement, "hard_limit_usd", "未能读取账户额度上限。");

        string endDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string usagePath = $"v1/dashboard/billing/usage?start_date=2020-01-01&end_date={Uri.EscapeDataString(endDate)}";
        using JsonDocument usage = await GetRequiredJsonAsync(usagePath, cancellationToken).ConfigureAwait(false);
        decimal totalUsage = GetRequiredDecimal(usage.RootElement, "total_usage", "未能读取账户已用额度。");

        return CreateSnapshot(hardLimit - totalUsage / BillingUsageUnitsPerDollar, "USD", TryReadPlanName(subscription.RootElement));
    }

    /// <summary>
    /// Queries the legacy Micu Subscription usage contract, which exposes the remaining balance directly.
    /// </summary>
    private async Task<MicuBalanceSnapshot> GetSubscriptionBalanceAsync(CancellationToken cancellationToken)
    {
        using JsonDocument usage = await GetRequiredJsonAsync("v1/usage", cancellationToken).ConfigureAwait(false);
        JsonElement content = GetDataOrRoot(usage.RootElement);
        decimal balance = GetRequiredDecimal(content, "balance", "未能读取订阅余额。");
        string currency = TryReadString(content, "currency") ?? "USD";

        return CreateSnapshot(balance, currency, TryReadPlanName(content));
    }

    /// <summary>
    /// Sends a GET request with the API key only in the Authorization header and never logs the request or response.
    /// </summary>
    private async Task<HttpResponseMessage> SendGetAsync(string relativePath, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_options.BaseUri, relativePath));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        request.Headers.Accept.ParseAdd("application/json");

        return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets and parses a successful JSON response, converting HTTP status failures to safe messages without provider body text.
    /// </summary>
    private async Task<JsonDocument> GetRequiredJsonAsync(string relativePath, CancellationToken cancellationToken)
    {
        using CancellationTokenSource requestCancellation = CreateRequestCancellation(cancellationToken);
        using HttpResponseMessage response = await SendGetAsync(relativePath, requestCancellation.Token).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw CreateHttpFailure(response.StatusCode);
        }

        return await ReadJsonAsync(response, requestCancellation.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies the five-second deadline to both header retrieval and later body parsing because ResponseHeadersRead does not time out content reads by itself.
    /// </summary>
    private CancellationTokenSource CreateRequestCancellation(CancellationToken callerCancellationToken)
    {
        var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(callerCancellationToken);
        requestCancellation.CancelAfter(_requestTimeout);

        return requestCancellation;
    }

    /// <summary>
    /// Parses a small response body as JSON while applying the same deadline to content reads and refusing unexpectedly large payloads.
    /// </summary>
    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is long contentLength && contentLength > MaximumJsonPayloadBytes)
        {
            throw new MicuApiException("余额服务返回的数据过大，已拒绝读取。");
        }

        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(8 * 1024);

        try
        {
            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var payload = new MemoryStream();
            int totalBytes = 0;

            while (true)
            {
                int bytesRead = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);

                if (bytesRead == 0)
                {
                    break;
                }

                totalBytes += bytesRead;

                if (totalBytes > MaximumJsonPayloadBytes)
                {
                    throw new MicuApiException("余额服务返回的数据过大，已拒绝读取。");
                }

                await payload.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
            }

            return JsonDocument.Parse(payload.ToArray());
        }
        catch (JsonException)
        {
            throw new MicuSchemaException("余额服务返回了无法识别的数据。");
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    /// <summary>
    /// Reads a profile payload only when it contains an unambiguous direct balance or the expected New API quota fields.
    /// </summary>
    private static bool TryParseProfileSnapshot(JsonElement root, out MicuBalanceSnapshot? snapshot)
    {
        JsonElement content = GetDataOrRoot(root);

        if (TryReadDecimal(content, "balance", out decimal directBalance))
        {
            snapshot = CreateSnapshot(directBalance, TryReadString(content, "currency") ?? "USD", TryReadPlanName(content));
            return true;
        }

        if (TryReadDecimal(content, "quota", out decimal quota) && TryReadDecimal(content, "used_quota", out decimal usedQuota))
        {
            snapshot = CreateSnapshot((quota - usedQuota) / NewApiQuotaUnitsPerDollar, "USD", TryReadPlanName(content));
            return true;
        }

        snapshot = null;
        return false;
    }

    /// <summary>
    /// Uses the nested <c>data</c> object when the provider wraps its fields, otherwise preserves a root-level response shape.
    /// </summary>
    private static JsonElement GetDataOrRoot(JsonElement root)
    {
        return root.ValueKind == JsonValueKind.Object
               && root.TryGetProperty("data", out JsonElement data)
               && data.ValueKind == JsonValueKind.Object
            ? data
            : root;
    }

    /// <summary>
    /// Requires a numeric field and accepts only JSON numbers or invariant numeric strings to avoid locale-dependent parsing.
    /// </summary>
    private static decimal GetRequiredDecimal(JsonElement source, string propertyName, string errorMessage)
    {
        if (TryReadDecimal(source, propertyName, out decimal value))
        {
            return value;
        }

        throw new MicuApiException(errorMessage);
    }

    /// <summary>
    /// Safely reads a decimal field from a provider response without coercing unrelated JSON values.
    /// </summary>
    private static bool TryReadDecimal(JsonElement source, string propertyName, out decimal value)
    {
        value = default;

        if (source.ValueKind != JsonValueKind.Object || !source.TryGetProperty(propertyName, out JsonElement property))
        {
            return false;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number => property.TryGetDecimal(out value),
            JsonValueKind.String => decimal.TryParse(property.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out value),
            _ => false
        };
    }

    /// <summary>
    /// Reads an optional non-empty string field without persisting raw payload data beyond the immediate snapshot.
    /// </summary>
    private static string? TryReadString(JsonElement source, string propertyName)
    {
        if (source.ValueKind != JsonValueKind.Object
            || !source.TryGetProperty(propertyName, out JsonElement property)
            || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? value = property.GetString();

        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>
    /// Selects the first known human-readable plan label while keeping unknown response fields out of the UI.
    /// </summary>
    private static string? TryReadPlanName(JsonElement source)
    {
        return TryReadString(source, "planName")
               ?? TryReadString(source, "plan_name")
               ?? TryReadString(source, "group")
               ?? TryReadString(source, "subscription");
    }

    /// <summary>
    /// Creates a timestamped snapshot and clamps impossible negative available values at the display boundary.
    /// </summary>
    private static MicuBalanceSnapshot CreateSnapshot(decimal amount, string currency, string? planName)
    {
        return new MicuBalanceSnapshot(amount, currency, DateTimeOffset.UtcNow, planName);
    }

    /// <summary>
    /// Converts an HTTP failure into a recovery-oriented message without echoing server-provided body text.
    /// </summary>
    private static MicuApiException CreateHttpFailure(HttpStatusCode statusCode)
    {
        return statusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new MicuApiException("API Key 无效、已过期或没有查询余额的权限。"),
            HttpStatusCode.TooManyRequests => new MicuApiException("余额服务暂时限流，请稍后重试。"),
            _ => new MicuApiException($"余额服务暂时不可用（HTTP {(int)statusCode}）。")
        };
    }

    /// <summary>
    /// Limits profile-to-billing fallback to the known session-only or unsupported-profile cases so rate-limit and server failures are not amplified.
    /// </summary>
    private static bool CanFallbackFromProfile(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.Unauthorized
            or HttpStatusCode.Forbidden
            or HttpStatusCode.NotFound
            or HttpStatusCode.MethodNotAllowed;
    }
}
