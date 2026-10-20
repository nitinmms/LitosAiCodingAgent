using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace Litos.SoftwareFactory.Setup;

/// <summary>Whether litos-factory starts Docker when the factory needs it and it is not running.</summary>
public enum StartDockerPolicy
{
    /// <summary>The Start-menu shortcut and setup ask; the background start at sign-in only waits.</summary>
    Ask,
    /// <summary>Started whenever the factory needs it, at sign-in too.</summary>
    Always,
    /// <summary>Never started; the factory waits for it.</summary>
    Never,
}

/// <summary>
/// factory.env: the settings file the host reads with --env-file and docker compose with
/// --env-file, as KEY=value lines. Lines are kept in order, and a changed value is rewritten in
/// place, so comments and anything a person added survive. How a line is read (# comments, the
/// first '=', surrounding quotes) must match the host's EnvFile.Read in Host/FactoryOptions.cs,
/// which this program does not reference; FactoryEnvTests pins it.
/// </summary>
public sealed class FactoryEnv
{
    public const string DataDir = "FACTORY_DATA_DIR";
    public const string LogDir = "FACTORY_LOG_DIR";
    public const string Urls = "ASPNETCORE_URLS";
    public const string DbPort = "FACTORY_DB_PORT";
    public const string AdminUser = "FACTORY_ADMIN_USER";
    public const string AdminPassword = "FACTORY_ADMIN_PASSWORD";
    public const string ControlToken = "FACTORY_CONTROL_TOKEN";
    public const string ToolchainPath = "FACTORY_TOOLCHAIN_PATH";
    public const string StartDocker = "FACTORY_START_DOCKER";

    private readonly List<string> _lines;

    private FactoryEnv(IEnumerable<string> lines) => _lines = [.. lines];

    public static FactoryEnv Parse(string text) => new(text.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n'));

    public static FactoryEnv Load(string path) => Parse(File.ReadAllText(path));

    public string? this[string key]
    {
        get
        {
            var index = IndexOf(key);
            if (index < 0)
                return null;
            var line = _lines[index];
            var value = line[(line.IndexOf('=') + 1)..].Trim();
            return value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;
        }
    }

    public void Set(string key, string value)
    {
        var index = IndexOf(key);
        if (index >= 0)
            _lines[index] = $"{key}={value}";
        else
            _lines.Add($"{key}={value}");
    }

    public string DataDirectory => this[DataDir] is { Length: > 0 } dir ? dir : throw new InvalidOperationException($"{DataDir} is not set in factory.env.");

    /// <summary>The address the browser and this program reach the host on.</summary>
    public string Url => (this[Urls] is { Length: > 0 } urls ? urls.Split(';')[0] : "http://127.0.0.1:5180").TrimEnd('/');

    public StartDockerPolicy StartDockerPolicy => this[StartDocker]?.Trim().ToLowerInvariant() switch
    {
        "always" => StartDockerPolicy.Always,
        "never" => StartDockerPolicy.Never,
        _ => StartDockerPolicy.Ask,
    };

    public override string ToString() => string.Join('\n', _lines) + "\n";

    /// <summary>Writes the file readable by its owner only (§4.3 step 2): it holds every password.</summary>
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, ToString());
        OwnerOnly.Apply(temporary);
        File.Move(temporary, path, overwrite: true);
    }

    private int IndexOf(string key) => _lines.FindIndex(line =>
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith('#'))
            return false;
        var at = trimmed.IndexOf('=');
        return at > 0 && trimmed[..at].Trim().Equals(key, StringComparison.OrdinalIgnoreCase);
    });

    /// <summary>
    /// The C# port of deploy/factory/new-env.py (G5, §4.3 step 2). A new install's settings: generated passwords, the database's roles' connection
    /// strings, the ports chosen for this machine, the data and log folders, the one-time admin
    /// password, and the control token litos-factory uses to stop the host.
    /// </summary>
    public static FactoryEnv CreateNew(string dataDirectory, int hostPort, int dbPort)
    {
        var (admin, migrator, runtime) = (Secret(), Secret(), Secret());
        var connection = $"Host=127.0.0.1;Port={dbPort};Database=litos_factory";
        var data = dataDirectory.Replace('\\', '/');
        return new FactoryEnv(
        [
            "# Litos Software Factory settings, written by litos-factory setup.",
            "# The database keeps the passwords it was created with: changing them here locks the host out.",
            $"FACTORY_DB_ADMIN_PASSWORD={admin}",
            $"FACTORY_DB_MIGRATOR_PASSWORD={migrator}",
            $"FACTORY_DB_RUNTIME_PASSWORD={runtime}",
            $"{DbPort}={dbPort}",
            $"ConnectionStrings__FactoryState={connection};Username=factory_runtime;Password={runtime}",
            $"ConnectionStrings__FactoryMigrations={connection};Username=factory_migrator;Password={migrator}",
            $"{Urls}=http://127.0.0.1:{hostPort}",
            $"{AdminUser}=admin",
            $"{AdminPassword}={Secret()}",
            "FACTORY_ADMIN_PASSWORD_ONE_TIME=1",
            $"{DataDir}={data}",
            $"{LogDir}={data}/logs",
            $"{ControlToken}={Secret()}",
            $"{StartDocker}=ask",
        ]);
    }

    /// <summary>24 random bytes, URL-safe: what new-env.py's token_urlsafe(24) made, 32 characters.</summary>
    internal static string Secret() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
}

/// <summary>Restricts a file or folder to the current user, with nothing inherited (§4.3 step 2).</summary>
public static class OwnerOnly
{
    public static void Apply(string path)
    {
        var user = WindowsIdentity.GetCurrent().User!;
        if (Directory.Exists(path))
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(path).SetAccessControl(security);
        }
        else
        {
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);
        }
    }
}
