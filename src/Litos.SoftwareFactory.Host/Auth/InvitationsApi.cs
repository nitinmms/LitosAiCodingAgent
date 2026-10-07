using System.Buffers.Text;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;

namespace Litos.SoftwareFactory.Host.Auth;

public sealed record CreateInvitationRequest(string? UserName, string? Email, string? Role, IReadOnlyList<Guid>? ProjectIds);

public sealed record InvitationTokenRequest(string? Token);

public sealed record AcceptInvitationRequest(string? Token, string? Password, string? DisplayName);

/// <summary>
/// Invitations (§13.3: Admins invite users; there is no self-service sign-up;
/// docs/software-factory/m2-architecture.md §4). An Admin creates one and is shown its one-time
/// link once, to send by any channel. Only the SHA-256 of the link's token is stored. The invitee
/// opens the link, chooses a password, and is signed in with the invitation's role and projects.
/// The token travels in request bodies, never in a URL the host would log.
/// </summary>
public static class InvitationsApi
{
    /// <summary>Checking and accepting a link are anonymous, so they are rate limited against
    /// guessing tokens, separately from sign-in.</summary>
    public const string RateLimit = "invitation";

    private const string InvalidLink = "This invitation link is not valid. Check that it was copied whole, or ask an Admin for a new one.";

