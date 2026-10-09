using Litos.Agent.Providers;
using Litos.Host;
using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Host.Api;
using Litos.SoftwareFactory.Host.Auth;
using Litos.SoftwareFactory.Host.Gateway;
using Litos.SoftwareFactory.Host.Runs;
using Litos.SoftwareFactory.Host.Settings;
using Litos.SoftwareFactory.Infrastructure.Git;
using Litos.SoftwareFactory.Infrastructure.GitHub;
using Litos.SoftwareFactory.Infrastructure.Persistence;
using Litos.SoftwareFactory.Infrastructure.Verification;
using Litos.SoftwareFactory.Infrastructure.Workers;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Litos.SoftwareFactory.Host;

/// <summary>Composes the factory host (ReadMe_LitosSoftwareFactory_V1.md §13).</summary>
public static class FactoryHostApp
{
    public const string DefaultUrl = "http://127.0.0.1:5180";

    /// <param name="configureServices">Replaces registrations, after the defaults; tests use it
    /// to swap the database, the provider and the ports for fakes.</param>
    public static WebApplication Build(string[] args, FactoryOptions options, Action<IServiceCollection>? configureServices = null)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Loopback by default (§13.3). Exposing the host on a network is a deliberate choice,
        // made by setting ASPNETCORE_URLS behind HTTPS.
        if (string.IsNullOrEmpty(builder.Configuration["urls"]) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
            builder.WebHost.UseUrls(DefaultUrl);

        // EF Core logs every SQL statement at Information; that is noise in a host's log.
        builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);

        var services = builder.Services;
        services.AddSingleton(options);
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<RunRegistry>();
        services.AddSingleton<FactorySignals>();

        if (!string.IsNullOrWhiteSpace(options.ConnectionString))
            services.AddFactoryStore(options.ConnectionString);
        services.TryAddSingleton<IHostInstanceLock, NoHostInstanceLock>();
        // Before auth: the sign-in cookies use the same key ring.
        services.AddFactorySecretProtection(options.DataDirectory);
        services.AddSingleton<FactorySettings>();
        services.AddFactoryAuth();
        // A session is checked against its account this often: disabling an account rotates its
        // security stamp, so its open sessions end at the next check.
        services.Configure<Microsoft.AspNetCore.Identity.SecurityStampValidatorOptions>(o => o.ValidationInterval = options.SessionCheckInterval);
        services.AddSingleton<ProjectAccess>();

        // The real providers, behind the gateway. The config is built from the host's own
        // settings: it is never loaded from, or saved to, the ~/.litos/config.json that the
        // other Litos faces share (§9.3).
        services.AddSingleton<IChatProviderFactory>(_ => HostProviders.Create(options));
        services.AddSingleton<ModelGateway>();

        services.AddSingleton<IWorkspaceProvider, GitWorkspaceProvider>();
        services.AddSingleton<IVerifier>(_ => new ProfileVerifier());
        services.AddSingleton<IWorkerLauncher>(_ => new LocalProcessWorkerLauncher(WorkerLocator.Resolve(options.WorkerPath)));
        services.AddSingleton<IWorkerClientFactory, HttpWorkerClientFactory>();
        services.AddSingleton<IUserDirectory, IdentityUserDirectory>();
        services.AddSingleton<RunExecutor>(sp => new RunExecutor(
            sp.GetRequiredService<IFactoryStore>(), options,
            sp.GetRequiredService<IWorkspaceProvider>(), sp.GetRequiredService<IVerifier>(), sp.GetRequiredService<IWorkerLauncher>(),
            sp.GetRequiredService<IWorkerClientFactory>(), sp.GetRequiredService<IUserDirectory>(), sp.GetRequiredService<FactorySignals>(),
            sp.GetRequiredService<IClock>(), sp.GetRequiredService<FactorySettings>(), sp.GetRequiredService<ILogger<RunExecutor>>(), sp.GetService<IGitHub>(),
            sp.GetRequiredService<VerificationGate>()));
        services.AddSingleton<VerificationGate>();
        services.AddSingleton<ReadingCopies>();
        services.AddSingleton<ChatExecutor>();
        services.AddSingleton<SpecExecutor>();
        if (!string.IsNullOrWhiteSpace(options.GitHubToken))
            services.AddSingleton<IGitHub>(_ => new GitHubClient(GitHubClient.CreateHttpClient(options.GitHubToken)));

