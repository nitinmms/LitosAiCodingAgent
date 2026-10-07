using Litos.SoftwareFactory.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Litos.SoftwareFactory.Infrastructure.Tests.Persistence;

/// <summary>
/// Migrations that change existing data, run against a real PostgreSQL server on a database of
/// their own: the state before the migration is built, then the migration is applied.
/// </summary>
public sealed class MigrationTests : IAsyncLifetime
{
    private string? _database;

    private static string ConnectionTo(string database) =>
        new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable(PostgresFactoryStoreTests.Variable)) { Database = database }.ConnectionString;

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

    private FactoryDbContext Context() => new(new DbContextOptionsBuilder<FactoryDbContext>().UseNpgsql(ConnectionTo(_database!)).Options);

    private async Task MigrateToAsync(string migration)
    {
        await using var db = Context();
        await db.GetService<IMigrator>().MigrateAsync(migration);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionTo(_database!));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>A project registered before memberships existed keeps its registrar as a member.</summary>
    [SkippableFact]
    public async Task M2Identity_MakesEachExistingProjectsCreatorAMember()
    {
        Skip.If(_database is null, $"Set {PostgresFactoryStoreTests.Variable} to run the migration tests against PostgreSQL.");
        await MigrateToAsync("20261006124043_M2Concurrency");
        var creator = Guid.NewGuid();
        var project = Guid.NewGuid();
        await ExecuteAsync($"""
            INSERT INTO projects ("Id", "Name", "GitHubOwner", "GitHubRepository", "DefaultBranch", "Mode", "PullRequestEnabled",
                                  "VerificationProfileJson", "ProfileRevision", "CreatedBy", "CreatedAt")
            VALUES ('{project}', 'salesapp', 'acme', 'salesapp', 'main', 'Clone', true, '{"{"}"profileVersion":2,"steps":[]{"}"}', 1, '{creator}', '2026-10-01T09:00:00Z');
            """);

        await MigrateToAsync("20261007074713_M2Identity");

        await using var db = Context();
        var member = await db.ProjectMembers.SingleAsync();
        Assert.Equal((project, creator, creator), (member.ProjectId, member.UserId, member.CreatedBy));
    }
}
