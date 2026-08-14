using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Litos.Api.Auth;

public static class AdminTokenAuthenticationDefaults
{
    public const string AuthenticationScheme = "AdminToken";
}

/// <summary>
/// Authenticates `Authorization: Bearer &lt;ADMIN_TOKEN&gt;` as the same bootstrap/superuser
/// identity AdminTokenFilter previously granted ad hoc — reimplemented as a real ASP.NET Core
/// AuthenticationHandler (rather than the old IEndpointFilter) so it participates in
/// HttpContext.User/[Authorize] alongside per-user cookie auth (Program.cs's
/// "AdminOrUser" policy), instead of two disconnected gates. ADMIN_TOKEN's success/failure
/// semantics (missing header, wrong prefix, constant-time compare) are unchanged from
/// AdminTokenFilter.
/// </summary>
public sealed class AdminTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    AdminTokenProvider tokenProvider)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers[HeaderNames.Authorization].ToString();
        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.Ordinal))
            return Task.FromResult(AuthenticateResult.NoResult());

        var presented = header[prefix.Length..];
        if (!tokenProvider.IsValid(presented))
            return Task.FromResult(AuthenticateResult.Fail("Invalid admin token."));

        var identity = new ClaimsIdentity(
            [new Claim(AdminAuthEndpoints.AdminClaimType, "true")], AdminTokenAuthenticationDefaults.AuthenticationScheme);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), AdminTokenAuthenticationDefaults.AuthenticationScheme);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
