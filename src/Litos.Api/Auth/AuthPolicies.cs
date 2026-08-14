using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;

namespace Litos.Api.Auth;

public static class AuthPolicies
{
    /// <summary>Any signed-in caller — the litos_admin cookie, a per-user cookie, the ADMIN_TOKEN bearer scheme, or a JWT access token.</summary>
    public const string AdminOrUser = "AdminOrUser";

    /// <summary>
    /// Signed in AND carrying AdminClaimType — either the ADMIN_TOKEN bearer/cookie identity, or
    /// a per-user account with IsAdmin=true (via cookie or JWT). Gates Users.razor's @attribute —
    /// the page's own _isAdmin check only controls what markup renders, not whether a non-admin's
    /// InteractiveServer circuit can call the page's @code handlers at all, so the enforcement
    /// point has to be here (and, belt-and-suspenders, re-checked inside those handlers too —
    /// see Users.razor).
    /// </summary>
    public const string AdminOnly = "AdminOnly";

    public static AuthorizationOptions AddAdminOrUserPolicy(this AuthorizationOptions options)
    {
        var allSchemes = new[]
        {
            CookieAuthenticationDefaults.AuthenticationScheme,
            AdminTokenAuthenticationDefaults.AuthenticationScheme,
            JwtBearerDefaults.AuthenticationScheme,
        };

        options.AddPolicy(AdminOrUser, policy => policy
            .AddAuthenticationSchemes(allSchemes)
            .RequireAuthenticatedUser());

        options.AddPolicy(AdminOnly, policy => policy
            .AddAuthenticationSchemes(allSchemes)
            .RequireClaim(AdminAuthEndpoints.AdminClaimType));

        return options;
    }
}
