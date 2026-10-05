using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Litos.Agent.Providers;

namespace Litos.SoftwareFactory.Host.Gateway;

/// <summary>
/// Classifies a provider's failure, whichever provider raised it. The providers report failures in
/// different shapes: OpenRouter, MeshAPI and the local server make their own HTTP calls and raise
/// HttpRequestException or ChatProviderRateLimitedException; the Anthropic, OpenAI and Gemini SDKs
/// raise their own exception types, often with the HTTP status as a property and the transport
/// failure inside; an HttpClient timeout arrives as a cancellation wrapping a TimeoutException. So
/// this walks the whole exception chain and reads a status code wherever one is exposed.
/// </summary>
public static class ProviderFailures
{
    /// <summary>The provider is asking for fewer requests (HTTP 429): wait, then send it again.</summary>
    public static bool IsRateLimit(Exception exception) =>
        Chain(exception).Any(e => e is ChatProviderRateLimitedException || StatusOf(e) == 429);

    /// <summary>
    /// The connection failed, or the provider was briefly unable to answer (408, 500, 502, 503,
    /// 504, or Anthropic's 529 "overloaded"): worth sending again. A request the provider rejected
    /// (400, 401, 404 and the like) is not.
    /// </summary>
    public static bool IsTransient(Exception exception)
    {
        foreach (var e in Chain(exception))
        {
            if (e is IOException or TimeoutException or SocketException)
                return true;
            if (StatusOf(e) is { } status)
                return status is 408 or 500 or 502 or 503 or 504 or 529;
            if (e is HttpRequestException)
                return true; // no status: the request never got an answer
        }

        return false;
    }

    /// <summary>The exception and everything wrapped inside it, outermost first.</summary>
    private static IEnumerable<Exception> Chain(Exception exception)
    {
        var pending = new Stack<Exception>([exception]);
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        while (pending.Count > 0)
        {
            var e = pending.Pop();
            if (!seen.Add(e))
                continue;
            yield return e;
            if (e is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions.Reverse())
                    pending.Push(inner);
            }
            else if (e.InnerException is { } inner)
            {
                pending.Push(inner);
            }
        }
    }

    /// <summary>
    /// The HTTP status an exception carries: HttpRequestException.StatusCode, or a public "Status"
    /// or "StatusCode" property holding a number or an HttpStatusCode, as the SDKs' own exception
    /// types do (System.ClientModel's ClientResultException has Status).
    /// </summary>
    internal static int? StatusOf(Exception exception)
    {
        if (exception is HttpRequestException http)
            return http.StatusCode is { } code ? (int)code : null;

        foreach (var name in new[] { "StatusCode", "Status" })
        {
            var property = exception.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property is null || property.GetIndexParameters().Length > 0)
                continue;
            switch (property.GetValue(exception))
            {
                case int value when value is >= 100 and < 600:
                    return value;
                case HttpStatusCode value:
                    return (int)value;
            }
        }

        return null;
    }
}
