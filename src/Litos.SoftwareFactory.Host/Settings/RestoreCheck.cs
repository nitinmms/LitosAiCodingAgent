using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Infrastructure.Backup;

namespace Litos.SoftwareFactory.Host.Settings;

/// <summary>What a host started on restored data found (m4-architecture.md §8.3).</summary>
/// <param name="Problems">Files the backup held that are missing or changed in the data directory.</param>
/// <param name="UnreadableSecrets">Stored secrets the restored key ring cannot read.</param>
public sealed record RestoreReport(
    DateTimeOffset BackupTakenAt, string BackupHostVersion, int FilesChecked,
    IReadOnlyList<BackupProblem> Problems, IReadOnlyList<string> UnreadableSecrets, DateTimeOffset CheckedAt)
{
    public bool Clean => Problems.Count == 0 && UnreadableSecrets.Count == 0;
}

/// <summary>
/// On a data directory restored from a backup, which keeps the backup's manifest, the host checks at
/// start that the files the backup held are there unchanged and that the copied key ring reads every
/// stored secret. It reports, in the log and to Admins, and does not stop the host: start-up recovery
/// already holds every unfinished run for a person, and a missing log is no reason to refuse to serve.
/// The skill library is not checked: a missing revision is written again from the database.
/// </summary>
public sealed class RestoreCheck(FactoryOptions options, FactorySettings settings, IClock clock, ILogger<RestoreCheck> logger)
{
    /// <summary>Null when the data directory was not restored from a backup.</summary>
    public RestoreReport? Report { get; private set; }

    public async Task RunAsync(CancellationToken ct)
    {
        var path = Path.Combine(options.DataDirectory, FactoryBackup.RestoredManifestName);
        if (!File.Exists(path))
            return;

        BackupManifest manifest;
        try
        {
            manifest = FactoryBackup.ReadManifest(path);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            logger.LogWarning("{Path} could not be read, so the restored data was not checked: {Reason}", path, ex.Message);
            return;
        }

        var problems = await FactoryBackup.VerifyDataAsync(options.DataDirectory, manifest, ct);
        var unreadable = new List<string>();
        foreach (var secret in settings.Secrets)
        {
            try
            {
                settings.Secret(secret.Name);
            }
            catch (SecretUnreadableException)
            {
                unreadable.Add(secret.Name);
            }
        }

        Report = new RestoreReport(manifest.CreatedAt, manifest.HostVersion, manifest.Data.Count, problems, unreadable, clock.UtcNow);
        if (Report.Clean)
        {
            logger.LogInformation(
                "This data was restored from the backup taken {TakenAt:u}: its {Files} file(s) are all here unchanged and every stored secret can be read.",
                manifest.CreatedAt, manifest.Data.Count);
            return;
        }

        foreach (var problem in problems)
            logger.LogWarning("Restored from backup: {Path} {Problem}.", problem.Path, problem.Problem);
        foreach (var name in unreadable)
            logger.LogWarning("Restored from backup: the secret {Name} cannot be read with this key ring; set it again.", name);
    }
}
