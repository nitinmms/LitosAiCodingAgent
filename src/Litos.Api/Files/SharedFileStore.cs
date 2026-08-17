using System.Security.Cryptography;
using System.Text.Json;
using Litos.Agent.Session;

namespace Litos.Api.Files;

/// <summary>
/// Disk-backed store for files the agent has shared with the user via ShareFileTool — copies the
/// source file into `{root}/{owner}/{token}/{fileName}` alongside a meta.json (SharedFileMeta),
/// mirroring JsonlTranscriptStore's own `~/.litos/{owner}/{sessionId}.jsonl` layout and
/// ValidateSegment-style "reject, don't sanitize" path safety.
///
/// Expiry (24h from ShareAsync) invalidates the token only — TryGetAsync returns null for an
/// expired token, but the copied file and its meta.json are never deleted here. No cleanup job
/// exists yet; that's deferred, known v1 debt (disk usage from shared files grows unboundedly
/// until one is added).
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

    /// <summary>Copies <paramref name="sourcePath"/> into the store and mints a fresh, unguessable token for it. Caller has already validated existence/size.</summary>
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

        // Written last, after the copy completes, so a reader can never observe a meta.json
        // pointing at a not-yet-fully-copied file — a request landing in the brief window before
        // this write lands just sees "token not found" (TryGetAsync treats a missing meta.json as
        // a lookup miss, not an error), which a retry resolves.
        var metaPath = Path.Combine(directory, "meta.json");
        await File.WriteAllTextAsync(metaPath, JsonSerializer.Serialize(meta), ct);

        return new SharedFileToken(token, expiresAt);
    }

    /// <summary>Resolves a token to its metadata and file path, or null if the token is malformed, unknown, or expired. Never throws for a bad token — a 404 is the correct response, not a 500.</summary>
    public async Task<(SharedFileMeta Meta, string FilePath)?> TryGetAsync(string token, CancellationToken ct)
    {
        if (!IsValidSegment(token))
            return null;

        // Token alone doesn't carry its owner subdirectory, so this does a one-level directory
        // scan (owner dirs — expected to be a handful of distinct SessionOwners, not one per
        // share) then a direct Combine+Exists check. O(distinct owners), not O(shares); fine at
        // this feature's expected scale, not a design that would scale to many tenants.
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

    /// <summary>
    /// Owner, token, and file name all become path segments, so anything that could escape the
    /// intended directory (path separators, "..", empty) is rejected outright rather than
    /// sanitized — a rejected request is safe; a silently-corrected one can still point somewhere
    /// unintended. Mirrors JsonlTranscriptStore.ValidateSegment.
    /// </summary>
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
