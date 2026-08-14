using System.Security.Claims;
using Litos.Agent.Session;

namespace Litos.Api.Auth;

/// <summary>
/// Derives the SessionOwner a Turns request should act as, from the caller ClaimsPrincipal that
/// [Authorize(Policy = AdminOrUserPolicy)] already populated (see Program.cs) — never from a
/// client-supplied field, per ReadMe_AgentDesign.md §10.4's isolation requirement. A per-user
/// cookie login carries UserIdClaimType and gets SessionOwner(that id); the ADMIN_TOKEN bearer
/// scheme carries only AdminClaimType and maps to the fixed SessionOwner.Local bucket used
/// before per-user accounts existed, so its existing sessions on disk keep resolving.
/// </summary>
public static class CurrentSessionOwner
{
    public static SessionOwner Resolve(ClaimsPrincipal user)
    {
        var userId = user.FindFirst(AdminAuthEndpoints.UserIdClaimType)?.Value;
        return userId is not null ? new SessionOwner(userId) : SessionOwner.Local;
    }
}
