namespace Litos.Probe.Web.Processes;

/// <summary>
/// Finds what a bare command name refers to on Windows, as a shell would: without one, Windows
/// finds only <c>.exe</c> files by name, but npm is <c>npm.cmd</c>. The same search as the
/// factory's own resolver, kept here because Probe.Web never references the factory.
/// </summary>
internal static class ExecutableResolver
{
    public static string Resolve(string executable, string? path) =>
        OperatingSystem.IsWindows() ? ResolveOnWindows(executable, path, Environment.GetEnvironmentVariable("PATHEXT")) : executable;

    internal static string ResolveOnWindows(string executable, string? path, string? pathExtensions)
    {
        if (executable.IndexOfAny(['\\', '/']) >= 0 || Path.HasExtension(executable))
            return executable;

        var extensions = (pathExtensions ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var directory in (path ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var extension in extensions)
            {
                string candidate;
                try
                {
                    candidate = Path.Combine(directory.Trim('"'), executable + extension);
                }
                catch (ArgumentException)
                {
                    continue;
                }

                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return executable;
    }
}
