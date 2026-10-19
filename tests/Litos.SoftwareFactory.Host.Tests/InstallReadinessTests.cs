using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Litos.SoftwareFactory.Host.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Litos.SoftwareFactory.Host.Tests;

/// <summary>The host changes installers need (SoftwareFactoryInstallation.md §9): content root, readiness, log file.</summary>
public sealed class InstallReadinessTests
{
    /// <summary>G2: a host started by a service manager from another folder still serves the web app beside it.</summary>
    [Fact]
    public async Task TheWebApp_IsServedFromTheContentRoot_NotTheCurrentFolder()
    {
        var root = Directory.CreateTempSubdirectory("litos-content-root-").FullName;
        Directory.CreateDirectory(Path.Combine(root, "wwwroot"));
        await File.WriteAllTextAsync(Path.Combine(root, "wwwroot", "index.html"), "<!doctype html><title>Litos Software Factory</title>");
        try
        {
            await using var host = await TestHost.StartAsync(o => o.ContentRoot = root);
            Assert.NotEqual(root, Directory.GetCurrentDirectory());

            var page = await host.NewClient().GetStringAsync("/");

            Assert.Contains("<title>Litos Software Factory</title>", page);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void WithoutAContentRoot_TheHostsOwnFolderIsUsed()
    {
        var app = FactoryHostApp.Build([], new FactoryOptions { DataDirectory = Path.GetTempPath() });

        Assert.Equal(
            Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory),
            Path.TrimEndingDirectorySeparator(app.Environment.ContentRootPath));
    }

    [Fact]
    public async Task Readyz_IsOk_OnceTheDatabaseAnswersAndTheCoordinatorRuns()
    {
        await using var host = await TestHost.StartAsync(signIn: false);

        using var response = await host.NewClient().GetAsync("/readyz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((true, "ok", "running"),
            (body.GetProperty("ready").GetBoolean(), body.GetProperty("database").GetString(), body.GetProperty("coordinator").GetString()));
    }

    [Fact]
    public async Task Readyz_Is503_AndSaysWhy_WhileTheCoordinatorHasNotStarted()
    {
        await using var host = await TestHost.StartAsync(signIn: false, startCoordinator: false);

        using var response = await host.NewClient().GetAsync("/readyz");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("starting", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("coordinator").GetString());
    }

    /// <summary>§9.8: a host started hidden has no console; with FACTORY_LOG_DIR its log is in a file.</summary>
    [Fact]
    public async Task WithALogFolder_TheHostWritesItsLogThere()
    {
        var logs = Directory.CreateTempSubdirectory("litos-logs-").FullName;
        try
        {
            await using (var host = await TestHost.StartAsync(o => o.LogDirectory = logs))
            {
                host.App.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Check").LogWarning("The log reaches its file.");
            }

            var file = Assert.Single(Directory.GetFiles(logs, "host-*.log"));
            // Read as a log viewer would, sharing the file with a writer.
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Assert.Contains("warn Check: The log reaches its file.", await new StreamReader(stream).ReadToEndAsync());
        }
        finally
        {
            Directory.Delete(logs, recursive: true);
        }
    }
}

public sealed class FileLoggerTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("litos-file-logger-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void EachDay_HasItsOwnFile_NamedByUtcDate_AndAnExceptionFollowsItsLine()
    {
        var now = new DateTimeOffset(2026, 10, 19, 23, 59, 0, TimeSpan.Zero);
        using (var provider = new FileLoggerProvider(_folder, () => now))
        {
            var logger = provider.CreateLogger("Litos.Runs");
            logger.LogInformation(new EventId(7), "Run started.");
            now = now.AddMinutes(2);
            logger.LogError(new InvalidOperationException("boom"), "Run failed.");
        }

        var first = File.ReadAllText(Path.Combine(_folder, "host-20261019.log"));
        var second = File.ReadAllText(Path.Combine(_folder, "host-20261020.log"));
        Assert.Equal("2026-10-19T23:59:00.000Z info Litos.Runs[7]: Run started." + Environment.NewLine, first);
        Assert.StartsWith("2026-10-20T00:01:00.000Z fail Litos.Runs: Run failed." + Environment.NewLine + "System.InvalidOperationException: boom", second);
    }

    [Fact]
    public void FilesOlderThanTwoWeeks_AreRemoved_WhenANewDayStarts()
    {
        File.WriteAllText(Path.Combine(_folder, "host-20261001.log"), "old");
        File.WriteAllText(Path.Combine(_folder, "host-20261010.log"), "recent");
        File.WriteAllText(Path.Combine(_folder, "notes.txt"), "not a log");

        using (var provider = new FileLoggerProvider(_folder, () => new DateTimeOffset(2026, 10, 19, 9, 0, 0, TimeSpan.Zero)))
            provider.CreateLogger("x").LogInformation("hello");

        Assert.Equal(["host-20261010.log", "host-20261019.log", "notes.txt"], Directory.GetFiles(_folder).Select(Path.GetFileName).Order());
    }
}
