using System.Security.Claims;
using System.Text.Encodings.Web;
using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Litos.SoftwareFactory.Host.Auth;

public sealed record CreateAccessTokenRequest(string? Name);

/// <summary>
/// Personal access tokens (ReadMe_LitosSoftwareFactory_V1.md §26.3), for the tray app: a program
/// on someone's PC, on this machine or across the network, that cannot hold a browser session.
/// An account makes a token in the web app and is shown it once; only its SHA-256 is stored.
/// Sent as <c>Authorization: Bearer lsf_…</c>, it signs a request in as its owner, with their
/// roles and projects, but reads only who they are, the task list and the board's event stream:
/// a token that leaks shows task titles and changes nothing. Tokens of a disabled account stop
/// working; the owner, or any Admin, revokes one at once, open streams included.
/// </summary>
public static class AccessTokensApi
{
    /// <summary>The scheme that checks a Bearer token.</summary>
    public const string Scheme = "FactoryAccessToken";

    /// <summary>The default scheme: a Bearer token when the request carries one, else the session cookie.</summary>
    public const string SelectorScheme = "FactoryCookieOrToken";

    /// <summary>Every token starts with this, so one pasted into the wrong place is recognisable.</summary>
    public const string Prefix = "lsf_";

    /// <summary>Carried by a request signed in with a token: the token's id.</summary>
    public const string TokenIdClaim = "factory:access_token";

    /// <summary>How many tokens one account may have that still work.</summary>
    public const int MaxActivePerUser = 20;

    /// <summary>The only calls a token may make, all GET.</summary>
    private static readonly string[] Readable = ["/api/auth/me", "/api/threads", "/api/events"];

    /// <summary>Using a token is noted on it at most this often, so a busy client does not write on every call.</summary>
    private static readonly TimeSpan UseNotedEvery = TimeSpan.FromMinutes(1);

    internal static string? BearerOf(HttpRequest request)
    {
        // "Bearer" with nothing after it is still an attempt at a token, and is refused as one.
        var header = request.Headers.Authorization.ToString().Trim();
        return header.Equals("Bearer", StringComparison.OrdinalIgnoreCase) || header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer".Length..].Trim()
            : null;
    }

    public static bool IsTokenRequest(ClaimsPrincipal user) => user.HasClaim(c => c.Type == TokenIdClaim);

    public static AuthenticationBuilder AddFactoryAccessTokens(this AuthenticationBuilder builder) => builder
        .AddPolicyScheme(SelectorScheme, null, options =>
            options.ForwardDefaultSelector = context => BearerOf(context.Request) is not null ? Scheme : IdentityConstants.ApplicationScheme)
        .AddScheme<AuthenticationSchemeOptions, AccessTokenHandler>(Scheme, null);

