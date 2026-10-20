using System.Security.Cryptography;
using System.Text;
using Litos.SoftwareFactory.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;

namespace Litos.SoftwareFactory.Host.Api;

/// <summary>
/// The installed factory's launcher talks to its own host here (SoftwareFactoryInstallation.md
/// §4.1): <c>litos-factory stop</c> stops a host that has no console to press Ctrl+C in, and
/// <c>litos-factory open</c> learns when the first sign-in password has been replaced. Mapped only
/// when FACTORY_CONTROL_TOKEN is set, answered only on loopback, and only with that token: the
/// launcher reads it from the same owner-only settings file the host does.
/// </summary>
public static class ControlApi
{
    /// <summary>Keep in step with HostClient.ControlHeader in litos-factory, which does not reference the host.</summary>
    public const string TokenHeader = "X-Factory-Control";

    public static IEndpointRouteBuilder MapFactoryControl(this IEndpointRouteBuilder app, FactoryOptions options)
    {
        if (string.IsNullOrEmpty(options.ControlToken))
            return app;

        var expected = Encoding.UTF8.GetBytes(options.ControlToken);
        var control = app.MapGroup("/control").AllowAnonymous().AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var given = Encoding.UTF8.GetBytes(http.Request.Headers[TokenHeader].ToString());
            var loopback = http.Connection.RemoteIpAddress is { } address && System.Net.IPAddress.IsLoopback(address);
            // Not found, whichever check failed: nobody learns that the endpoint exists.
            return loopback && CryptographicOperations.FixedTimeEquals(given, expected)
                ? await next(context)
                : Results.NotFound();
        });

        // As Ctrl+C would: a run cut off stays Running and the next start marks it Interrupted (§16).
        control.MapPost("/stop", (IHostApplicationLifetime lifetime) =>
        {
            lifetime.StopApplication();
            return Results.Accepted();
        });

        control.MapGet("/status", async (IServiceScopeFactory scopes) =>
        {
            using var scope = scopes.CreateScope();
            var admin = await scope.ServiceProvider.GetRequiredService<UserManager<FactoryUser>>().FindByNameAsync(options.AdminUser);
            return Results.Ok(new { adminMustChangePassword = admin?.MustChangePassword ?? false });
        });

        return app;
    }
}
