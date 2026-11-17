namespace Litos.Probe.Web;

/// <summary>Secrets from environment variables of the same name (<c>{secret:TEST_STAFF_PASS}</c> → <c>TEST_STAFF_PASS</c>): the CLI's resolver.</summary>
public sealed class EnvironmentSecretResolver(IReadOnlyDictionary<string, string>? environment = null) : ISecretResolver
{
    public string? Resolve(string name) =>
        environment is not null
            ? environment.TryGetValue(name, out var value) ? value : null
            : Environment.GetEnvironmentVariable(name);
}

/// <summary>
/// Writes artifacts into a folder, one subfolder per kind, and returns each file's path relative
/// to the folder as its id. The CLI uses <c>./.probe-web/&lt;timestamp&gt;/</c>.
/// </summary>
public sealed class FolderArtifactSink : IArtifactSink
{
    private readonly Lock _lock = new();
    private readonly HashSet<string> _used = new(StringComparer.OrdinalIgnoreCase);

    public FolderArtifactSink(string folder)
    {
        Folder = Path.GetFullPath(folder);
        Directory.CreateDirectory(Folder);
    }

    public string Folder { get; }

    public async Task<string> SaveAsync(ArtifactKind kind, string fileName, Stream content, CancellationToken ct)
    {
        var sub = kind.ToString().ToLowerInvariant() + "s";
        var id = UniqueId(sub, SafeName(fileName));
        var path = Path.Combine(Folder, id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var file = File.Create(path);
        await content.CopyToAsync(file, ct);
        return id;
    }

    private string UniqueId(string sub, string name)
    {
        lock (_lock)
        {
            var stem = Path.GetFileNameWithoutExtension(name);
            var extension = Path.GetExtension(name);
            var id = $"{sub}/{name}";
            for (var n = 2; !_used.Add(id); n++)
                id = $"{sub}/{stem}-{n}{extension}";
            return id;
        }
    }

    internal static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string([.. name.Select(c => invalid.Contains(c) || c is '/' or '\\' or ':' ? '-' : c)]).Trim('-', '.');
        return cleaned.Length == 0 ? "artifact" : cleaned;
    }
}
