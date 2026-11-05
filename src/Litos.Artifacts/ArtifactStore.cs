using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Litos.Artifacts;

/// <summary>
/// Versioned artifact storage under <c>~/.litos/artifacts/{id}/</c>: a <c>meta.json</c> plus one
/// <c>v{n}.html</c> per retained version. Unlike SharedFileStore's 24h share tokens, artifacts never
/// expire — republishing to the same URL is the point. The id doubles as the sole credential for the
/// unauthenticated GET route (ArtifactEndpoints), so it is random and every id from a caller is
/// validated before it touches the filesystem.
///
/// Several VS Code windows each run their own host process over this same directory, so meta.json
/// is replaced via temp file + move (a reader never sees a half-written file). Within one process,
/// publish and delete are serialized; across processes, two simultaneous publishes of the same
/// artifact are last-writer-wins, which is acceptable for a single local user.
/// </summary>
public sealed class ArtifactStore
{
    /// <summary>Versions older than the newest this-many are pruned on publish.</summary>
    public const int MaxRetainedVersions = 50;

    private const string MetaFileName = "meta.json";
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly string _rootDirectory;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public ArtifactStore(string? rootDirectory = null)
    {
        _rootDirectory = rootDirectory
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".litos", "artifacts");
    }

    /// <summary>16 lowercase hex characters — the only shape <see cref="NewId"/> produces.</summary>
    public static bool IsValidId(string? id) =>
        id is { Length: 16 } && id.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    /// <summary>
    /// Stores <paramref name="html"/> as a new version. The target is the artifact
    /// <paramref name="id"/> names when given, else the one last published from
    /// <paramref name="sourcePath"/>, else a new artifact.
    /// </summary>
    /// <returns>Null when <paramref name="id"/> is given but names no artifact — never a silent create.</returns>
    public async Task<ArtifactPublishResult?> PublishAsync(string html, string title, string? sourcePath, string? id, CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            ArtifactMeta? existing;
            if (id is not null)
            {
                existing = IsValidId(id) ? await ReadMetaAsync(id, ct) : null;
                if (existing is null)
                    return null;
            }
            else
            {
                existing = sourcePath is null
                    ? null
                    : (await ReadAllMetaAsync(ct)).FirstOrDefault(m => m.SourcePath is not null && PathsEqual(m.SourcePath, sourcePath));
            }

            var now = DateTimeOffset.UtcNow;
            var meta = existing is null
                ? new ArtifactMeta(NewId(), title, sourcePath, CurrentVersion: 1, OldestVersion: 1, now, now)
                : existing with
                {
                    Title = title,
                    SourcePath = sourcePath ?? existing.SourcePath,
                    CurrentVersion = existing.CurrentVersion + 1,
                    UpdatedAt = now,
                };

            Directory.CreateDirectory(DirectoryOf(meta.Id));
            await File.WriteAllTextAsync(VersionPath(meta.Id, meta.CurrentVersion), html, Utf8NoBom, ct);
            meta = PruneOldVersions(meta);
            await WriteMetaAsync(meta, ct);

            return new ArtifactPublishResult(meta, Created: existing is null);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>The artifact and the file holding <paramref name="version"/> (the current one when null).</summary>
    /// <returns>Null for a malformed or unknown id, or a version that was pruned or never existed.</returns>
    public async Task<(ArtifactMeta Meta, string FilePath)?> TryGetAsync(string id, int? version, CancellationToken ct)
    {
        if (!IsValidId(id) || await ReadMetaAsync(id, ct) is not { } meta)
            return null;

        var wanted = version ?? meta.CurrentVersion;
        if (wanted < meta.OldestVersion || wanted > meta.CurrentVersion)
            return null;

        var path = VersionPath(id, wanted);
        return File.Exists(path) ? (meta, path) : null;
    }

    public async Task<ArtifactMeta?> GetAsync(string id, CancellationToken ct) =>
        IsValidId(id) ? await ReadMetaAsync(id, ct) : null;

    /// <summary>Every artifact, most recently updated first.</summary>
    public async Task<IReadOnlyList<ArtifactMeta>> ListAsync(CancellationToken ct) =>
        [.. (await ReadAllMetaAsync(ct)).OrderByDescending(m => m.UpdatedAt)];

    /// <summary>Removes the artifact and every stored version.</summary>
    /// <returns>What was deleted, or null when there was nothing to delete.</returns>
    public async Task<ArtifactMeta?> DeleteAsync(string id, CancellationToken ct)
    {
        if (!IsValidId(id))
            return null;

        await _writeGate.WaitAsync(ct);
        try
        {
            if (await ReadMetaAsync(id, ct) is not { } meta)
                return null;

            Directory.Delete(DirectoryOf(id), recursive: true);
            return meta;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private string NewId()
    {
        string id;
        do
            id = RandomNumberGenerator.GetHexString(16, lowercase: true);
        while (Directory.Exists(DirectoryOf(id)));
        return id;
    }

    private ArtifactMeta PruneOldVersions(ArtifactMeta meta)
    {
        var oldestToKeep = Math.Max(meta.OldestVersion, meta.CurrentVersion - MaxRetainedVersions + 1);
        for (var version = meta.OldestVersion; version < oldestToKeep; version++)
            File.Delete(VersionPath(meta.Id, version));
        return meta with { OldestVersion = oldestToKeep };
    }

    private async Task<List<ArtifactMeta>> ReadAllMetaAsync(CancellationToken ct)
    {
        var all = new List<ArtifactMeta>();
        if (!Directory.Exists(_rootDirectory))
            return all;

        foreach (var directory in Directory.EnumerateDirectories(_rootDirectory))
            if (Path.GetFileName(directory) is var id && IsValidId(id) && await ReadMetaAsync(id, ct) is { } meta)
                all.Add(meta);
        return all;
    }

    private async Task<ArtifactMeta?> ReadMetaAsync(string id, CancellationToken ct)
    {
        var path = Path.Combine(DirectoryOf(id), MetaFileName);
        if (!File.Exists(path))
            return null;

        try
        {
            var meta = JsonSerializer.Deserialize<ArtifactMeta>(await File.ReadAllTextAsync(path, ct));
            // A meta.json whose id disagrees with its folder would let one artifact's URL serve another's files.
            return meta?.Id == id ? meta : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    private async Task WriteMetaAsync(ArtifactMeta meta, CancellationToken ct)
    {
        var path = Path.Combine(DirectoryOf(meta.Id), MetaFileName);
        var temp = $"{path}.{Guid.NewGuid():n}.tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(meta), Utf8NoBom, ct);

        // On Windows the replace fails with a sharing violation while anything has meta.json open
        // — a browser refresh being served, or another VS Code window's host listing artifacts —
        // since .NET opens files for reading without FILE_SHARE_DELETE. Those reads are brief, so
        // retry briefly rather than fail a publish whose version file is already written.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, path, overwrite: true);
                return;
            }
            // Windows reports the open target as either a sharing violation (IOException) or access
            // denied (UnauthorizedAccessException), depending on how the reader opened it.
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < MetaReplaceAttempts)
            {
                await Task.Delay(25 * attempt, ct);
            }
            catch
            {
                File.Delete(temp);
                throw;
            }
        }
    }

    private const int MetaReplaceAttempts = 8;

    private string DirectoryOf(string id) => Path.Combine(_rootDirectory, id);

    private string VersionPath(string id, int version) => Path.Combine(DirectoryOf(id), $"v{version}.html");

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), PathComparison);
}
