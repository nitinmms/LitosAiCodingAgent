using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Litos.Api.Data;

/// <summary>
/// Thin wrapper over LitosDbContext for user provisioning/authentication — kept separate from
/// the DbContext itself so callers (Blazor pages, auth endpoints) don't need EF Core types in
/// their own signatures. Admin-provisioned only: no self-service registration endpoint exists
/// anywhere, by design (accounts are created from the Users admin page).
/// </summary>
public sealed class UserStore(LitosDbContext db)
{
    // PasswordHasher<TUser> only uses TUser for its generic constraint, not its fields — a
    // standalone Argon2-family (PBKDF2) hasher without pulling in full ASP.NET Core Identity
    // (roles, lockout, email confirmation) for what is otherwise a single-table user store.
    private static readonly PasswordHasher<User> Hasher = new();

    public async Task<User?> FindByUsernameAsync(string username, CancellationToken ct)
        => await db.Users.SingleOrDefaultAsync(u => u.Username == username, ct);

    public async Task<IReadOnlyList<User>> ListAsync(CancellationToken ct)
        => await db.Users.OrderBy(u => u.Username).ToListAsync(ct);

    public async Task<User> CreateAsync(string username, string password, bool isAdmin, CancellationToken ct)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = username,
            PasswordHash = string.Empty,
            IsAdmin = isAdmin,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        user.PasswordHash = Hasher.HashPassword(user, password);

        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        return user;
    }

    public async Task SetDisabledAsync(Guid userId, bool disabled, CancellationToken ct)
    {
        var user = await db.Users.SingleAsync(u => u.Id == userId, ct);
        user.DisabledAt = disabled ? DateTimeOffset.UtcNow : null;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Null if the username doesn't exist, is disabled, or the password doesn't match.</summary>
    public async Task<User?> ValidateCredentialsAsync(string username, string password, CancellationToken ct)
    {
        var user = await FindByUsernameAsync(username, ct);
        if (user is null || user.DisabledAt is not null)
            return null;

        var result = Hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        return result is PasswordVerificationResult.Success or PasswordVerificationResult.SuccessRehashNeeded
            ? user
            : null;
    }
}