        services.AddSingleton<PullRequestStatus>(sp => new PullRequestStatus(
            sp.GetRequiredService<IClock>(), sp.GetRequiredService<ILogger<PullRequestStatus>>(), sp.GetService<IGitHub>()));

        services.AddSingleton<RunSupervisor>();
        services.AddSingleton<IRunControl>(sp => sp.GetRequiredService<RunSupervisor>());
        services.AddSingleton<RunLiveness>();
        services.AddSingleton<RunCoordinator>();
        services.AddHostedService(sp => sp.GetRequiredService<RunCoordinator>());

        configureServices?.Invoke(services);

        var app = builder.Build();
        app.UseRateLimiter();
        app.UseFactoryCsrfHeader();
        app.UseAuthentication();
        app.UseAuthorization();

        // The React build, once there is one (step 6), is served from wwwroot.
        app.UseDefaultFiles();
        app.UseStaticFiles();

        app.MapGet("/healthz", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
        app.MapFactoryAuth();
        app.MapFactoryInvitations();
        app.MapFactoryUsers();
        app.MapFactoryApi();
        app.MapFactorySettings();
        app.MapFactoryEvents();
        app.MapWorkerCallbacks();

        app.Lifetime.ApplicationStarted.Register(() =>
        {
            // Workers call back on loopback, whatever name the browser reaches the host by.
            var address = new Uri(app.Urls.First());
            var hostUrl = $"http://127.0.0.1:{address.Port}";
            app.Services.GetRequiredService<RunExecutor>().HostUrl = hostUrl;
            app.Services.GetRequiredService<ChatExecutor>().HostUrl = hostUrl;
            app.Services.GetRequiredService<SpecExecutor>().HostUrl = hostUrl;
        });
        return app;
    }

    /// <summary>
    /// Checks the database is at this build's schema, takes the one-host-per-database lock, then
    /// creates the first account if there is none. Returns the reason the host must not start, or null.
    /// </summary>
    public static async Task<string?> PrepareAsync(WebApplication app, FactoryOptions options, CancellationToken ct)
    {
        var contexts = app.Services.GetRequiredService<IDbContextFactory<FactoryDbContext>>();
        await using (var db = await contexts.CreateDbContextAsync(ct))
        {
            if (db.Database.IsNpgsql())
            {
                var pending = await FactoryDatabase.PendingMigrationsAsync(contexts, ct);
                if (pending.Count > 0)
                    return $"The database schema is behind this build ({pending.Count} pending migration(s): {string.Join(", ", pending)}). Run the host once with --migrate.";
            }
        }

        // Before anything reads the runs: startup recovery would interrupt another host's tasks.
        if (await app.Services.GetRequiredService<IHostInstanceLock>().AcquireAsync(ct) is { } held)
            return held;

        Directory.CreateDirectory(options.DataDirectory);

        // Settings are read from the database from here on; the first start writes them there
        // from the environment (m3-architecture.md §3.3).
        var settings = app.Services.GetRequiredService<FactorySettings>();
        await settings.LoadAsync(ct);
        if (await SettingsSeeding.SeedAsync(settings, options, ct))
            app.Logger.LogInformation(
                "Factory settings were written to the database from this host's environment. From now on they are changed in the app, and these variables are no longer read: {Variables}.",
                string.Join(", ", SettingsSeeding.Replaced));

        return await FactoryAuth.SeedAdminAsync(app.Services, options);
    }
}

/// <summary>The providers the gateway may call. M1: OpenRouter only.</summary>
public static class HostProviders
{
    public static IChatProviderFactory Create(FactoryOptions options)
    {
        var config = new LitosConfig(
            DefaultProvider: FactoryOptions.OpenRouter, DefaultModel: options.Model, LastWorkingDirectory: null,
            ApiKeys: new Dictionary<string, string> { [FactoryOptions.OpenRouter] = options.OpenRouterApiKey ?? "" });

        var services = new ServiceCollection().AddLitosAgent(config).BuildServiceProvider();
        return new OnlyProvider(services.GetRequiredService<IChatProviderFactory>(), FactoryOptions.OpenRouter);
    }

