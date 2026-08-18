using System.Security.Cryptography;
using System.Text.Json;
using Litos.Agent.Session;

namespace Litos.VsCodeHost.Files;

/// <summary>
/// Local copy of Litos.Api's Files/SharedFileStore.cs, unchanged (same disk layout, same
/// ValidateSegment reject-don't-sanitize path safety, same 24h token lifetime, same v1 debt: no
/// cleanup job for expired shares) — copied rather than shared for the same reason every other
/// cross-face type in this project is (see AgentWorker.cs's own remarks), and because this project
/// deliberately has no reference to Litos.Api. Root directory defaults to the same
/// ~/.litos/shared-files path Litos.Api uses, so a file shared via one face is resolvable by
/// another's /files/{token} endpoint too, if both processes happen to run on the same machine —
/// not a design goal here, just a natural consequence of both storing under the same shared
/// ~/.litos tree everything else in this project already shares (sessions, config, mcp.json).
/// </summary>
public sealed class SharedFileStore
{
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(24);

    private readonly string _rootDirectory;

    public SharedFileStore(string? rootDirectory = null)
    {
        _rootDirectory = rootDirectory
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".litos", "shared-files");
    }

    public async Task<SharedFileToken> ShareAsync(SessionOwner owner, string sourcePath, CancellationToken ct)
    {
        var ownerSegment = ValidateSegment(owner.Value, nameof(owner));
        var fileName = ValidateSegment(Path.GetFileName(sourcePath), nameof(sourcePath));
        var token = RandomNumberGenerator.GetHexString(16, lowercase: true);

        var directory = Path.Combine(_rootDirectory, ownerSegment, token);
        Directory.CreateDirectory(directory);

        var destinationPath = Path.Combine(directory, fileName);
        File.Copy(sourcePath, destinationPath, overwrite: true);

        var expiresAt = DateTimeOffset.UtcNow + TokenLifetime;
        var meta = new SharedFileMeta(ownerSegment, fileName, ContentTypeFor(fileName), expiresAt);

        var metaPath = Path.Combine(directory, "meta.json");
        await File.WriteAllTextAsync(metaPath, JsonSerializer.Serialize(meta), ct);

        return new SharedFileToken(token, expiresAt);
    }

    public async Task<(SharedFileMeta Meta, string FilePath)?> TryGetAsync(string token, CancellationToken ct)
    {
        if (!IsValidSegment(token))
            return null;

        if (!Directory.Exists(_rootDirectory))
            return null;

        foreach (var ownerDirectory in Directory.EnumerateDirectories(_rootDirectory))
        {
            var directory = Path.Combine(ownerDirectory, token);
            var metaPath = Path.Combine(directory, "meta.json");
            if (!File.Exists(metaPath))
                continue;

            SharedFileMeta? meta;
            try
            {
                meta = JsonSerializer.Deserialize<SharedFileMeta>(await File.ReadAllTextAsync(metaPath, ct));
            }
            catch (JsonException)
            {
                return null;
            }

            if (meta is null || meta.ExpiresAt <= DateTimeOffset.UtcNow)
                return null;

            var filePath = Path.Combine(directory, meta.FileName);
            if (!File.Exists(filePath))
                return null;

            return (meta, filePath);
        }

        return null;
    }

    private static string ContentTypeFor(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".txt" => "text/plain",
        ".md" => "text/markdown",
        ".csv" => "text/csv",
        ".json" => "application/json",
        ".pdf" => "application/pdf",
        ".zip" => "application/zip",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        _ => "application/octet-stream",
    };

    private static string ValidateSegment(string value, string paramName)
    {
        if (!IsValidSegment(value))
            throw new ArgumentException($"Value '{value}' is not a valid path segment.", paramName);
        return value;
    }

    private static bool IsValidSegment(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && !value.Contains("..")
        && value.All(c => c is not ('/' or '\\'))
        && value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
}
