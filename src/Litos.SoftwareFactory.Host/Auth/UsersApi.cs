using System.Security.Claims;
using System.Text.Json;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;

namespace Litos.SoftwareFactory.Host.Auth;

public sealed record SetRoleRequest(string? Role);

public sealed record AddMemberRequest(Guid UserId);

/// <summary>
/// The Admin's people screen (§13.3, docs/software-factory/m2-architecture.md §4): who has an
/// account, their role, whether they are disabled, and which projects they belong to. People are
/// disabled, never deleted, so the history of every task keeps its authors. Disabling an account
/// rotates its security stamp, so its open sessions end at the next session check; a role change
/// takes effect at that check without signing anyone out. The last enabled Admin can be neither
/// disabled nor demoted, so the factory always has someone to run it.
/// </summary>
public static class UsersApi
{
    public static IEndpointRouteBuilder MapFactoryUsers(this IEndpointRouteBuilder app)
    {
        var users = app.MapGroup("/api/users").RequireAuthorization(FactoryRoles.Admin);

        users.MapGet("", async (UserManager<FactoryUser> manager, IFactoryStore store, CancellationToken ct) =>
        {
            var admins = (await manager.GetUsersInRoleAsync(FactoryRoles.Admin)).Select(u => u.Id).ToHashSet();
            var views = new List<object>();
            foreach (var user in manager.Users.OrderBy(u => u.UserName).ToList())
                views.Add(View(user, admins.Contains(user.Id), await store.ListMemberProjectIdsAsync(user.Id, ct)));
            return Results.Ok(views);
        });

        users.MapPost("/{id:guid}/disable", async (
            Guid id, ClaimsPrincipal principal, UserManager<FactoryUser> manager, IFactoryStore store, IClock clock, CancellationToken ct) =>
        {
            if (await manager.FindByIdAsync(id.ToString()) is not { } user)
                return Results.NotFound();
            if (user.Disabled)
                return Results.Conflict(new { error = $"{user.UserName} is already disabled." });
            if (id == principal.UserId())
                return Results.Conflict(new { error = "You cannot disable your own account. Ask another Admin." });
            if (await manager.IsInRoleAsync(user, FactoryRoles.Admin) && await EnabledAdminCountAsync(manager) <= 1)
                return Results.Conflict(new { error = $"{user.UserName} is the last enabled Admin. Make someone else an Admin first." });

            user.Disabled = true;
            await manager.UpdateAsync(user);
            // Ends every session the account has open, at its next check.
            await manager.UpdateSecurityStampAsync(user);
            await AuditAsync(store, principal, AuditActions.UserDisable, user.Id, new { user.UserName }, clock, ct);
            return Results.Ok(await ViewAsync(manager, store, user, ct));
        });

        users.MapPost("/{id:guid}/enable", async (
            Guid id, ClaimsPrincipal principal, UserManager<FactoryUser> manager, IFactoryStore store, IClock clock, CancellationToken ct) =>
        {
            if (await manager.FindByIdAsync(id.ToString()) is not { } user)
                return Results.NotFound();
            if (!user.Disabled)
                return Results.Conflict(new { error = $"{user.UserName} is not disabled." });

            user.Disabled = false;
            await manager.UpdateAsync(user);
            await AuditAsync(store, principal, AuditActions.UserEnable, user.Id, new { user.UserName }, clock, ct);
            return Results.Ok(await ViewAsync(manager, store, user, ct));
        });

        users.MapPost("/{id:guid}/role", async (
            Guid id, SetRoleRequest request, ClaimsPrincipal principal, UserManager<FactoryUser> manager, IFactoryStore store, IClock clock,
            CancellationToken ct) =>
        {
            if (!Enum.TryParse<AccountRole>(request.Role?.Trim(), ignoreCase: true, out var role) || !Enum.IsDefined(role))
                return Results.BadRequest(new { error = "role must be Member or Admin." });
            if (await manager.FindByIdAsync(id.ToString()) is not { } user)
                return Results.NotFound();

            var wasAdmin = await manager.IsInRoleAsync(user, FactoryRoles.Admin);
            if (wasAdmin == (role == AccountRole.Admin))
                return Results.Ok(await ViewAsync(manager, store, user, ct));
            if (wasAdmin && !user.Disabled && await EnabledAdminCountAsync(manager) <= 1)
                return Results.Conflict(new { error = $"{user.UserName} is the last enabled Admin. Make someone else an Admin first." });

            var (from, to) = wasAdmin ? (FactoryRoles.Admin, FactoryRoles.Member) : (FactoryRoles.Member, FactoryRoles.Admin);
            await manager.RemoveFromRoleAsync(user, from);
            await manager.AddToRoleAsync(user, to);
            await AuditAsync(store, principal, AuditActions.UserRole, user.Id, new { user.UserName, From = from, To = to }, clock, ct);
            return Results.Ok(await ViewAsync(manager, store, user, ct));
        });

        var members = app.MapGroup("/api/projects/{projectId:guid}/members").RequireAuthorization(FactoryRoles.Admin);

        members.MapGet("", async (Guid projectId, IFactoryStore store, CancellationToken ct) =>
            await store.GetProjectAsync(projectId, ct) is null
                ? Results.NotFound()
                : Results.Ok((await store.ListMembersAsync(projectId, ct)).Select(m => new { m.UserId, m.CreatedBy, m.CreatedAt })));

        members.MapPost("", async (
            Guid projectId, AddMemberRequest request, ClaimsPrincipal principal, UserManager<FactoryUser> manager, IFactoryStore store,
            IClock clock, CancellationToken ct) =>
        {
            if (await manager.FindByIdAsync(request.UserId.ToString()) is null)
                return Results.NotFound(new { error = "The user does not exist." });
            try
            {
                var added = await store.AddMemberAsync(projectId, request.UserId, principal.UserId(), clock.UtcNow, ct);
                return added ? Results.Created($"/api/projects/{projectId}/members/{request.UserId}", null) : Results.NoContent();
            }
            catch (StoreNotFoundException)
            {
                return Results.NotFound(new { error = "The project does not exist." });
            }
        });

        members.MapDelete("/{userId:guid}", async (
            Guid projectId, Guid userId, ClaimsPrincipal principal, IFactoryStore store, IClock clock, CancellationToken ct) =>
            await store.RemoveMemberAsync(projectId, userId, principal.UserId(), clock.UtcNow, ct) ? Results.NoContent() : Results.NotFound());

        return app;
    }

