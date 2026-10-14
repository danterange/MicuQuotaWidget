using System.Net;
using System.Text;
using MicuQuotaWidget.Core.Models;
using MicuQuotaWidget.Core.Services;

namespace MicuQuotaWidget.Core.Tests;

/// <summary>
/// Verifies the observed Micu query contracts without contacting a real account or holding a real API key.
/// </summary>
public sealed class MicuBalanceClientTests
{
    private static readonly string[] BillingFallbackPaths =
    [
        "/api/user/self",
        "/v1/dashboard/billing/subscription",
        "/v1/dashboard/billing/usage"
    ];

    private static readonly string[] ProfileOnlyPaths = ["/api/user/self"];

    private static readonly string[] SubscriptionPaths = ["/v1/usage"];

    /// <summary>
    /// Confirms that an unavailable profile endpoint falls back to billing and applies the documented usage-unit conversion.
    /// </summary>
    [Fact]
    public async Task GetBalanceAsyncWhenProfileIsUnavailableFallsBackToBilling()
    {
        var handler = new SequenceHttpMessageHandler(
            CreateResponse(HttpStatusCode.Unauthorized, "{}"),
            CreateResponse(HttpStatusCode.OK, "{\"hard_limit_usd\":20}"),
            CreateResponse(HttpStatusCode.OK, "{\"total_usage\":650}"));
        using var httpClient = new HttpClient(handler);
        var client = new MicuBalanceClient(httpClient, new MicuClientOptions(MicuEndpointKind.MicuApi, "test-key"));

        MicuBalanceSnapshot result = await client.GetBalanceAsync(CancellationToken.None);

        Assert.Equal(13.5m, result.Amount);
        Assert.Equal("USD", result.Currency);
        Assert.Equal(BillingFallbackPaths, handler.RequestPaths);
        Assert.All(handler.AuthorizationSchemes, scheme => Assert.Equal("Bearer", scheme));
    }

    /// <summary>
    /// Confirms that a redirect-shaped profile response is rejected rather than followed or amplified into a fallback request.
    /// </summary>
    [Fact]
    public async Task GetBalanceAsyncWhenProfileRespondsWithRedirectRejectsWithoutFallback()
    {
        var handler = new SequenceHttpMessageHandler(CreateResponse(HttpStatusCode.Found, "{}"));
        using var httpClient = new HttpClient(handler);
        var client = new MicuBalanceClient(httpClient, new MicuClientOptions(MicuEndpointKind.MicuApi, "test-key"));

        await Assert.ThrowsAsync<MicuApiException>(() => client.GetBalanceAsync(CancellationToken.None));

        Assert.Equal(ProfileOnlyPaths, handler.RequestPaths);
    }

    /// <summary>
    /// Confirms that an unparseable profile payload follows the documented schema fallback rather than reporting a false balance.
    /// </summary>
    [Fact]
    public async Task GetBalanceAsyncWhenProfilePayloadIsMalformedFallsBackToBilling()
    {
        var handler = new SequenceHttpMessageHandler(
            CreateResponse(HttpStatusCode.OK, "{not-json"),
            CreateResponse(HttpStatusCode.OK, "{\"hard_limit_usd\":20}"),
            CreateResponse(HttpStatusCode.OK, "{\"total_usage\":650}"));
        using var httpClient = new HttpClient(handler);
        var client = new MicuBalanceClient(httpClient, new MicuClientOptions(MicuEndpointKind.MicuApi, "test-key"));

        MicuBalanceSnapshot result = await client.GetBalanceAsync(CancellationToken.None);

        Assert.Equal(13.5m, result.Amount);
        Assert.Equal(BillingFallbackPaths, handler.RequestPaths);
    }

    /// <summary>
    /// Prevents rate-limit and server failures from being multiplied into billing fallback requests every polling interval.
    /// </summary>
    [Theory]
    [InlineData(429)]
    [InlineData(500)]
    public async Task GetBalanceAsyncWhenProfileServiceFailsDoesNotFallback(int statusCode)
    {
        var handler = new SequenceHttpMessageHandler(CreateResponse((HttpStatusCode)statusCode, "{}"));
        using var httpClient = new HttpClient(handler);
        var client = new MicuBalanceClient(httpClient, new MicuClientOptions(MicuEndpointKind.MicuApi, "test-key"));

        await Assert.ThrowsAsync<MicuApiException>(() => client.GetBalanceAsync(CancellationToken.None));

        Assert.Equal(ProfileOnlyPaths, handler.RequestPaths);
    }

    /// <summary>
    /// Confirms that the legacy Subscription mode reads its direct balance field without querying the current API host.
    /// </summary>
    [Fact]
    public async Task GetBalanceAsyncWhenSubscriptionModeIsSelectedReadsDirectBalance()
    {
        var handler = new SequenceHttpMessageHandler(
            CreateResponse(HttpStatusCode.OK, "{\"balance\":7.25,\"planName\":\"Demo\"}"));
        using var httpClient = new HttpClient(handler);
        var client = new MicuBalanceClient(httpClient, new MicuClientOptions(MicuEndpointKind.MicuSubscription, "test-key"));

        MicuBalanceSnapshot result = await client.GetBalanceAsync(CancellationToken.None);

        Assert.Equal(7.25m, result.Amount);
        Assert.Equal("Demo", result.PlanName);
        Assert.Equal(SubscriptionPaths, handler.RequestPaths);
    }

