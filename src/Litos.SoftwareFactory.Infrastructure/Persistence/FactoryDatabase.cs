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
    /// Migrations the database is missing. The host refuses to start dispatching when there are
    /// any: a schema and an application that disagree must fail clearly, not partly work.
    /// </summary>
    public static async Task<IReadOnlyList<string>> PendingMigrationsAsync(IDbContextFactory<FactoryDbContext> factory, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return [.. await db.Database.GetPendingMigrationsAsync(ct)];
    }
}

/// <summary>Lets `dotnet ef migrations add` build the model. It never connects to a database.</summary>
internal sealed class FactoryDbContextDesignFactory : IDesignTimeDbContextFactory<FactoryDbContext>
{
    public FactoryDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<FactoryDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=5433;Database=litos_factory;Username=design_time_only")
            .Options);
}
