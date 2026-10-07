using Litos.SoftwareFactory.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Litos.SoftwareFactory.Infrastructure.Tests.Persistence;

/// <summary>
/// One host per database (docs/software-factory/m2-architecture.md §3.5), against a real
/// PostgreSQL server. Advisory locks are per database, so each test uses a database of its own and
/// cannot collide with a factory host running on the same server.
/// </summary>
public sealed class PostgresHostInstanceLockTests : IAsyncLifetime
{
    private string? _database;

    private static string ConnectionTo(string database) =>
        new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable(PostgresFactoryStoreTests.Variable)) { Database = database }.ConnectionString;

    private string Connection => ConnectionTo(_database!);

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PostgresFactoryStoreTests.Variable)))
            return;

        _database = $"litos_factory_test_{Guid.NewGuid():n}";
        await using var connection = new NpgsqlConnection(ConnectionTo("postgres"));
        await connection.OpenAsync();
        await using var create = new NpgsqlCommand($"CREATE DATABASE \"{_database}\"", connection);
        await create.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        if (_database is null)
            return;

        NpgsqlConnection.ClearAllPools();
        await using var connection = new NpgsqlConnection(ConnectionTo("postgres"));
        await connection.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)", connection);
        await drop.ExecuteNonQueryAsync();
    }

    private void SkipWithoutServer() => Skip.If(
        _database is null,
        $"Set {PostgresFactoryStoreTests.Variable} to an admin connection string for a PostgreSQL server to run the host-lock tests.");

    [SkippableFact]
    public async Task ASecondHost_IsRefused_UntilTheFirstLetsGo()
    {
        SkipWithoutServer();
        var first = new PostgresHostInstanceLock(Connection);
        await using var second = new PostgresHostInstanceLock(Connection);

        Assert.Null(await first.AcquireAsync(default));
        var refusal = await second.AcquireAsync(default);

        Assert.Contains("Another factory host is already running against this database", refusal);
        Assert.True(await first.IsHeldAsync(default));
        Assert.False(await second.IsHeldAsync(default));

        await first.DisposeAsync();
        await using var third = new PostgresHostInstanceLock(Connection);
        Assert.Null(await third.AcquireAsync(default));
    }

    /// <summary>When the lock's session is lost, so is the lock: the host must know, because
    /// another host can now start.</summary>
    [SkippableFact]
    public async Task ALostConnection_IsNoLongerHeld_AndAnotherHostCanTakeIt()
    {
        SkipWithoutServer();
        await using var held = new PostgresHostInstanceLock(Connection);
        Assert.Null(await held.AcquireAsync(default));

        await using (var admin = new NpgsqlConnection(ConnectionTo("postgres")))
        {
            await admin.OpenAsync();
            await using var terminate = new NpgsqlCommand(
                "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @database", admin);
            terminate.Parameters.AddWithValue("database", _database!);
            await terminate.ExecuteNonQueryAsync();
        }

        Assert.False(await held.IsHeldAsync(default));
        await using var next = new PostgresHostInstanceLock(Connection);
        Assert.Null(await next.AcquireAsync(default));
    }

    /// <summary>The host lock and the claim transaction's lock use different keys: a host holding
    /// its lock can still claim work.</summary>
    [SkippableFact]
    public async Task HoldingTheHostLock_DoesNotBlockTheClaim()
    {
        SkipWithoutServer();
        await FactoryDatabase.MigrateAsync(Connection, default);
        await using var hostLock = new PostgresHostInstanceLock(Connection);
        Assert.Null(await hostLock.AcquireAsync(default));
        var store = new EfFactoryStore(new TestContextFactory(new DbContextOptionsBuilder<FactoryDbContext>().UseNpgsql(Connection).Options));

        var claim = store.ClaimNextRunAsync(1, DateTimeOffset.UtcNow, default);

        Assert.Null(await claim.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [SkippableFact]
    public async Task IsHeld_BeforeAcquiring_IsFalse()
    {
        SkipWithoutServer();
        await using var hostLock = new PostgresHostInstanceLock(Connection);

        Assert.False(await hostLock.IsHeldAsync(default));
    }
}