    /// <summary>Admins who can still sign in: the guard against a factory nobody can run.</summary>
    private static async Task<int> EnabledAdminCountAsync(UserManager<FactoryUser> manager) =>
        (await manager.GetUsersInRoleAsync(FactoryRoles.Admin)).Count(u => !u.Disabled);

    /// <summary>Identity changes are made through the UserManager, outside the store's
    /// transactions, so their audit row is written right after.</summary>
    private static Task AuditAsync(
        IFactoryStore store, ClaimsPrincipal principal, string action, Guid userId, object details, IClock clock, CancellationToken ct) =>
        store.AddAuditAsync(new AuditEvent
        {
            ActorId = principal.UserId(),
            Action = action,
            TargetType = AuditTargets.User,
            TargetId = userId,
            DetailsJson = JsonSerializer.Serialize(details, FactoryWire.Json),
            CreatedAt = clock.UtcNow,
        }, ct);

    private static async Task<object> ViewAsync(UserManager<FactoryUser> manager, IFactoryStore store, FactoryUser user, CancellationToken ct) =>
        View(user, await manager.IsInRoleAsync(user, FactoryRoles.Admin), await store.ListMemberProjectIdsAsync(user.Id, ct));

    private static object View(FactoryUser user, bool admin, IReadOnlyList<Guid> projectIds) => new
    {
        user.Id,
        user.UserName,
        user.DisplayName,
        Role = admin ? FactoryRoles.Admin : FactoryRoles.Member,
        user.Disabled,
        // Admins see every project whatever their membership rows say.
        ProjectIds = projectIds,
    };
}
