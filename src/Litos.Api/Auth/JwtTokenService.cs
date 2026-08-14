using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Litos.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace Litos.Api.Auth;

public sealed record TokenPair(string AccessToken, string RefreshToken, DateTimeOffset AccessTokenExpiresAt);

/// <summary>
/// Issues short-lived JWT access tokens plus opaque, server-tracked refresh tokens for any
/// account (admin or non-admin) — the API/client auth path, separate from the cookie flow
/// AdminAuthEndpoints owns for the Blazor UI. Only RefreshTokens' hashes are ever persisted
/// (RefreshToken.TokenHash), so a database read alone can't produce a usable credential.
/// </summary>
public sealed class JwtTokenService(LitosDbContext db, JwtSigningKeyProvider signingKey)
{
    private static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromHours(1);
    private static readonly TimeSpan RefreshTokenLifetime = TimeSpan.FromDays(30);

    public async Task<TokenPair> IssueAsync(User user, CancellationToken ct)
    {
        var accessToken = CreateAccessToken(user, out var expiresAt);
        var refreshToken = await CreateRefreshTokenAsync(user.Id, ct);
        return new TokenPair(accessToken, refreshToken, expiresAt);
    }

    /// <summary>
    /// Null if the refresh token is unknown, expired, revoked, or its owning account is now
    /// disabled — all treated identically so a caller can't distinguish "wrong token" from
    /// "your account was disabled" via response shape. Rotates on every use: the presented
    /// token is revoked and a new one issued, so a stolen-then-replayed refresh token is only
    /// usable once before the legitimate client's next refresh silently invalidates it too
    /// (visible to an admin as unexpected re-auth, unlike a non-rotating token that stays
    /// silently valid for a thief indefinitely).
    /// </summary>
    public async Task<TokenPair?> RefreshAsync(string presentedRefreshToken, CancellationToken ct)
    {
        var hash = Hash(presentedRefreshToken);
        var stored = await db.RefreshTokens.SingleOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (stored is null || !stored.IsActive)
            return null;

        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == stored.UserId, ct);
        if (user is null || user.DisabledAt is not null)
            return null;

        // Not saved here — IssueAsync -> CreateRefreshTokenAsync's own SaveChangesAsync flushes
        // every tracked change on this DbContext, including this RevokedAt mutation, in the same
        // round trip as inserting the new RefreshToken row. A second SaveChangesAsync after
        // IssueAsync would be a no-op that costs an extra DB call on every refresh.
        stored.RevokedAt = DateTimeOffset.UtcNow;
        return await IssueAsync(user, ct);
    }

    /// <summary>Called when an account is disabled (UserStore.SetDisabledAsync) so a live refresh token can't keep minting fresh access tokens for it.</summary>
    public async Task RevokeAllForUserAsync(Guid userId, CancellationToken ct)
    {
        var active = await db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null).ToListAsync(ct);
        foreach (var token in active)
            token.RevokedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private string CreateAccessToken(User user, out DateTimeOffset expiresAt)
    {
        expiresAt = DateTimeOffset.UtcNow.Add(AccessTokenLifetime);

        var claims = new List<Claim> { new(AdminAuthEndpoints.UserIdClaimType, user.Id.ToString()) };
        if (user.IsAdmin)
            claims.Add(new Claim(AdminAuthEndpoints.AdminClaimType, "true"));

        var credentials = new SigningCredentials(signingKey.Key, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            JwtSigningKeyProvider.Issuer, JwtSigningKeyProvider.Audience, claims,
            expires: expiresAt.UtcDateTime, signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private async Task<string> CreateRefreshTokenAsync(Guid userId, CancellationToken ct)
    {
        var raw = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TokenHash = Hash(raw),
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.Add(RefreshTokenLifetime),
        });
        await db.SaveChangesAsync(ct);
        return raw;
    }

    // SHA-256, not a slow password hash — a refresh token is already 256 bits of random data
    // (unlike a human-chosen password), so a fast hash is fine and keeps refresh-token lookups
    // cheap on every /auth/token/refresh call.
    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
}