    /// <summary>Refuses any provider but the one configured, however the request names it.</summary>
    private sealed class OnlyProvider(IChatProviderFactory inner, string allowed) : IChatProviderFactory
    {
        public IChatProvider Resolve(string providerName) =>
            providerName == allowed
                ? inner.Resolve(providerName)
                : throw new InvalidOperationException($"The factory is configured for '{allowed}' only, not '{providerName}'.");
    }
}

public sealed class GitWorkspaceProvider(FactoryOptions options) : IWorkspaceProvider
{
    public IWorkspace For(Project project) => Clone(project, options.WorkspacesDirectory);

    public IWorkspace ReadingCopyFor(Project project) => Clone(project, options.ReadingDirectory);

    private GitWorkspace Clone(Project project, string directory) => new(new GitWorkspaceOptions(
        Path.Combine(directory, project.Id.ToString("N")),
        new GitHubRepository(project.GitHubOwner, project.GitHubRepository).CloneUrl)
    {
        AccessToken = options.GitHubToken,
    });
}

public sealed class HttpWorkerClientFactory : IWorkerClientFactory
{
    public IWorkerClient Create(Uri baseAddress, string secret) => new HttpWorkerClient(new HttpClient(), baseAddress, secret);
}

public sealed class IdentityUserDirectory(IServiceScopeFactory scopes) : IUserDirectory
{
    public async Task<string?> CoAuthorAsync(Guid userId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var user = await scope.ServiceProvider.GetRequiredService<UserManager<FactoryUser>>().FindByIdAsync(userId.ToString());
        if (user is null)
            return null;

        // GitHub attributes a co-author by email; an account without one still gets its name
        // recorded, under an address that can never be someone else's.
        var name = string.IsNullOrWhiteSpace(user.DisplayName) ? user.UserName : user.DisplayName;
        var email = string.IsNullOrWhiteSpace(user.Email) ? $"{user.UserName}@users.litos-factory.invalid" : user.Email;
        return $"{name} <{email}>";
    }
}

/// <summary>Finds the worker executable: the configured path, or the one published beside the host.</summary>
public static class WorkerLocator
{
    private const string Name = "Litos.SoftwareFactory.Worker";

    public static WorkerCommand Resolve(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
            return ForPath(configuredPath);

        foreach (var directory in new[] { Path.GetDirectoryName(Environment.ProcessPath), AppContext.BaseDirectory })
        {
            if (directory is null)
                continue;
            foreach (var candidate in new[] { Path.Combine(directory, Name + ".exe"), Path.Combine(directory, Name), Path.Combine(directory, Name + ".dll") })
            {
                if (File.Exists(candidate))
                    return ForPath(candidate);
            }
        }

        // Running from source: the worker's own build output, found by walking up to the repository.
        if (FindInSourceTree(AppContext.BaseDirectory) is { } built)
            return ForPath(built);

        // Not found yet: named anyway, so the failure says what is missing when a run starts.
        return new WorkerCommand(Name);
    }

    /// <summary>From a host built in place (src/Litos.SoftwareFactory.Host/bin/&lt;configuration&gt;/&lt;framework&gt;),
    /// the worker built beside it with the same configuration and framework.</summary>
    internal static string? FindInSourceTree(string hostOutputDirectory)
    {
        var output = new DirectoryInfo(hostOutputDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var bin = output.Parent?.Parent;
        var source = bin?.Parent?.Parent;
        if (bin?.Name != "bin" || source is null)
            return null;

        var candidate = Path.Combine(source.FullName, Name, "bin", output.Parent!.Name, output.Name, Name + ".dll");
        return File.Exists(candidate) ? candidate : null;
    }

    private static WorkerCommand ForPath(string path) =>
        path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? new WorkerCommand("dotnet", [path]) : new WorkerCommand(path);
}
