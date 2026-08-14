using Microsoft.EntityFrameworkCore;

namespace Litos.Api.Data;

public sealed class LitosDbContext(DbContextOptions<LitosDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(entity =>
        {
            entity.HasIndex(u => u.Username).IsUnique();
            entity.Property(u => u.Username).HasMaxLength(64);
        });

        modelBuilder.Entity<RefreshToken>(entity =>
        {
            // Looked up by hash on every /auth/token/refresh call — not unique (a hash collision
            // is cryptographically implausible, not schema-enforced, since the real uniqueness
            // guarantee comes from the token generator, not this index).
            entity.HasIndex(t => t.TokenHash);
            entity.HasIndex(t => t.UserId);
            entity.Property(t => t.TokenHash).HasMaxLength(128);
        });
    }
}
