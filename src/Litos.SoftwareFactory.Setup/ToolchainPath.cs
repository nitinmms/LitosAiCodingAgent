using Microsoft.Win32;

namespace Litos.SoftwareFactory.Setup;

/// <summary>
/// The PATH the factory's builds and checks run with (G6): the machine and user PATH as the
/// registry has them now, not as this process inherited them. A toolchain installed after sign-in
/// (Maven, on 2026-10-13) is then found without signing out, and a Scheduled Task's PATH no longer
/// matters. Stored as FACTORY_TOOLCHAIN_PATH; `litos-factory refresh-path` captures it again.
/// </summary>
public static class ToolchainPath
{
    public static string Capture() => Combine(
        Read(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment"),
        Read(Registry.CurrentUser, "Environment"),
        Environment.ExpandEnvironmentVariables);

    /// <summary>Machine entries first, then the user's, as Windows builds a new session's PATH;
    /// each expanded, empty and repeated entries dropped.</summary>
    internal static string Combine(string? machine, string? user, Func<string, string> expand)
    {
        var entries = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in $"{machine};{user}".Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var entry = expand(part);
            if (entry.Length > 0 && seen.Add(entry.TrimEnd('\\')))
                entries.Add(entry);
        }

        return string.Join(';', entries);
    }

    // Unexpanded, so %USERPROFILE% and the like are expanded for this user, as Windows does.
    private static string? Read(RegistryKey hive, string key)
    {
        using var environment = hive.OpenSubKey(key);
        return environment?.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }
}
