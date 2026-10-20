using System.Text.Json;
using Microsoft.Win32;

namespace Litos.SoftwareFactory.Setup;

/// <summary>An installed Docker product litos-factory can start: Docker Desktop or Rancher Desktop.</summary>
/// <param name="AutoStart">Whether it starts by itself at sign-in; null when that cannot be read.</param>
public sealed record DockerEngine(string Name, string Application, bool? AutoStart);

/// <summary>The docker CLI and the engine behind it (§4.3 step 1, §4.5).</summary>
public sealed class Docker(IProcessRunner runner, string executable)
{
    /// <summary>docker.exe on the PATH, or where Docker Desktop and Rancher Desktop install it (G10).</summary>
    public static string? Find(string? path, string programFiles, string localAppData, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        if (ExecutableSearch.Find("docker", path, exists) is { } onPath)
            return onPath;

        return new[]
        {
            Path.Combine(programFiles, "Docker", "Docker", "resources", "bin", "docker.exe"),
            Path.Combine(localAppData, "Programs", "Rancher Desktop", "resources", "resources", "win32", "bin", "docker.exe"),
            Path.Combine(programFiles, "Rancher Desktop", "resources", "resources", "win32", "bin", "docker.exe"),
        }.FirstOrDefault(exists);
    }

    /// <summary>The engine answers: Docker is running and this user may use it.</summary>
    public async Task<bool> IsRunningAsync(CancellationToken ct) =>
        (await runner.RunAsync(executable, ["info", "--format", "{{.ServerVersion}}"], TimeSpan.FromSeconds(20), ct)).Succeeded;

    /// <summary>Compose's major version, or null when the compose plugin is missing.</summary>
    public async Task<int?> ComposeMajorVersionAsync(CancellationToken ct)
    {
        var result = await runner.RunAsync(executable, ["compose", "version", "--short"], TimeSpan.FromSeconds(20), ct);
        return result.Succeeded ? ParseMajorVersion(result.Output) : null;
    }

    internal static int? ParseMajorVersion(string version)
    {
        var text = version.Trim().TrimStart('v', 'V');
        var dot = text.IndexOf('.');
        return int.TryParse(dot < 0 ? text : text[..dot], out var major) ? major : null;
    }

    /// <summary>Waits until the engine answers, polling; it can take a minute or more after sign-in.</summary>
    public async Task<bool> WaitUntilRunningAsync(TimeSpan limit, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + limit;
        while (true)
        {
            if (await IsRunningAsync(ct))
                return true;
            if (DateTime.UtcNow >= deadline)
                return false;
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }
    }

    /// <summary>docker compose on the install's compose file, with factory.env for its variables.</summary>
    public Task<ProcessResult> ComposeAsync(FactoryPaths paths, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct) =>
        runner.RunAsync(executable, ["compose", "-f", paths.ComposeFile, "--env-file", paths.EnvFile, .. arguments], timeout, ct);

    /// <summary>The Docker product installed for this user, Docker Desktop first; null if neither is.</summary>
    public static DockerEngine? FindEngine(string programFiles, string localAppData, string appData, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        var desktopFolders = new List<string>();
        if (DockerDesktopInstallLocation() is { } registered)
            desktopFolders.Add(registered);
        desktopFolders.Add(Path.Combine(programFiles, "Docker", "Docker"));
        foreach (var folder in desktopFolders)
        {
            var app = Path.Combine(folder, "Docker Desktop.exe");
            if (exists(app))
                return new DockerEngine("Docker Desktop", app, DockerDesktopAutoStart(appData));
        }

        foreach (var folder in new[] { Path.Combine(localAppData, "Programs", "Rancher Desktop"), Path.Combine(programFiles, "Rancher Desktop") })
        {
            var app = Path.Combine(folder, "Rancher Desktop.exe");
            if (exists(app))
                return new DockerEngine("Rancher Desktop", app, AutoStart: null);
        }

        return null;
    }

    private static string? DockerDesktopInstallLocation()
    {
        const string key = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Docker Desktop";
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            using var uninstall = hive.OpenSubKey(key);
            if (uninstall?.GetValue("InstallLocation") is string { Length: > 0 } location)
                return location;
        }

        return null;
    }

    /// <summary>Docker Desktop's "Start Docker Desktop when you sign in", from its settings file.</summary>
    internal static bool? DockerDesktopAutoStart(string appData)
    {
        // settings-store.json since Docker Desktop 4.34; settings.json before.
        foreach (var name in new[] { "settings-store.json", "settings.json" })
        {
            var file = Path.Combine(appData, "Docker", name);
            if (!File.Exists(file))
                continue;
            try
            {
                return ReadAutoStart(File.ReadAllText(file));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        return null;
    }

    internal static bool? ReadAutoStart(string settingsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(settingsJson);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Name.Equals("AutoStart", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    return property.Value.GetBoolean();
            }

            // Absent: the default, which has differed between Docker Desktop versions.
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
