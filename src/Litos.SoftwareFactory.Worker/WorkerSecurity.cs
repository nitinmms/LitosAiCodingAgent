using System.Security.Cryptography;
using System.Text;
using Litos.SoftwareFactory.Contracts;

namespace Litos.SoftwareFactory.Worker;

/// <summary>
/// The worker listens on loopback, which any local process can reach, and it runs commands. So
/// every request must carry the secret the host generated for this launch, and must be addressed
/// to a loopback name — the second check stops a web page in a local browser from reaching the
/// worker through a hostname it has pointed at 127.0.0.1 (DNS rebinding).
/// </summary>
public static class WorkerSecurity
{
    public static IApplicationBuilder UseFactoryWorkerSecurity(this IApplicationBuilder app, string secret)
    {
        var expected = Encoding.UTF8.GetBytes(secret);
        return app.Use(async (context, next) =>
        {
            if (!IsLoopbackHost(context.Request.Host.Host))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            if (!HasSecret(context.Request.Headers[FactoryWire.SecretHeader].ToString(), expected))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            await next(context);
        });
    }

    internal static bool IsLoopbackHost(string host) =>
        host is "127.0.0.1" or "[::1]" or "::1" || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase);

    /// <summary>Compared in constant time, so the response time says nothing about how much of a
    /// guessed secret was right.</summary>
    internal static bool HasSecret(string presented, byte[] expected) =>
        presented.Length > 0 && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), expected);
}