    internal static string NewToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    internal static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static IEndpointRouteBuilder MapFactoryInvitations(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/invitations").RequireAuthorization(FactoryRoles.Admin);

        admin.MapPost("", async (
            CreateInvitationRequest request, ClaimsPrincipal principal, UserManager<FactoryUser> users, IFactoryStore store,
            FactoryOptions options, IClock clock, CancellationToken ct) =>
        {
            var userName = request.UserName?.Trim() ?? "";
            if (userName.Length is 0 or > 100 || userName.Any(c => !users.Options.User.AllowedUserNameCharacters.Contains(c)))
                return Problem("userName is required: up to 100 letters, digits and - . _ @ +.");
            if (!Enum.TryParse<AccountRole>(string.IsNullOrWhiteSpace(request.Role) ? nameof(AccountRole.Member) : request.Role.Trim(), ignoreCase: true, out var role)
                || !Enum.IsDefined(role))
            {
                return Problem("role must be Member or Admin.");
            }

            if (await users.FindByNameAsync(userName) is not null)
                return Results.Conflict(new { error = $"Someone already signs in as {userName}." });
            var now = clock.UtcNow;
            if ((await store.ListInvitationsAsync(ct)).Any(i => string.Equals(i.UserName, userName, StringComparison.OrdinalIgnoreCase) && i.IsUsable(now)))
                return Results.Conflict(new { error = $"An invitation for {userName} is already waiting. Revoke it to send a new one." });

            var projectIds = (request.ProjectIds ?? []).Distinct().ToList();
            foreach (var projectId in projectIds)
            {
                if (await store.GetProjectAsync(projectId, ct) is null)
                    return Problem($"Project {projectId} does not exist.");
            }

            var token = NewToken();
            var invitation = await store.AddInvitationAsync(new Invitation
            {
                UserName = userName,
                Email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim(),
                Role = role,
                ProjectIdsJson = JsonSerializer.Serialize(projectIds),
                TokenHash = Hash(token),
                ExpiresAt = now + options.InvitationLifetime,
                CreatedBy = principal.UserId(),
                CreatedAt = now,
            }, ct);

            // The only time the link exists anywhere: it cannot be shown again.
            return Results.Created($"/api/invitations/{invitation.Id}", new { Invitation = View(invitation, now), Link = $"#/invite/{token}" });
        });

        admin.MapGet("", async (IFactoryStore store, IClock clock, CancellationToken ct) =>
        {
            var now = clock.UtcNow;
            return Results.Ok((await store.ListInvitationsAsync(ct)).Select(i => View(i, now)));
        });

        admin.MapPost("/{id:guid}/revoke", async (Guid id, ClaimsPrincipal principal, IFactoryStore store, IClock clock, CancellationToken ct) =>
        {
            try
            {
                await store.RevokeInvitationAsync(id, principal.UserId(), clock.UtcNow, ct);
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

        var open = app.MapGroup("/api/invitations").AllowAnonymous().RequireRateLimiting(RateLimit);

        // What the invitation page shows before the invitee chooses a password.
        open.MapPost("/lookup", async (InvitationTokenRequest request, IFactoryStore store, IClock clock, CancellationToken ct) =>
        {
            var (invitation, refusal) = await FindUsableAsync(request.Token, store, clock.UtcNow, ct);
            return refusal ?? Results.Ok(new { invitation!.UserName, Role = invitation.Role.ToString(), invitation.ExpiresAt });
        });

        open.MapPost("/accept", async (
            AcceptInvitationRequest request, UserManager<FactoryUser> users, SignInManager<FactoryUser> signIn, IFactoryStore store,
            IClock clock, ILoggerFactory loggers, CancellationToken ct) =>
        {
            var now = clock.UtcNow;
            var (invitation, refusal) = await FindUsableAsync(request.Token, store, now, ct);
            if (refusal is not null)
                return refusal;
            if (await users.FindByNameAsync(invitation!.UserName) is not null)
                return Results.Conflict(new { error = $"Someone already signs in as {invitation.UserName}. Ask an Admin for a new invitation." });

            var user = new FactoryUser
            {
                Id = Guid.NewGuid(),
                UserName = invitation.UserName,
                Email = invitation.Email,
                DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? invitation.UserName : request.DisplayName.Trim(),
            };
            var created = await users.CreateAsync(user, request.Password ?? "");
            if (!created.Succeeded)
                return Problem(string.Join(" ", created.Errors.Select(e => e.Description)));
            await users.AddToRoleAsync(user, invitation.Role.ToString());

            try
            {
                await store.AcceptInvitationAsync(invitation.Id, user.Id, now, ct);
            }
            catch (Exception ex) when (ex is StoreConflictException or StoreNotFoundException)
            {
                // Someone used or revoked the link a moment ago. This account was never anyone's:
                // it is removed, not kept disabled.
                loggers.CreateLogger("Litos.SoftwareFactory.Host.Invitations").LogWarning(ex, "Invitation {InvitationId} could not be accepted.", invitation.Id);
                await users.DeleteAsync(user);
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status410Gone);
            }

            await signIn.SignInAsync(user, isPersistent: false);
            return Results.Ok(new CurrentUser(user.Id, user.UserName, user.DisplayName, [invitation.Role.ToString()]));
        });

        return app;
    }

    /// <summary>The invitation a token belongs to, or the response that refuses it: 404 for a
    /// token that matches nothing, 410 for one that has been used, revoked or has expired.</summary>
    private static async Task<(Invitation? Invitation, IResult? Refusal)> FindUsableAsync(
        string? token, IFactoryStore store, DateTimeOffset now, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || await store.FindInvitationAsync(Hash(token.Trim()), ct) is not { } invitation)
            return (null, Results.NotFound(new { error = InvalidLink }));
        if (invitation.UnusableReason(now) is { } reason)
            return (null, Results.Json(new { error = reason }, statusCode: StatusCodes.Status410Gone));
        return (invitation, null);
    }

    private static object View(Invitation invitation, DateTimeOffset now) => new
    {
        invitation.Id,
        invitation.UserName,
        invitation.Email,
        Role = invitation.Role.ToString(),
        ProjectIds = JsonSerializer.Deserialize<List<Guid>>(invitation.ProjectIdsJson) ?? [],
        Status = invitation.AcceptedAt is not null ? "Accepted" : invitation.RevokedAt is not null ? "Revoked" : now >= invitation.ExpiresAt ? "Expired" : "Pending",
        invitation.CreatedBy,
        invitation.CreatedAt,
        invitation.ExpiresAt,
        invitation.AcceptedAt,
        invitation.AcceptedUserId,
        invitation.RevokedAt,
    };

    private static IResult Problem(string message) => Results.BadRequest(new { error = message });
}
