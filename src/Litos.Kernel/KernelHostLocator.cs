using System.Diagnostics;

namespace Litos.Kernel;

public sealed record KernelHostPath(string FileName, IReadOnlyList<string> Arguments);

/// <summary>
/// Resolves how to launch Litos.Kernel.Host. A ProjectReference alone won't bundle a second
/// executable into a self-contained single-file publish output (§8.6 Milestone 3) — the published
/// layout expects a sibling executable next to the host's own (Litos.Gui's, or Litos.VsCodeHost's
/// inside the extension's bin/&lt;rid&gt;/), named per-platform (Litos.Kernel.Host.exe on Windows,
/// Litos.Kernel.Host with no extension on macOS/Linux, per the Hard requirements' cross-platform
/// publish note). See CandidateSiblingDirectories for why "next to the host" is not the same thing
/// as AppContext.BaseDirectory once single-file publishing is involved.
/// For local dev (no such sibling exists next to
/// whatever's running the process out of a build/debug directory), builds the project once
/// (output discarded, not inherited by the eventual subprocess) and launches the resulting DLL via
/// `dotnet exec` — deliberately NOT `dotnet run`: `dotnet run` prints MSBuild restore/build banner
/// lines ("C:\...\dotnet.exe...", "Restore complete...") to its own stdout ahead of the program's
/// real output, and since KernelSession treats the subprocess's entire stdout as the wire protocol
/// stream, that banner text is indistinguishable from a malformed first message — the observed
/// failure was exactly this: "'C' is an invalid start of a value" from the "C:\Program Files\..."
/// banner line landing where a Handshake JSON line was expected. `dotnet exec` against an
/// already-built DLL has no such banner.
/// </summary>
public static class KernelHostLocator
{
    private const string ExeName = "Litos.Kernel.Host";

    public static KernelHostPath Resolve()
    {
        foreach (var directory in CandidateSiblingDirectories())
        {
            var published = Path.Combine(directory, PlatformExeName());
            if (File.Exists(published))
                return new KernelHostPath(published, []);
        }

        var devProjectPath = FindDevProjectPath();
        if (devProjectPath is not null)
        {
            var dll = BuildAndLocateDll(devProjectPath);
            return new KernelHostPath("dotnet", ["exec", dll]);
        }

        throw new FileNotFoundException(
            $"Could not locate {ExeName}: no published sibling executable in any of " +
            $"[{string.Join(", ", CandidateSiblingDirectories().Select(d => $"'{d}'"))}] and no " +
            $"'{ExeName}.csproj' found by walking up from '{AppContext.BaseDirectory}'. " +
            "Publish Litos.Kernel.Host alongside the host executable, or run from within the repo.");
    }

