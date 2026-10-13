using Litos.SoftwareFactory.Core.Verification;
using Litos.SoftwareFactory.Infrastructure.Processes;

namespace Litos.SoftwareFactory.Infrastructure.Verification;

/// <summary>
/// Whether the host can start what a profile names (m3-architecture.md §8): checked when a
/// project is registered or its profile replaced, so a missing toolchain is found then rather
/// than at its first task's verification.
/// </summary>
public static class ProfileExecutables
{
    /// <summary>
    /// The bare command names the profile uses that are not on <paramref name="path"/>, each once.
    /// A path such as ./gradlew is relative to the working copy, which registration does not have
    /// yet, so only bare names are checked.
    /// </summary>
    public static IReadOnlyList<string> Missing(VerificationProfile profile, string? path) =>
    [
        .. profile.Steps
            .SelectMany(step => step.Commands())
            .Select(c => c.Command.Executable)
            .Where(e => !string.IsNullOrWhiteSpace(e) && e.IndexOfAny(['/', '\\']) < 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(e => Find(e, path) is null),
    ];

    /// <summary>The PATH the verifier gives its commands: the host's own, as the toolchain allowlist passes it.</summary>
    public static string? HostPath() =>
        ToolchainEnvironment.Scrub(ToolchainEnvironment.CurrentHostEnvironment()).TryGetValue("PATH", out var path) ? path : null;

    /// <summary>Where a bare command name is found, as the process runner would find it; null when it is not.</summary>
    public static string? Find(string executable, string? path)
    {
        if (OperatingSystem.IsWindows())
        {
            // A name with an extension (git.exe) is started as written; the resolver searches only for bare names.
            if (Path.HasExtension(executable))
                return OnPath(executable, path, ';');

            // The resolver hands back the name unchanged when it finds nothing.
            var resolved = ExecutableResolver.ResolveOnWindows(executable, path, Environment.GetEnvironmentVariable("PATHEXT"));
            return resolved == executable ? null : resolved;
        }

        return OnPath(executable, path, ':');
    }

    private static string? OnPath(string executable, string? path, char separator)
    {
        foreach (var directory in (path ?? "").Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim('"'), executable);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException)
            {
                // a malformed PATH entry
            }
        }

        return null;
    }
}
