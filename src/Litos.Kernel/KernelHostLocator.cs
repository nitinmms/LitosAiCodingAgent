using System.Diagnostics;

namespace Litos.Kernel;

public sealed record KernelHostPath(string FileName, IReadOnlyList<string> Arguments);

/// <summary>
/// Resolves how to launch Litos.Kernel.Host. A ProjectReference alone won't bundle a second
/// executable into Litos.Gui's self-contained single-file publish output (§8.6 Milestone 3) — the
/// published layout expects a sibling executable next to Litos.Gui's own, named per-platform
/// (Litos.Kernel.Host.exe on Windows, Litos.Kernel.Host with no extension on macOS/Linux, per the
/// Hard requirements' cross-platform publish note). For local dev (no such sibling exists next to
/// whatever's running the Gui process out of a build/debug directory), builds the project once
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
        var published = Path.Combine(AppContext.BaseDirectory, PlatformExeName());
        if (File.Exists(published))
            return new KernelHostPath(published, []);

        var devProjectPath = FindDevProjectPath();
        if (devProjectPath is not null)
        {
            var dll = BuildAndLocateDll(devProjectPath);
            return new KernelHostPath("dotnet", ["exec", dll]);
        }

        throw new FileNotFoundException(
            $"Could not locate {ExeName}: no published sibling executable at '{published}' and no " +
            $"'{ExeName}.csproj' found by walking up from '{AppContext.BaseDirectory}'. " +
            "Publish Litos.Kernel.Host alongside Litos.Gui, or run from within the repo.");
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

    /// <summary>
    /// Runs `dotnet build` synchronously (this only happens once per Gui process — subsequent
    /// KernelSession spawns reuse whatever's already on disk from here, since Resolve() is called
    /// again lazily per session but the build is a no-op / fast up-to-date check when nothing
    /// changed) with its own stdout/stderr redirected away from the caller entirely, then returns
    /// the built Litos.Kernel.Host.dll's path.
    /// </summary>
    private static string BuildAndLocateDll(string projectPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("build");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("Release");
        startInfo.ArgumentList.Add("--nologo");
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("quiet");

        using var process = Process.Start(startInfo)!;
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            var stderr = process.StandardError.ReadToEnd();
            throw new InvalidOperationException($"Failed to build {ExeName} for dev-mode launch: {stderr}");
        }

        var projectDir = Path.GetDirectoryName(projectPath)!;
        var dllPath = Path.Combine(projectDir, "bin", "Release", "net10.0", ExeName + ".dll");
        if (!File.Exists(dllPath))
            throw new FileNotFoundException($"Built {ExeName} but did not find the expected output at '{dllPath}'.", dllPath);
        return dllPath;
    }
}