    /// <summary>Refuses everything but the readable calls to a request signed in with a token. Runs after authentication.</summary>
    public static IApplicationBuilder UseFactoryAccessTokenScope(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        if (IsTokenRequest(context.User)
            && context.Request.Path.StartsWithSegments("/api")
            && !(HttpMethods.IsGet(context.Request.Method) && Readable.Any(path => context.Request.Path.Equals(path, StringComparison.OrdinalIgnoreCase))))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "An access token can only read your tasks and their events. Sign in to the web app to do anything else." });
            return;
        }

        await next(context);
    });

    /// <summary>
    /// Whether a request signed in with a token may go on: the token is not revoked and its owner
    /// not disabled. A cookie session is always let through here. For a stream that stays open,
    /// asked on every pass, so revoking a token ends the tray app's stream, not only its next call.
    /// </summary>
    public static async Task<bool> StillSignedInAsync(ClaimsPrincipal user, IDbContextFactory<FactoryDbContext> contexts, CancellationToken ct)
    {
        if (user.FindFirstValue(TokenIdClaim) is not { } id)
            return true;
        if (!Guid.TryParse(id, out var tokenId))
            return false;

        // A fresh context each time: a long-lived one would answer from what it read first.
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.AccessTokens.AnyAsync(t => t.Id == tokenId && t.RevokedAt == null && db.Users.Any(u => u.Id == t.UserId && !u.Disabled), ct);
    }

    public static IEndpointRouteBuilder MapFactoryAccessTokens(this IEndpointRouteBuilder app)
    {
        // Managed only from a browser session: the scope gate refuses all of these to a token.
        var tokens = app.MapGroup("/api/access-tokens").RequireAuthorization();

        tokens.MapGet("", async (bool? all, ClaimsPrincipal principal, IFactoryStore store, UserManager<FactoryUser> users, CancellationToken ct) =>
        {
            if (all == true && !ProjectAccess.IsAdmin(principal))
                return Results.Json(new { error = "Only an Admin sees everyone's access tokens." }, statusCode: StatusCodes.Status403Forbidden);

            var list = await store.ListAccessTokensAsync(all == true ? null : principal.UserId(), ct);
            var owners = list.Select(t => t.UserId).Distinct().ToList();
            var names = users.Users.Where(u => owners.Contains(u.Id)).ToDictionary(u => u.Id, u => u.UserName);
            return Results.Ok(list.Select(t => View(t, names.GetValueOrDefault(t.UserId))));
        });

        tokens.MapPost("", async (
            CreateAccessTokenRequest request, ClaimsPrincipal principal, IFactoryStore store, IClock clock, CancellationToken ct) =>
        {
            var name = request.Name?.Trim() ?? "";
            if (name.Length is 0 or > 100)
                return Results.BadRequest(new { error = "Give the token a name of up to 100 characters, such as the PC it is for." });

            var owner = principal.UserId();
            var active = (await store.ListAccessTokensAsync(owner, ct)).Count(t => t.RevokedAt is null);
            if (active >= MaxActivePerUser)
                return Results.Conflict(new { error = $"You already have {MaxActivePerUser} access tokens. Revoke one you no longer use first." });

            var secret = Prefix + InvitationsApi.NewToken();
            var token = await store.AddAccessTokenAsync(new AccessToken
            {
                UserId = owner,
                Name = name,
                TokenHash = InvitationsApi.Hash(secret),
                CreatedAt = clock.UtcNow,
            }, ct);

            // The only time the token exists anywhere: it cannot be shown again.
            return Results.Created($"/api/access-tokens/{token.Id}", new { Token = View(token, principal.Identity?.Name), Secret = secret });
        });

        tokens.MapPost("/{id:guid}/revoke", async (Guid id, ClaimsPrincipal principal, IFactoryStore store, IClock clock, CancellationToken ct) =>
        {
            // Someone else's token answers as one that does not exist, unless the caller is an Admin.
            if (await store.GetAccessTokenAsync(id, ct) is not { } token || (token.UserId != principal.UserId() && !ProjectAccess.IsAdmin(principal)))
                return Results.NotFound();
            try
            {
                await store.RevokeAccessTokenAsync(id, principal.UserId(), clock.UtcNow, ct);
                return Results.NoContent();
            }
            catch (StoreNotFoundException)
            {
                return Results.NotFound();
            }
            catch (StoreConflictException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        return app;
    }

    private static object View(AccessToken token, string? userName) => new
    {
        token.Id,
        token.UserId,
        UserName = userName,
        token.Name,
        Status = token.RevokedAt is null ? "Active" : "Revoked",
        token.CreatedAt,
        token.LastUsedAt,
        token.RevokedAt,
    };

    /// <summary>Signs a request in as the owner of the Bearer token it carries.</summary>
    internal sealed class AccessTokenHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
        IFactoryStore store, UserManager<FactoryUser> users, IUserClaimsPrincipalFactory<FactoryUser> principals, IClock clock)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        private const string Invalid = "The access token is not valid. Create a new one in the web app, under Settings → Tray app.";

        protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (BearerOf(Request) is not { } presented)
                return AuthenticateResult.NoResult();
            var ct = Context.RequestAborted;

            if (!presented.StartsWith(Prefix, StringComparison.Ordinal)
                || await store.FindAccessTokenAsync(InvitationsApi.Hash(presented), ct) is not { RevokedAt: null } token
                || await users.FindByIdAsync(token.UserId.ToString()) is not { Disabled: false } user)
            {
                return AuthenticateResult.Fail(Invalid);
            }

            var principal = await principals.CreateAsync(user);
            principal.Identities.First().AddClaim(new Claim(TokenIdClaim, token.Id.ToString()));

            var now = clock.UtcNow;
            if (token.LastUsedAt is not { } last || now - last >= UseNotedEvery)
                await store.MarkAccessTokenUsedAsync(token.Id, now, ct);

            return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
        }

        protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
        {
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            Response.Headers.WWWAuthenticate = "Bearer";
            await Response.WriteAsJsonAsync(new { error = Invalid });
        }
    }
}