    /// <summary>
    /// Where a published sibling kernel executable might live, most-reliable first.
    ///
    /// AppContext.BaseDirectory alone is WRONG for a single-file publish, which is how both
    /// Litos.VsCodeHost and Litos.Gui actually ship: a compressed single-file bundle extracts its
    /// contents to a temp directory at startup and BaseDirectory points THERE, not at the folder
    /// holding the .exe the user launched. The kernel binary is deployed next to that .exe, so the
    /// probe never found it and kernel mode silently reported itself unavailable in every packaged
    /// build — confirmed by running a staged Litos.VsCodeHost.exe, which looked for the kernel at
    /// '%TEMP%\.net\Litos.VsCodeHost\&lt;hash&gt;\' instead of its own bin directory.
    ///
    /// Environment.ProcessPath is the launched executable's real path and is therefore the one that
    /// matters for a packaged build; BaseDirectory is kept as a fallback because it IS correct for a
    /// non-single-file layout (plain `dotnet build` output, and the test host), where ProcessPath
    /// points at dotnet.exe itself rather than at anything next to the kernel.
    /// </summary>
    /// <remarks>internal, not private, so KernelHostLocatorTests can assert the probe ORDER directly.
    /// The alternative — planting a sentinel file in Environment.ProcessPath's real directory — is not
    /// portable: under `dotnet test` on macOS/Linux that directory is the shared dotnet install root
    /// (e.g. /usr/local/share/dotnet), which is not writable.</remarks>
    internal static IEnumerable<string> CandidateSiblingDirectories()
    {
        var processDirectory = Path.GetDirectoryName(Environment.ProcessPath);
        if (!string.IsNullOrEmpty(processDirectory))
            yield return processDirectory;

        var baseDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.IsNullOrEmpty(baseDirectory) && !string.Equals(baseDirectory, processDirectory, StringComparison.OrdinalIgnoreCase))
            yield return baseDirectory;
    }

    private static string PlatformExeName() => OperatingSystem.IsWindows() ? ExeName + ".exe" : ExeName;

    /// <summary>Walks up from the running process's base directory looking for the repo's src/Litos.Kernel.Host/Litos.Kernel.Host.csproj — works from any bin/Debug|Release/netX.Y output directory.</summary>
    private static string? FindDevProjectPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Litos.Kernel.Host", "Litos.Kernel.Host.csproj");
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    /// <summary>The longest a dev-mode build may take before it is killed and reported as failed.</summary>
    internal static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(5);

    /// <summary>The most of each output stream an error message carries: the end of it, where MSBuild's errors are.</summary>
    internal const int BuildOutputTailChars = 4_000;

    /// <summary>
    /// Runs `dotnet build` synchronously (this only happens once per Gui process — subsequent
    /// KernelSession spawns reuse whatever's already on disk from here, since Resolve() is called
    /// again lazily per session but the build is a no-op / fast up-to-date check when nothing
    /// changed) with its own stdout/stderr redirected away from the caller entirely, then returns
    /// the built Litos.Kernel.Host.dll's path.
    /// </summary>
    private static string BuildAndLocateDll(string projectPath)
    {
        var startInfo = new ProcessStartInfo { FileName = "dotnet" };
        startInfo.ArgumentList.Add("build");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("Release");
        startInfo.ArgumentList.Add("--nologo");
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("quiet");
        // A reused MSBuild node outlives the build and inherits its output pipes, so reading them
        // to the end would wait for that node instead of for the build.
        startInfo.ArgumentList.Add("-nodeReuse:false");

        RunBuild(startInfo, BuildTimeout);

        var projectDir = Path.GetDirectoryName(projectPath)!;
        var dllPath = Path.Combine(projectDir, "bin", "Release", "net10.0", ExeName + ".dll");
        if (!File.Exists(dllPath))
            throw new FileNotFoundException($"Built {ExeName} but did not find the expected output at '{dllPath}'.", dllPath);
        return dllPath;
    }

    /// <summary>
    /// Runs a build to completion and throws, with the end of its output, if it fails or runs past
    /// <paramref name="timeout"/>. Both output streams are read while the build runs: waiting for
    /// exit first, as this once did, deadlocks as soon as a failing build's output fills the pipe —
    /// the build blocks writing and the caller blocks waiting (m3-architecture.md §12, where one
    /// kernel start waited 12 minutes on a build reporting locked files).
    /// </summary>
    /// <remarks>internal so KernelHostLocatorTests can run it against a stand-in command.</remarks>
    internal static void RunBuild(ProcessStartInfo startInfo, TimeSpan timeout)
    {
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to build {ExeName} for dev-mode launch: '{startInfo.FileName}' did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(timeout))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // It exited between the wait and the kill.
            }

            throw new InvalidOperationException(
                $"Failed to build {ExeName} for dev-mode launch: the build did not finish within {timeout.TotalSeconds:0} seconds and was stopped.{OutputTail(stdout, stderr)}");
        }

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Failed to build {ExeName} for dev-mode launch: the build exited with code {process.ExitCode}.{OutputTail(stdout, stderr)}");
    }

    /// <summary>The end of what the build wrote — MSBuild reports errors on stdout, not stderr — or
    /// nothing if a process that outlived the build still holds the pipes open.</summary>
    private static string OutputTail(Task<string> stdout, Task<string> stderr)
    {
        if (!Task.WaitAll([stdout, stderr], TimeSpan.FromSeconds(5)))
            return " Its output could not be read.";

        // Each stream keeps its own tail, so a noisy stderr cannot push MSBuild's stdout errors out.
        return string.Concat(new[] { stdout.Result, stderr.Result }
            .Select(output => output.Trim())
            .Where(output => output.Length > 0)
            .Select(output => Environment.NewLine + (output.Length > BuildOutputTailChars ? "…" + output[^BuildOutputTailChars..] : output)));
    }
}
