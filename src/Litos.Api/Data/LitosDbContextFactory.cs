using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Litos.Api.Data;

/// <summary>
/// Lets `dotnet ef migrations add`/`database update` construct a LitosDbContext without running
/// Program.cs's full host (which requires POSTGRES_CONNECTION_STRING to be set and every other
/// service — MCP connections, Telegram, etc. — to initialize). Only used at design time; the
/// running app always goes through Program.cs's AddDbContext registration instead. The
/// connection string here only needs to be valid enough for Npgsql to build a model — no actual
/// connection is opened by `migrations add`.
/// </summary>
public sealed class LitosDbContextFactory : IDesignTimeDbContextFactory<LitosDbContext>
{
    public LitosDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING")
            ?? "Host=localhost;Database=litos;Username=litos;Password=litos";

        var options = new DbContextOptionsBuilder<LitosDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new LitosDbContext(options);
    }
}
