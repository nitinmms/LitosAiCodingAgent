using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Litos.SoftwareFactory.Infrastructure.Backup;

/// <summary>One file a backup holds, by its path relative to the folder it was taken from.</summary>
public sealed record BackupFile(string Path, long Size, string Sha256);

/// <summary>
/// What a backup holds (m4-architecture.md §8.2): the database dump and the data directory's files,
/// each with its size and SHA-256, and what wrote them.
/// </summary>
/// <param name="LastMigration">The newest migration the dumped database had: a host restored onto it
/// must know it (§8.4).</param>
/// <param name="Data">Paths relative to the data directory, with '/' between folders.</param>
public sealed record BackupManifest(
    int Format, DateTimeOffset CreatedAt, string HostVersion, string? LastMigration, BackupFile Dump, IReadOnlyList<BackupFile> Data);

/// <summary>A file a backup lists that is missing or not what was backed up.</summary>
public sealed record BackupProblem(string Path, string Problem);

/// <summary>
/// Writes and checks a backup's files. The scripts in deploy/ pause dispatch and dump the database;
/// this copies the data directory beside the dump, lists everything in a manifest, and checks a
/// backup, or a data directory restored from one, against it.
/// </summary>
public static class FactoryBackup
{
    public const int Format = 1;
    public const string ManifestName = "manifest.json";
    public const string DumpName = "litos_factory.dump";

    /// <summary>Where a backup keeps the data directory's files.</summary>
    public const string DataFolderName = "data";

    /// <summary>
    /// A restored data directory keeps the backup's manifest under this name, so the host started on
    /// it checks its files and its key ring (§8.3).
    /// </summary>
    public const string RestoredManifestName = "restored-from.json";

    /// <summary>
    /// What is kept from the data directory: the key ring (without which no stored secret can be read
    /// again), the skill library, runs' logs, reports and transcripts, the projects' baselines, and
    /// the files attached to threads (attachments-architecture.md §3).
    /// Working copies, reading copies and virtual environments are left out: GitHub holds handed-off
    /// code, and they are made again (§24).
    /// </summary>
    public static readonly IReadOnlyList<string> KeptFolders = ["keys", "skills", "runs", "projects", "attachments"];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>
    /// Copies the kept folders of <paramref name="dataDirectory"/> into the backup folder, then writes
    /// the manifest over them and the dump already there.
    /// </summary>
    /// <exception cref="FileNotFoundException">The dump is not in the backup folder.</exception>
    /// <exception cref="IOException">The backup folder already holds a manifest or data.</exception>
    public static async Task<BackupManifest> WriteAsync(
        string backupFolder, string dataDirectory, string hostVersion, string? lastMigration, DateTimeOffset now, CancellationToken ct)
    {
        var dump = Path.Combine(backupFolder, DumpName);
        if (!File.Exists(dump))
            throw new FileNotFoundException($"The database dump is not in the backup folder: {dump}.", dump);
        var target = Path.Combine(backupFolder, DataFolderName);
        if (File.Exists(Path.Combine(backupFolder, ManifestName)) || Directory.Exists(target))
            throw new IOException($"{backupFolder} already holds a backup's manifest or data. Use an empty folder for each backup.");

        foreach (var folder in KeptFolders)
        {
            var source = Path.Combine(dataDirectory, folder);
            if (Directory.Exists(source))
                CopyDirectory(source, Path.Combine(target, folder), ct);
        }

        var files = new List<BackupFile>();
        foreach (var path in ListFiles(target))
            files.Add(await DescribeAsync(target, path, ct));

        var manifest = new BackupManifest(Format, now, hostVersion, lastMigration, await DescribeAsync(backupFolder, dump, ct), files);
        await File.WriteAllTextAsync(Path.Combine(backupFolder, ManifestName), JsonSerializer.Serialize(manifest, Json), ct);
        return manifest;
    }

    /// <exception cref="InvalidDataException">The manifest is not one this build can read.</exception>
    public static BackupManifest ReadManifest(string path)
    {
        BackupManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(path), Json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{path} is not a backup manifest: {ex.Message}", ex);
        }

        if (manifest is null || manifest.Dump is null || manifest.Data is null)
            throw new InvalidDataException($"{path} is not a backup manifest.");
        if (manifest.Format != Format)
            throw new InvalidDataException($"{path} is a format {manifest.Format} manifest; this build reads format {Format}.");
        return manifest;
    }

    /// <summary>Checks a backup folder: its dump and every data file, as the manifest lists them.</summary>
    public static async Task<IReadOnlyList<BackupProblem>> VerifyBackupAsync(string backupFolder, CancellationToken ct)
    {
        var manifest = ReadManifest(Path.Combine(backupFolder, ManifestName));
        var problems = new List<BackupProblem>();
        if (await CheckAsync(backupFolder, manifest.Dump, ct) is { } dump)
            problems.Add(dump);
        problems.AddRange(await VerifyDataAsync(Path.Combine(backupFolder, DataFolderName), manifest, ct));
        return problems;
    }

    /// <summary>
    /// Checks the data files the manifest lists under <paramref name="dataRoot"/>: a backup's data
    /// folder, or a data directory restored from it. A file added since is not a problem.
    /// </summary>
    public static async Task<IReadOnlyList<BackupProblem>> VerifyDataAsync(string dataRoot, BackupManifest manifest, CancellationToken ct)
    {
        var problems = new List<BackupProblem>();
        foreach (var file in manifest.Data)
        {
            if (await CheckAsync(dataRoot, file, ct) is { } problem)
                problems.Add(problem);
        }

        return problems;
    }

    private static async Task<BackupProblem?> CheckAsync(string root, BackupFile file, CancellationToken ct)
    {
        // A manifest names files under its root and nowhere else, however it was edited.
        var full = Path.GetFullPath(Path.Combine(root, file.Path));
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            return new BackupProblem(file.Path, "is outside the folder being checked");
        if (!File.Exists(full))
            return new BackupProblem(file.Path, "is missing");

        var actual = await DescribeAsync(root, full, ct);
        if (actual.Size != file.Size)
            return new BackupProblem(file.Path, $"is {actual.Size} bytes, not {file.Size}");
        if (!string.Equals(actual.Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase))
            return new BackupProblem(file.Path, "has changed (its SHA-256 differs)");
        return null;
    }

    private static async Task<BackupFile> DescribeAsync(string root, string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return new BackupFile(Path.GetRelativePath(root, path).Replace('\\', '/'), stream.Length, Convert.ToHexStringLower(hash));
    }

    /// <summary>Every file under a folder, in a stable order, so two manifests of the same data compare.</summary>
    private static IEnumerable<string> ListFiles(string root) => Directory.Exists(root)
        ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
        : [];

    private static void CopyDirectory(string source, string target, CancellationToken ct)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)));
        }
    }
}
