using System.Reflection;
using Litos.SoftwareFactory.Infrastructure.Backup;
using Litos.SoftwareFactory.Infrastructure.Persistence;

namespace Litos.SoftwareFactory.Host;

/// <summary>
/// The host's backup commands (m4-architecture.md §8), run by deploy/factory-backup.ps1 and
/// deploy/factory-restore.ps1 so the manifest is written and checked by one implementation.
/// Each returns the process's exit code.
/// </summary>
public static class BackupCommands
{
    public const string BackupDataVerb = "--backup-data";
    public const string VerifyBackupVerb = "--verify-backup";

    /// <summary>The host's version, as a manifest records it.</summary>
    public static string HostVersion =>
        typeof(BackupCommands).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    /// <summary>
    /// Copies the data directory's kept folders beside the dump the script has written, and writes the
    /// manifest, with the newest migration the database has.
    /// </summary>
    public static async Task<int> BackupDataAsync(string folder, FactoryOptions options, TextWriter output, TextWriter error, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.DataDirectory) || string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            await error.WriteLineAsync("FACTORY_DATA_DIR and ConnectionStrings__FactoryState must be set: name the host's settings file with --env-file.");
            return 2;
        }

        try
        {
            var schema = await FactoryDatabase.CheckSchemaAsync(options.ConnectionString, ct);
            var manifest = await FactoryBackup.WriteAsync(folder, options.DataDirectory, HostVersion, schema.LastApplied, DateTimeOffset.UtcNow, ct);
            await output.WriteLineAsync(
                $"Backed up {manifest.Data.Count} file(s), {manifest.Data.Sum(f => f.Size) + manifest.Dump.Size:N0} bytes with the dump; the database is at {manifest.LastMigration ?? "no migration"}.");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync($"The backup could not be written: {ex.Message}");
            return 1;
        }
    }

    /// <summary>Checks a backup folder against its manifest, and lists what is missing or changed.</summary>
    public static async Task<int> VerifyBackupAsync(string folder, TextWriter output, TextWriter error, CancellationToken ct)
    {
        IReadOnlyList<BackupProblem> problems;
        BackupManifest manifest;
        try
        {
            manifest = FactoryBackup.ReadManifest(Path.Combine(folder, FactoryBackup.ManifestName));
            problems = await FactoryBackup.VerifyBackupAsync(folder, ct);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync($"The backup could not be checked: {ex.Message}");
            return 1;
        }

        if (problems.Count > 0)
        {
            await error.WriteLineAsync($"The backup in {folder} does not match its manifest:");
            foreach (var problem in problems)
                await error.WriteLineAsync($"  - {problem.Path} {problem.Problem}");
            return 1;
        }

        await output.WriteLineAsync(
            $"The backup taken {manifest.CreatedAt:u} by {manifest.HostVersion} matches its manifest: the dump and {manifest.Data.Count} file(s). The database was at {manifest.LastMigration ?? "no migration"}.");
        return 0;
    }
}