    /// <summary>
    /// Prevents a local configuration mistake from constructing a network client with no credential.
    /// </summary>
    [Fact]
    public void MicuClientOptionsWhenKeyIsBlankRejectsTheConfiguration()
    {
        Assert.Throws<ArgumentException>(() => new MicuClientOptions(MicuEndpointKind.MicuApi, "  "));
    }

    /// <summary>
    /// Verifies that the client applies its deadline while JSON content is still being read after response headers arrived.
    /// </summary>
    [Fact]
    public async Task GetBalanceAsyncWhenResponseBodyStallsHonorsThePerRequestDeadline()
    {
        var handler = new SequenceHttpMessageHandler(CreateBlockingResponse());
        using var httpClient = new HttpClient(handler);
        var client = new MicuBalanceClient(
            httpClient,
            new MicuClientOptions(MicuEndpointKind.MicuApi, "test-key"),
            TimeSpan.FromMilliseconds(100));
        System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetBalanceAsync(CancellationToken.None));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), "A stalled response body must be canceled by the per-request deadline.");
    }

    /// <summary>
    /// Creates a deterministic JSON HTTP response for a query-contract test.
    /// </summary>
    private static HttpResponseMessage CreateResponse(HttpStatusCode statusCode, string content)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json")
        };
    }

    /// <summary>
    /// Creates a successful header response whose JSON stream never yields bytes, exercising the content-read timeout boundary.
    /// </summary>
    private static HttpResponseMessage CreateBlockingResponse()
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new BlockingReadStream())
        };
    }

    /// <summary>
    /// Returns prepared responses in order while recording only non-secret request metadata for assertions.
    /// </summary>
    private sealed class SequenceHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;

        /// <summary>
        /// Creates a handler that fails the test if the client performs more requests than the expected fallback sequence.
        /// </summary>
        public SequenceHttpMessageHandler(params HttpResponseMessage[] responses)
        {
            _responses = new Queue<HttpResponseMessage>(responses);
        }

        /// <summary>
        /// Gets request paths in the order the core client attempted them.
        /// </summary>
        public List<string> RequestPaths { get; } = [];

        /// <summary>
        /// Gets authorization schemes only, deliberately excluding the sensitive credential value.
        /// </summary>
        public List<string?> AuthorizationSchemes { get; } = [];

        /// <summary>
        /// Records a request and supplies the next mocked response without forwarding traffic to the network.
        /// </summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestPaths.Add(request.RequestUri?.AbsolutePath ?? string.Empty);
            AuthorizationSchemes.Add(request.Headers.Authorization?.Scheme);

            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("The balance client performed an unexpected extra request.");
            }

            return Task.FromResult(_responses.Dequeue());
        }
    }

    /// <summary>
    /// Simulates an upstream server that sent headers but never completes its response body.
    /// </summary>
    private sealed class BlockingReadStream : Stream
    {
        /// <summary>
        /// Gets whether the synthetic stream can be read asynchronously.
        /// </summary>
        public override bool CanRead => true;

        /// <summary>
        /// Gets whether the synthetic stream can seek; it cannot because it represents a live stalled connection.
        /// </summary>
        public override bool CanSeek => false;

        /// <summary>
        /// Gets whether the synthetic stream can write; it cannot because it models a response body.
        /// </summary>
        public override bool CanWrite => false;

        /// <summary>
        /// Gets an unknown content length for the stalled response stream.
        /// </summary>
        public override long Length => throw new NotSupportedException();

        /// <summary>
        /// Gets the current stream position, which is unavailable for a non-seekable response body.
        /// </summary>
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        /// <summary>
        /// Completes synchronous flushing because this is a read-only response stream with no output buffer.
        /// </summary>
        public override void Flush()
        {
        }

        /// <summary>
        /// Completes asynchronous flushing immediately because no output buffer exists.
        /// </summary>
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        /// <summary>
        /// Rejects synchronous reads so the test exercises the cancellation-aware asynchronous parser path.
        /// </summary>
        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        /// <summary>
        /// Waits forever unless the supplied cancellation token enforces the client's request deadline.
        /// </summary>
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return WaitForCancellationAsync(cancellationToken);
        }

        /// <summary>
        /// Waits forever unless the supplied cancellation token enforces the client's request deadline.
        /// </summary>
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return new ValueTask<int>(WaitForCancellationAsync(cancellationToken));
        }

        /// <summary>
        /// Rejects seek requests because a live HTTP response body has no stable random-access position.
        /// </summary>
        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        /// <summary>
        /// Rejects length changes because a live HTTP response body is read-only.
        /// </summary>
        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        /// <summary>
        /// Rejects writes because this stream only models an upstream response body.
        /// </summary>
        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        /// <summary>
        /// Waits under cancellation rather than returning artificial EOF, ensuring the test detects a missing deadline.
        /// </summary>
        private static async Task<int> WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
