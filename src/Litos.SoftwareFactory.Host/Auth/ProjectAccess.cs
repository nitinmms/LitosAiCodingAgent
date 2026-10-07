using System.Security.Claims;
using Litos.SoftwareFactory.Core.Store;

namespace Litos.SoftwareFactory.Host.Auth;

/// <summary>
/// Who may see which project (§13.3: every API call checks project membership;
/// docs/software-factory/m2-architecture.md §4). Admins see every project. A Member sees the
/// projects they belong to, and anything in another project answers 404, exactly as an id that does
/// not exist, so its threads and titles are never confirmed. This is the one place the rule lives:
/// routes that name a project's thread, decision or finding take it as an endpoint filter.
/// </summary>
public sealed class ProjectAccess(IFactoryStore store)
{
    public static bool IsAdmin(ClaimsPrincipal user) => user.IsInRole(FactoryRoles.Admin);

    /// <summary>The projects the user may see, or null for every project (an Admin).</summary>
    public async Task<IReadOnlySet<Guid>?> VisibleProjectsAsync(ClaimsPrincipal user, CancellationToken ct) =>
        IsAdmin(user) ? null : (await store.ListMemberProjectIdsAsync(user.UserId(), ct)).ToHashSet();

    public async Task<bool> CanSeeProjectAsync(ClaimsPrincipal user, Guid projectId, CancellationToken ct) =>
        IsAdmin(user) || await store.IsMemberAsync(projectId, user.UserId(), ct);

    /// <summary>Whether the thread, decision or finding exists and the user may see its project.</summary>
    public async Task<bool> CanSeeAsync(ClaimsPrincipal user, ProjectScoped kind, Guid id, CancellationToken ct) =>
        await store.FindProjectIdAsync(kind, id, ct) is { } projectId && await CanSeeProjectAsync(user, projectId, ct);
}

/// <summary>Marks an endpoint as checked by <see cref="ProjectAccess"/>, so a test can find any that is not.</summary>
public sealed record ProjectAccessMetadata(ProjectScoped Kind);

public static class ProjectAccessEndpoints
{
    /// <summary>
    /// Answers 404 before the handler runs when the route's <c>{id}</c> names a
    /// <paramref name="kind"/> the signed-in user may not see, or one that does not exist.
    /// </summary>
    public static TBuilder RequireProjectAccess<TBuilder>(this TBuilder builder, ProjectScoped kind)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new ProjectAccessMetadata(kind));
        return builder.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            if (!Guid.TryParse(http.GetRouteValue("id") as string, out var id))
                return Results.NotFound();

            var access = http.RequestServices.GetRequiredService<ProjectAccess>();
            return await access.CanSeeAsync(http.User, kind, id, http.RequestAborted)
                ? await next(context)
                : Results.NotFound();
        });
    }
}
