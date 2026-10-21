using Litos.SoftwareFactory.Core.Store;
using Microsoft.EntityFrameworkCore;

namespace Litos.SoftwareFactory.Infrastructure.Persistence;

/// <summary>
/// Personal access tokens (ReadMe_LitosSoftwareFactory_V1.md §26.3): made and revoked with an
/// audit record, like invitations; using one is a read and is not audited, only noted on the token.
/// </summary>
public sealed partial class EfFactoryStore
{
    public async Task<AccessToken> AddAccessTokenAsync(AccessToken token, CancellationToken ct)
    {
        await using var write = await BeginWriteAsync(ct);
        var db = write.Db;
        if (token.Id == Guid.Empty)
            token.Id = Guid.NewGuid();
        db.AccessTokens.Add(token);
        Audit(db, token.UserId, AuditActions.AccessTokenCreate, AuditTargets.AccessToken, token.Id, null, new { token.Name }, token.CreatedAt);
        await write.CommitAsync(ct);
        return token;
    }

    public async Task<IReadOnlyList<AccessToken>> ListAccessTokensAsync(Guid? userId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var tokens = db.AccessTokens.AsNoTracking();
        if (userId is { } owner)
            tokens = tokens.Where(t => t.UserId == owner);
        return await tokens.OrderByDescending(t => t.CreatedAt).ToListAsync(ct);
    }

    public async Task<AccessToken?> FindAccessTokenAsync(string tokenHash, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.AccessTokens.AsNoTracking().FirstOrDefaultAsync(t => t.TokenHash == tokenHash, ct);
    }

    public async Task<AccessToken?> GetAccessTokenAsync(Guid tokenId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.AccessTokens.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tokenId, ct);
    }

    public async Task RevokeAccessTokenAsync(Guid tokenId, Guid actorId, DateTimeOffset now, CancellationToken ct)
    {
        await using var write = await BeginWriteAsync(ct);
        var db = write.Db;

        // Conditional, so two revokes at the same moment record one.
        var revoked = await db.AccessTokens
            .Where(t => t.Id == tokenId && t.RevokedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(t => t.RevokedAt, now).SetProperty(t => t.RevokedBy, actorId), ct);
        if (revoked == 0)
        {
            _ = await db.AccessTokens.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tokenId, ct)
                ?? throw new StoreNotFoundException("The access token does not exist.");
            throw new StoreConflictException("This access token has already been revoked.");
        }

        Audit(db, actorId, AuditActions.AccessTokenRevoke, AuditTargets.AccessToken, tokenId, null, null, now);
        await write.CommitAsync(ct);
    }

    public async Task MarkAccessTokenUsedAsync(Guid tokenId, DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await db.AccessTokens.Where(t => t.Id == tokenId).ExecuteUpdateAsync(set => set.SetProperty(t => t.LastUsedAt, now), ct);
    }
}
