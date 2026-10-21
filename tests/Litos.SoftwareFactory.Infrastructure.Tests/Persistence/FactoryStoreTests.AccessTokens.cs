using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Infrastructure.Persistence;

namespace Litos.SoftwareFactory.Infrastructure.Tests.Persistence;

/// <summary>Personal access tokens for the tray app (ReadMe_LitosSoftwareFactory_V1.md §26.3).</summary>
public abstract partial class FactoryStoreContract
{
    /// <summary>A token belongs to an account, so the account must exist.</summary>
    private async Task<Guid> AddUserAsync(string userName)
    {
        var id = Guid.NewGuid();
        await using var db = Contexts.CreateDbContext();
        db.Users.Add(new FactoryUser { Id = id, UserName = userName, NormalizedUserName = userName.ToUpperInvariant(), SecurityStamp = Guid.NewGuid().ToString() });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<AccessToken> AddTokenAsync(Guid userId, string name = "Work laptop", DateTimeOffset? at = null) =>
        await Store.AddAccessTokenAsync(new AccessToken
        {
            UserId = userId,
            Name = name,
            TokenHash = Guid.NewGuid().ToString("n") + Guid.NewGuid().ToString("n"),
            CreatedAt = at ?? T0,
        }, default);

    [SkippableFact]
    public async Task AccessToken_IsFoundByItsHash_AndItsCreationIsRecorded()
    {
        var ben = await AddUserAsync("ben");
        var token = await AddTokenAsync(ben);

        var found = await Store.FindAccessTokenAsync(token.TokenHash, default);
        Assert.Equal((token.Id, ben, "Work laptop", (DateTimeOffset?)null), (found!.Id, found.UserId, found.Name, found.RevokedAt));
        Assert.Equal(token.Id, (await Store.GetAccessTokenAsync(token.Id, default))!.Id);
        Assert.Null(await Store.FindAccessTokenAsync(new string('f', 64), default));
        Assert.Null(await Store.GetAccessTokenAsync(Guid.NewGuid(), default));

        var row = Assert.Single(await Store.ListAuditAsync(null, 10, default), r => r.TargetId == token.Id);
        Assert.Equal((ben, AuditActions.AccessTokenCreate, AuditTargets.AccessToken), (row.ActorId, row.Action, row.TargetType));
    }

    [SkippableFact]
    public async Task AccessTokens_AreListedNewestFirst_ForOneOwnerOrEveryone()
    {
        var ben = await AddUserAsync("ben");
        var erin = await AddUserAsync("erin");
        var older = await AddTokenAsync(ben, "Old PC", T0);
        var newer = await AddTokenAsync(ben, "New PC", T0.AddHours(1));
        var erins = await AddTokenAsync(erin, "Erin's PC", T0.AddHours(2));

        Assert.Equal([newer.Id, older.Id], (await Store.ListAccessTokensAsync(ben, default)).Select(t => t.Id));
        Assert.Equal([erins.Id, newer.Id, older.Id], (await Store.ListAccessTokensAsync(null, default)).Select(t => t.Id));
    }

    [SkippableFact]
    public async Task RevokingAnAccessToken_RecordsWhoAndWhen_Once()
    {
        var ben = await AddUserAsync("ben");
        var token = await AddTokenAsync(ben);

        await Store.RevokeAccessTokenAsync(token.Id, Admin, T0.AddHours(1), default);

        var revoked = await Store.GetAccessTokenAsync(token.Id, default);
        Assert.Equal((T0.AddHours(1), (Guid?)Admin), (revoked!.RevokedAt!.Value, revoked.RevokedBy));
        await Assert.ThrowsAsync<StoreConflictException>(() => Store.RevokeAccessTokenAsync(token.Id, ben, T0.AddHours(2), default));
        await Assert.ThrowsAsync<StoreNotFoundException>(() => Store.RevokeAccessTokenAsync(Guid.NewGuid(), ben, T0, default));

        var rows = (await Store.ListAuditAsync(null, 10, default)).Where(r => r.TargetId == token.Id && r.Action == AuditActions.AccessTokenRevoke);
        Assert.Equal(Admin, Assert.Single(rows).ActorId);
    }

    [SkippableFact]
    public async Task UsingAnAccessToken_IsNoted_AndNotAudited()
    {
        var ben = await AddUserAsync("ben");
        var token = await AddTokenAsync(ben);

        await Store.MarkAccessTokenUsedAsync(token.Id, T0.AddMinutes(5), default);

        Assert.Equal(T0.AddMinutes(5), (await Store.GetAccessTokenAsync(token.Id, default))!.LastUsedAt);
        Assert.Single(await Store.ListAuditAsync(null, 10, default), r => r.TargetId == token.Id);
    }
}
