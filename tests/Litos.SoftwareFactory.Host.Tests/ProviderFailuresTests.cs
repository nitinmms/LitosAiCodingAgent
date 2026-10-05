using System.ClientModel;
using System.Net;
using System.Net.Sockets;
using Litos.Agent.Providers;
using Litos.SoftwareFactory.Host.Gateway;

namespace Litos.SoftwareFactory.Host.Tests;

/// <summary>
/// The gateway retries a call that failed before producing anything, and waits out a rate limit,
/// whichever provider the task uses. Providers report those failures in different shapes, so these
/// are the shapes: the providers that make their own HTTP calls (OpenRouter, MeshAPI, local), the
/// SDK-based ones (OpenAI's ClientResultException with a numeric Status; others with a StatusCode),
/// wrapped failures, and an HttpClient timeout.
/// </summary>
public class ProviderFailuresTests
{
    /// <summary>An SDK exception carrying its HTTP status the way the SDKs do.</summary>
    private sealed class SdkException(int status, Exception? inner = null) : Exception($"HTTP {status}", inner)
    {
        public int Status { get; } = status;
    }

    private sealed class SdkStatusCodeException(HttpStatusCode code) : Exception($"HTTP {(int)code}")
    {
        public HttpStatusCode StatusCode { get; } = code;
    }

    public static TheoryData<Exception> Transient() =>
    [
        new HttpRequestException("Connection refused."),
        new HttpRequestException("Bad gateway.", null, HttpStatusCode.BadGateway),
        new IOException("The connection was reset."),
        new TimeoutException("The operation has timed out."),
        new SocketException((int)SocketError.ConnectionReset),
        new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.", new TimeoutException()),
        new InvalidOperationException("The SDK gave up.", new IOException("Unable to read data from the transport connection.")),
        new AggregateException(new ArgumentException("unrelated"), new HttpRequestException("Connection refused.")),
        new SdkException(503),
        new SdkException(529), // Anthropic: overloaded
        new SdkStatusCodeException(HttpStatusCode.GatewayTimeout),
        new ClientResultException("The response ended early.", null, new IOException("The connection was reset.")),
    ];

    [Theory]
    [MemberData(nameof(Transient))]
    public void AConnectionFailureOrAServerError_IsTransient(Exception failure) =>
        Assert.True(ProviderFailures.IsTransient(failure), failure.ToString());

    public static TheoryData<Exception> NotTransient() =>
    [
        new HttpRequestException("Bad request.", null, HttpStatusCode.BadRequest),
        new HttpRequestException("Unauthorized.", null, HttpStatusCode.Unauthorized),
        new SdkException(404),
        new SdkStatusCodeException(HttpStatusCode.Forbidden),
        new InvalidOperationException("The model does not exist."),
        new NotSupportedException("Unsupported content block."),
        new SdkException(400, new IOException("not the cause")), // the provider answered: the status decides
    ];

    [Theory]
    [MemberData(nameof(NotTransient))]
    public void ARejectedRequest_IsNotTransient(Exception failure) =>
        Assert.False(ProviderFailures.IsTransient(failure), failure.ToString());

    public static TheoryData<Exception> RateLimits() =>
    [
        new ChatProviderRateLimitedException("OpenRouter is rate-limiting requests."),
        new HttpRequestException("Too many requests.", null, HttpStatusCode.TooManyRequests),
        new SdkException(429),
        new SdkStatusCodeException(HttpStatusCode.TooManyRequests),
        new InvalidOperationException("Wrapped.", new SdkException(429)),
    ];

    [Theory]
    [MemberData(nameof(RateLimits))]
    public void A429_FromAnyProvider_IsARateLimit(Exception failure) =>
        Assert.True(ProviderFailures.IsRateLimit(failure), failure.ToString());

    [Fact]
    public void OtherFailures_AreNotRateLimits()
    {
        Assert.False(ProviderFailures.IsRateLimit(new SdkException(503)));
        Assert.False(ProviderFailures.IsRateLimit(new IOException("reset")));
        Assert.False(ProviderFailures.IsRateLimit(new InvalidOperationException("no")));
    }

    [Fact]
    public void AStatusPropertyThatIsNotAnHttpStatus_IsIgnored()
    {
        // ClientResultException with no response reports Status 0.
        Assert.Null(ProviderFailures.StatusOf(new ClientResultException("no response")));
        Assert.False(ProviderFailures.IsTransient(new ClientResultException("no response")));
    }
}
