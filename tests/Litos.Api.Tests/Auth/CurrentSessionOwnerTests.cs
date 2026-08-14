using System.Security.Claims;
using Litos.Agent.Session;
using Litos.Api.Auth;

namespace Litos.Api.Tests.Auth;

public class CurrentSessionOwnerTests
{
    [Fact]
    public void Resolve_UserIdClaimPresent_ReturnsOwnerKeyedByThatId()
    {
        var userId = Guid.NewGuid().ToString();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(AdminAuthEndpoints.UserIdClaimType, userId)]));

        var owner = CurrentSessionOwner.Resolve(principal);

        Assert.Equal(new SessionOwner(userId), owner);
    }

    [Fact]
    public void Resolve_OnlyAdminClaim_NoUserIdClaim_FallsBackToLocal()
    {
        // The ADMIN_TOKEN bearer scheme (AdminTokenAuthenticationHandler) only ever issues the
        // litos_admin claim, never a user id — it must keep resolving to the same
        // SessionOwner.Local bucket every pre-existing session on disk was written under, or
        // ADMIN_TOKEN callers would lose access to their own session history overnight.
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(AdminAuthEndpoints.AdminClaimType, "true")]));

        var owner = CurrentSessionOwner.Resolve(principal);

        Assert.Equal(SessionOwner.Local, owner);
    }

    [Fact]
    public void Resolve_NoClaimsAtAll_FallsBackToLocal()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity());

        var owner = CurrentSessionOwner.Resolve(principal);

        Assert.Equal(SessionOwner.Local, owner);
    }

    [Fact]
    public void Resolve_DifferentUserIds_ProduceDifferentOwners()
    {
        var principalA = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(AdminAuthEndpoints.UserIdClaimType, "user-a")]));
        var principalB = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(AdminAuthEndpoints.UserIdClaimType, "user-b")]));

        Assert.NotEqual(CurrentSessionOwner.Resolve(principalA), CurrentSessionOwner.Resolve(principalB));
    }
}
