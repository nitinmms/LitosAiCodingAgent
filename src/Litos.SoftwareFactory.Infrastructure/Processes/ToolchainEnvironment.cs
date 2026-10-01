namespace Litos.SoftwareFactory.Infrastructure.Processes;

/// <summary>
/// The environment given to any process that runs agent-written code: the worker itself, and
/// the verification commands that build and test what the agent wrote. It is built from an
/// allowlist rather than inherited, so it holds what a toolchain needs to work and nothing the
/// host keeps secret — no database connection string, no provider keys, no GitHub token,
/// whatever the host's own environment holds.
/// </summary>
public static class ToolchainEnvironment
{
    /// <summary>Passed through by exact name when the host has them.</summary>
    private static readonly string[] AllowedNames =
    [
        "PATH", "PATHEXT", "SystemRoot", "SystemDrive", "windir", "ComSpec", "TEMP", "TMP", "TMPDIR",
        "USERPROFILE", "HOME", "HOMEDRIVE", "HOMEPATH", "APPDATA", "LOCALAPPDATA", "ProgramData",
        "ProgramFiles", "ProgramFiles(x86)", "CommonProgramFiles", "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE",
        "OS", "LANG", "LC_ALL",
    ];

    /// <summary>Passed through by prefix: toolchain configuration such as DOTNET_ROOT,
    /// NUGET_PACKAGES and npm_config_cache.</summary>
    private static readonly string[] AllowedPrefixes = ["DOTNET_", "NUGET_", "npm_config_"];

    /// <summary>Never passed, even when a name or prefix above would allow it: these carry
    /// credentials (npm's registry token, a NuGet feed key).</summary>
    private static readonly string[] SecretMarkers = ["TOKEN", "SECRET", "PASSWORD", "PASSWD", "CREDENTIAL", "APIKEY", "API_KEY", "_AUTH", "CONNECTIONSTRING"];

    /// <summary>The allowlisted part of the host's environment.</summary>
    public static Dictionary<string, string> Scrub(IReadOnlyDictionary<string, string> hostEnvironment)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in hostEnvironment)
        {
            if (IsAllowed(name))
                environment[name] = value;
        }

        return environment;
    }

    public static bool IsAllowed(string name)
    {
        var allowed = AllowedNames.Contains(name, StringComparer.OrdinalIgnoreCase)
            || AllowedPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return allowed && !SecretMarkers.Any(marker => name.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    public static IReadOnlyDictionary<string, string> CurrentHostEnvironment()
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name && entry.Value is string value)
                environment[name] = value;
        }

        return environment;
    }
}
