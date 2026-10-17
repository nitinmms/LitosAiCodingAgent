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

    /// <summary>
    /// A revision written before M2Spec has no affected areas, test plan or open questions; it
    /// reads back with empty ones, which jsonb holds as [] (an empty string is not JSON).
    /// </summary>
    [SkippableFact]
    public async Task M2Spec_GivesEarlierRevisionsEmptyListsAndNoTestPlan()
    {
        Skip.If(_database is null, $"Set {PostgresFactoryStoreTests.Variable} to run the migration tests against PostgreSQL.");
        await MigrateToAsync("20261007120738_M2Chat");
        var project = Guid.NewGuid();
        var thread = Guid.NewGuid();
        await ExecuteAsync($$"""
            INSERT INTO projects ("Id", "Name", "GitHubOwner", "GitHubRepository", "DefaultBranch", "Mode", "PullRequestEnabled",
                                  "VerificationProfileJson", "ProfileRevision", "CreatedBy", "CreatedAt")
            VALUES ('{{project}}', 'salesapp', 'acme', 'salesapp', 'main', 'Clone', true, '{"profileVersion":2,"steps":[]}', 1, '{{Guid.NewGuid()}}', '2026-10-01T09:00:00Z');
            """);
        // The thread is written with today's model, whose columns added since M2Chat this schema
        // lacks: they are there for the insert only.
        await ExecuteAsync("""ALTER TABLE task_threads ADD COLUMN "ContextLength" integer, ADD COLUMN "PtcEnabled" boolean, ADD COLUMN "SkillsJson" jsonb NOT NULL DEFAULT '[]', ADD COLUMN "ManualLessonsJson" jsonb NOT NULL DEFAULT '[]';""");
        await using (var before = Context())
        {
            before.Threads.Add(new Core.Store.TaskThread
            {
                Id = thread, ProjectId = project, OwnerId = Guid.NewGuid(), Title = "Export", SessionId = "s", Provider = "openrouter", Model = "m",
            });
            await before.SaveChangesAsync();
        }

        await ExecuteAsync("""ALTER TABLE task_threads DROP COLUMN "ContextLength", DROP COLUMN "PtcEnabled", DROP COLUMN "SkillsJson", DROP COLUMN "ManualLessonsJson";""");

        await ExecuteAsync($$"""
            INSERT INTO specifications ("Id", "ThreadId", "Revision", "Summary", "AcceptanceCriteriaJson", "CreatedAt")
            VALUES ('{{Guid.NewGuid()}}', '{{thread}}', 1, 'Export orders.', '["One row per order."]', '2026-10-01T09:00:00Z');
            """);

        await MigrateToAsync("M2Spec");

        await using var db = Context();
        var spec = await db.Specifications.SingleAsync();
        Assert.Equal(("[]", "[]", "", (Guid?)null), (spec.AffectedAreasJson.Replace(" ", ""), spec.OpenQuestionsJson.Replace(" ", ""), spec.TestPlan, spec.RunId));
        // Only the column M2Spec added: today's model has run columns this schema does not.
        Assert.Null(await db.Runs.Select(r => r.SpecificationRevision).FirstOrDefaultAsync());
    }

    // ---- The schema check (m4-architecture.md §8.4) ----

    [SkippableFact]
    public async Task SchemaCheck_OnAnEmptyDatabase_ListsEveryMigrationAsPending()
    {
        Skip.If(_database is null, $"Set {PostgresFactoryStoreTests.Variable} to run the migration tests against PostgreSQL.");

        var status = await FactoryDatabase.CheckSchemaAsync(ConnectionTo(_database!), default);

        await using var db = Context();
        Assert.Equal(db.Database.GetMigrations(), status.Pending);
        Assert.Empty(status.Unknown);
        Assert.Null(status.LastApplied);
        Assert.StartsWith("The database schema is behind this build", status.Problem);
    }

    [SkippableFact]
    public async Task SchemaCheck_OnAMigratedDatabase_FindsNothing_AndNamesTheLastMigration()
    {
        Skip.If(_database is null, $"Set {PostgresFactoryStoreTests.Variable} to run the migration tests against PostgreSQL.");
        await using (var db = Context())
            await db.Database.MigrateAsync();

        var status = await FactoryDatabase.CheckSchemaAsync(ConnectionTo(_database!), default);

        await using var check = Context();
        Assert.Equal((0, 0), (status.Pending.Count, status.Unknown.Count));
        Assert.Equal(check.Database.GetMigrations().Last(), status.LastApplied);
        Assert.Null(status.Problem);
    }

    /// <summary>A database a newer build migrated is refused, not partly used.</summary>
    [SkippableFact]
    public async Task SchemaCheck_OnADatabaseMigratedByANewerBuild_NamesTheMigrationsItDoesNotKnow()
    {
        Skip.If(_database is null, $"Set {PostgresFactoryStoreTests.Variable} to run the migration tests against PostgreSQL.");
        await using (var db = Context())
            await db.Database.MigrateAsync();
        await ExecuteAsync("""INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion") VALUES ('29991231000000_FromTheFuture', '10.0.0');""");

        var status = await FactoryDatabase.CheckSchemaAsync(ConnectionTo(_database!), default);

        Assert.Empty(status.Pending);
        Assert.Equal(["29991231000000_FromTheFuture"], status.Unknown);
        Assert.Equal("29991231000000_FromTheFuture", status.LastApplied);
        Assert.Contains("migrated by a newer build", status.Problem);
        Assert.Contains("29991231000000_FromTheFuture", status.Problem);
    }
}
