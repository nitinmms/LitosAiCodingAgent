using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;

namespace Litos.SoftwareFactory.Infrastructure.Persistence;

public static class FactoryDatabase
{
    public const string RuntimeConnectionName = "FactoryState";
    public const string MigrationsConnectionName = "FactoryMigrations";

    /// <summary>Registers the factory database on PostgreSQL and the store on top of it.</summary>
    public static IServiceCollection AddFactoryStore(this IServiceCollection services, string connectionString)
    {
        services.AddDbContextFactory<FactoryDbContext>(options => options.UseNpgsql(connectionString));
        services.AddSingleton<Core.Store.IFactoryStore, EfFactoryStore>();
        services.AddSingleton<Core.Ports.IHostInstanceLock>(_ => new PostgresHostInstanceLock(connectionString));
        return services;
    }

    /// <summary>
    /// Applies pending migrations. Run by the trusted setup command with the migration role,
    /// before dispatch starts — never on a normal start, and never by a coding task (§24).
    /// </summary>
    public static async Task MigrateAsync(string migrationsConnectionString, CancellationToken ct)
    {
        var options = new DbContextOptionsBuilder<FactoryDbContext>().UseNpgsql(migrationsConnectionString).Options;
        await using var db = new FactoryDbContext(options);
        await db.Database.MigrateAsync(ct);
    }

    /// <summary>
    /// How the database's schema compares with this build's. The host refuses to start dispatching
    /// when they differ in either direction: a schema and an application that disagree must fail
    /// clearly, not partly work.
    /// </summary>
    public static async Task<SchemaStatus> CheckSchemaAsync(IDbContextFactory<FactoryDbContext> factory, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await CheckSchemaAsync(db, ct);
    }

    /// <summary>The same, for a command that has a connection string and no host.</summary>
    public static async Task<SchemaStatus> CheckSchemaAsync(string connectionString, CancellationToken ct)
    {
        await using var db = new FactoryDbContext(new DbContextOptionsBuilder<FactoryDbContext>().UseNpgsql(connectionString).Options);
        return await CheckSchemaAsync(db, ct);
    }

    private static async Task<SchemaStatus> CheckSchemaAsync(FactoryDbContext db, CancellationToken ct)
    {
        var known = db.Database.GetMigrations().ToList();
        var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).ToList();
        return new SchemaStatus(
            [.. known.Except(applied, StringComparer.Ordinal)],
            [.. applied.Except(known, StringComparer.Ordinal)],
            applied.Count == 0 ? null : applied.Max(StringComparer.Ordinal));
    }
}

/// <param name="Pending">Migrations this build needs that the database lacks: `--migrate` applies them.</param>
/// <param name="Unknown">Migrations the database has that this build does not know: it was migrated by a newer build.</param>
/// <param name="LastApplied">The newest migration the database has, or null for an empty one.</param>
public sealed record SchemaStatus(IReadOnlyList<string> Pending, IReadOnlyList<string> Unknown, string? LastApplied)
{
    /// <summary>Why the host must not start on this schema, or null when it matches.</summary>
    public string? Problem => (Pending.Count, Unknown.Count) switch
    {
        (0, 0) => null,
        (_, 0) => $"The database schema is behind this build ({Pending.Count} pending migration(s): {string.Join(", ", Pending)}). Run the host once with --migrate.",
        _ => $"The database was migrated by a newer build: this build does not know {Unknown.Count} of its migration(s) ({string.Join(", ", Unknown)}). Run the build that migrated it, or a later one.",
    };
}

/// <summary>Lets `dotnet ef migrations add` build the model. It never connects to a database.</summary>
internal sealed class FactoryDbContextDesignFactory : IDesignTimeDbContextFactory<FactoryDbContext>
{
    public FactoryDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<FactoryDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=5433;Database=litos_factory;Username=design_time_only")
            .Options);
}
