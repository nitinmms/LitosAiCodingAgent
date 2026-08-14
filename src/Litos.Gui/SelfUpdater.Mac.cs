using System.Diagnostics;
using System.Runtime.Versioning;

namespace Litos.Gui;

/// <summary>
/// macOS install path: unlike Windows, a directory (the .app bundle) can be renamed out from under
/// a running process without breaking it — the process keeps its already-mapped pages regardless of
/// what the path on disk now points to — so the swap itself needs no helper process, no waiting for
/// this process to exit first, and no Job Object-style hazard (macOS has no equivalent primitive,
/// per Win32JobObject's own remarks). What macOS needs instead that Windows doesn't: a
/// `codesign --verify` check before trusting the downloaded bundle (there's no .sha256 asset for
/// macOS releases — release-macos.yml signs and notarizes instead, so Gatekeeper/codesign is the
/// verification story here, same trust model deploy/install.sh already relies on).
/// </summary>
public static partial class SelfUpdater
{
    [SupportedOSPlatform("macos")]
    public static async Task InstallAndRelaunchMacAsync(HttpClient http, GitHubAsset asset, SessionResumeArgs resume, CancellationToken ct)
    {
        var extractDir = await DownloadAndExtractAsync(http, asset, ct);
        var stagedBundle = Directory.GetDirectories(extractDir, "*.app").FirstOrDefault()
            ?? throw new InvalidOperationException($"No .app bundle found in downloaded asset '{asset.Name}'.");

        if (!await RunProcessAsync("codesign", $"--verify --deep --strict \"{stagedBundle}\"", ct))
            throw new InvalidOperationException("Downloaded update failed codesign verification — aborting install.");

        var runningBundlePath = ResolveRunningBundlePath()
            ?? throw new InvalidOperationException("Could not resolve the running app bundle's path.");

        var backupPath = runningBundlePath + ".old";
        if (Directory.Exists(backupPath))
            Directory.Delete(backupPath, recursive: true);

        // Same-volume renames (both under the install directory), so this is effectively atomic —
        // no window where runningBundlePath is missing entirely.
        Directory.Move(runningBundlePath, backupPath);
        try
        {
            Directory.Move(stagedBundle, runningBundlePath);
        }
        catch
        {
            Directory.Move(backupPath, runningBundlePath);
            throw;
        }

        var resumeArgs = string.Join(' ', resume.ToArgv().Select(a => $"\"{a}\""));
        Process.Start("open", $"-n \"{runningBundlePath}\" --args {resumeArgs}");

        // Best-effort cleanup of the pre-update bundle; failure here doesn't affect the already-
        // relaunched new instance, so it's fire-and-forget rather than something worth surfacing.
        try { Directory.Delete(backupPath, recursive: true); } catch { /* leave it for manual cleanup */ }

        Environment.Exit(0);
    }

    /// <summary>
    /// Walks up from the running executable (Litos.app/Contents/MacOS/Litos.Gui) to the .app bundle
    /// root, the directory Directory.Move needs to operate on as a whole rather than just the
    /// executable inside Contents/MacOS.
    /// </summary>
    private static string? ResolveRunningBundlePath()
    {
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath);
        var contentsDir = exeDir is null ? null : Directory.GetParent(exeDir)?.FullName;
        var bundleDir = contentsDir is null ? null : Directory.GetParent(contentsDir)?.FullName;
        return bundleDir is { } dir && dir.EndsWith(".app", StringComparison.OrdinalIgnoreCase) ? dir : null;
    }

    private static async Task<bool> RunProcessAsync(string fileName, string arguments, CancellationToken ct)
    {
        using var process = Process.Start(new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        });
        if (process is null)
            return false;

        await process.WaitForExitAsync(ct);
        return process.ExitCode == 0;
    }
}
