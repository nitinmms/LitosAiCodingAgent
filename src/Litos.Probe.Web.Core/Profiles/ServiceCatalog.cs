using System.Text.Json;

namespace Litos.Probe.Web.Core.Profiles;

/// <summary>
/// The images a contract's <c>services</c> may start (testability-architecture.md §6.7): throwaway
/// containers for a test run, so there is no shared test database. An image not listed here is
/// refused; a host may add entries (the factory lets Admins extend the list).
/// </summary>
public sealed class ServiceCatalog
{
    private readonly Dictionary<string, ServiceImage> _byRepository;

    public ServiceCatalog(IEnumerable<ServiceImage> images) =>
        _byRepository = images.ToDictionary(i => i.Repository, StringComparer.Ordinal);

    public IReadOnlyCollection<ServiceImage> All => _byRepository.Values;

    /// <summary>The entry for an image reference such as <c>postgres:16</c>; null when it is not allowed.</summary>
    public ServiceImage? Find(string image) => _byRepository.GetValueOrDefault(RepositoryOf(image));

    /// <summary><c>postgres:16</c>, <c>postgres@sha256:…</c> and <c>docker.io/library/postgres</c> all name <c>postgres</c>.</summary>
    public static string RepositoryOf(string image)
    {
        var name = image.Split('@')[0];
        var lastSlash = name.LastIndexOf('/');
        var colon = name.LastIndexOf(':');
        if (colon > lastSlash)
            name = name[..colon];
        foreach (var prefix in new[] { "docker.io/library/", "docker.io/", "index.docker.io/library/" })
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal))
                return name[prefix.Length..];
        }

        return name;
    }

    public static ServiceCatalog Default { get; } = Load();

    private static ServiceCatalog Load()
    {
        using var stream = typeof(ServiceCatalog).Assembly.GetManifestResourceStream("Litos.Probe.Web.Core.Profiles.services.json")
            ?? throw new InvalidOperationException("The service catalog is missing from the assembly.");
        return new ServiceCatalog(JsonSerializer.Deserialize<List<ServiceImage>>(stream, ProbeWebJson.Options) ?? []);
    }
}

/// <summary>One allowed image: the ports it listens on, how to tell it is ready, and what it needs.</summary>
public sealed record ServiceImage
{
    /// <summary>The image without a tag, e.g. <c>postgres</c> or <c>mcr.microsoft.com/mssql/server</c>.</summary>
    public string Repository { get; init; } = "";

    /// <summary>Its ports by name. The first is the one <c>{service:&lt;name&gt;:port}</c> means.</summary>
    public IReadOnlyDictionary<string, int> Ports { get; init; } = new Dictionary<string, int>();

    public ServiceReady Ready { get; init; } = new();

    /// <summary>Variables the image will not start without, such as <c>POSTGRES_PASSWORD</c>.</summary>
    public IReadOnlyList<string> RequiredEnvironment { get; init; } = [];

    /// <summary>Mailpit: the emails it caught are saved as evidence after the run.</summary>
    public bool CapturesEmail { get; init; }

    public int StartTimeoutSeconds { get; init; } = 180;

    /// <summary>1 when proved by a test app, 2 when shipped as data.</summary>
    public int Tier { get; init; } = 2;

    public string PrimaryPort => Ports.Keys.First();
}

/// <summary>
/// How a container is known to be ready. Never "its port accepts": Docker's proxy accepts on the
/// host port before the server inside has started.
/// </summary>
public sealed record ServiceReady
{
    /// <summary>A command run inside the container until it exits 0. <c>{env:NAME}</c> stands for one of its variables.</summary>
    public IReadOnlyList<string> Exec { get; init; } = [];

    /// <summary>A path answered below 400 on <see cref="HttpPort"/>.</summary>
    public string? HttpPath { get; init; }

    public string? HttpPort { get; init; }
}
