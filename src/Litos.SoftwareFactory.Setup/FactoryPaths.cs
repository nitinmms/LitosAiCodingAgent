namespace Litos.SoftwareFactory.Setup;

/// <summary>
/// Where an installed factory keeps things (SoftwareFactoryInstallation.md §2): the install folder
/// (replaced on upgrade), the config folder (factory.env, owner-only, kept on upgrade) and the data
/// folder (kept on upgrade and, unless purged, on uninstall). The data folder is chosen once, by
/// setup, and recorded in factory.env as FACTORY_DATA_DIR.
/// </summary>
public sealed record FactoryPaths(string InstallDirectory, string ConfigDirectory, string LocalAppData)
{
    public const string ProductFolder = "LitosSoftwareFactory";

    /// <summary>Beside a source-tree dev setup, which already owns <see cref="ProductFolder"/> (§14.5).</summary>
    public const string BesideDevFolder = "LitosSoftwareFactory-Installed";

    /// <summary>Written into a data folder setup created, so a later setup knows the folder is its own.</summary>
    public const string InstallMarker = ".litos-factory-install";

    /// <summary>The marker <see cref="InstallMarker"/> in a data folder: only such a folder is deleted by a purge.</summary>
    public static bool IsOurDataDirectory(string directory) => File.Exists(Path.Combine(directory, InstallMarker));

    public static FactoryPaths ForCurrentUser() => new(
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ProductFolder),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    public string EnvFile => Path.Combine(ConfigDirectory, "factory.env");

    /// <summary>The first admin password, shown until it has been changed (§4.4).</summary>
    public string FirstSignInFile => Path.Combine(ConfigDirectory, "first-sign-in.txt");

    public string HostExecutable => Path.Combine(InstallDirectory, "Litos.SoftwareFactory.Host.exe");

    /// <summary>The windowless build of this program, which the Scheduled Task and the shortcut run.</summary>
    public string WindowlessExecutable => Path.Combine(InstallDirectory, "litos-factory-w.exe");

    public string ComposeFile => Path.Combine(InstallDirectory, "db", "compose.yml");

    /// <summary>
    /// The data folder for a new install: the usual one, unless something else (a source-tree dev
    /// setup, whose new-env.py uses the same folder) already has files there. Beside a dev setup the
    /// install also keeps off its ports, which are free whenever the dev host is not running (§14.5).
    /// </summary>
    public (string Directory, bool BesideDevSetup) ChooseDataDirectory()
    {
        var usual = Path.Combine(LocalAppData, ProductFolder);
        return IsFreeForUs(usual) ? (usual, false) : (Path.Combine(LocalAppData, BesideDevFolder), true);
    }

    private static bool IsFreeForUs(string directory) =>
        !Directory.Exists(directory)
        || IsOurDataDirectory(directory)
        || !Directory.EnumerateFileSystemEntries(directory).Any();

    public static string LogDirectory(string dataDirectory) => Path.Combine(dataDirectory, "logs");
}
