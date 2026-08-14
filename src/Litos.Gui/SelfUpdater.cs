using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace Litos.Gui;

/// <summary>
/// Checks GitHub Releases for a newer Litos.Gui build and installs it in place — the in-app
/// counterpart to deploy/install.ps1 / deploy/install.sh, reusing the same "GET .../releases/latest,
/// match an asset by name" approach those scripts already use for a fresh install. Split into this
/// file (version check/compare, shared by both platforms) plus SelfUpdater.Windows.cs and
/// SelfUpdater.Mac.cs (the two install mechanisms, different enough — file-replace-via-helper-
/// script vs. bundle-swap-with-codesign — that keeping them in separate partial-class files reads
/// better than one large branching method).
/// </summary>
// Public, not internal, because MainWindowSession.UpdateCheckTask (a public property, since
// MainWindowSession itself is public) exposes UpdateCheckResult — a nested type's effective
// accessibility is capped by its container's, so SelfUpdater has to be public for that member to
// compile (CS0053). Doesn't widen anything real: Litos.Gui isn't referenced by any other
// assembly's public surface, so this is only visible within the solution regardless.
public static partial class SelfUpdater
{
    private const string Repo = "nitinmms/LitosAiCodingAgent";
    private static readonly Uri LatestReleaseUri = new($"https://api.github.com/repos/{Repo}/releases/latest");

    public sealed record UpdateCheckResult(bool IsUpdateAvailable, string? LatestVersion, GitHubAsset? Asset, string? Error)
    {
        public static UpdateCheckResult UpToDate() => new(false, null, null, null);
        public static UpdateCheckResult Failed(string error) => new(false, null, null, error);
    }

    public sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string TagName,
        [property: JsonPropertyName("assets")] IReadOnlyList<GitHubAsset> Assets);

    public sealed record GitHubAsset(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl);

    /// <summary>
    /// The version this running build reports as, read from the same InformationalVersion the
    /// release workflows now stamp via -p:Version/-p:InformationalVersion at publish time (see
    /// release-gui.yml, publish-macos.sh). Falls back to "0.0.0" for a local `dotnet run`/debug
    /// build with no stamped version — CompareVersions then always reports "update available" for
    /// such a build, which is harmless (the button just offers a check that finds a real release).
    /// </summary>
    internal static string CurrentVersion { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            is { Length: > 0 } informational
            ? informational
            : "0.0.0";

    /// <summary>
    /// Fire-and-forget-friendly: never throws, reports network/parse failures via
    /// UpdateCheckResult.Error instead so a startup check can't crash the app and a manual /update
    /// can show the error inline as a tool line.
    /// </summary>
    public static async Task<UpdateCheckResult> CheckForUpdateAsync(HttpClient http, CancellationToken ct)
    {
        GitHubRelease? release;
        try
        {
            release = await http.GetFromJsonAsync<GitHubRelease>(LatestReleaseUri, ct);
        }
        catch (Exception ex)
        {
            return UpdateCheckResult.Failed($"Could not check for updates: {ex.Message}");
        }

        if (release is null)
            return UpdateCheckResult.Failed("Could not check for updates: empty response from GitHub.");

        var asset = release.Assets.FirstOrDefault(IsCurrentPlatformAsset);
        if (asset is null)
            return UpdateCheckResult.Failed($"Latest release {release.TagName} has no matching asset for this platform.");

        if (!IsNewerVersion(CurrentVersion, release.TagName))
            return UpdateCheckResult.UpToDate();

        return new UpdateCheckResult(IsUpdateAvailable: true, release.TagName, asset, Error: null);
    }

    /// <summary>
    /// True for the one release asset that matches this process's platform/architecture. Windows'
    /// zip name embeds the release tag (release-gui.yml: "Litos.Gui-$version-win-x64.zip"), so this
    /// has to prefix/suffix-match rather than compare a fixed literal — same reason
    /// deploy/install.ps1:21 globs for "Litos.Gui-*-win-x64.zip" instead of an exact name. macOS'
    /// name has no version segment (publish-macos.sh's "$APP_NAME-$RUNTIME.zip"), so an exact match
    /// is enough there, matching deploy/install.sh's own $ASSET_NAME.
    /// </summary>
    internal static bool IsCurrentPlatformAsset(GitHubAsset asset) =>
        OperatingSystem.IsWindows()
            ? asset.Name.StartsWith("Litos.Gui-", StringComparison.Ordinal) && asset.Name.EndsWith("-win-x64.zip", StringComparison.Ordinal)
            : asset.Name == $"Litos-{(RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64")}.zip";

    /// <summary>
    /// Pure string comparison, no network/filesystem — same shape as Program.ResolveStartupWorkingDirectory
    /// and MainWindow.ResolveResumeWorkingDirectory, so it's unit-testable without a real release feed.
    /// Tags are "v1.2.3"; a leading "v" is stripped from both sides before comparing via System.Version.
    /// Any unparseable version (e.g. this build's own "0.0.0" fallback, or a malformed tag) is treated
    /// as older, never throws — an update check should never crash on a weird tag name.
    /// </summary>
    internal static bool IsNewerVersion(string current, string latestTag)
    {
        var latest = latestTag.TrimStart('v', 'V');
        if (!Version.TryParse(current, out var currentVersion) || !Version.TryParse(latest, out var latestVersion))
            return false;

        return latestVersion > currentVersion;
    }

    /// <summary>
    /// Installs <paramref name="asset"/> in place and relaunches the app resuming
    /// <paramref name="resume"/>'s session — dispatches to the Windows or macOS mechanism (see
    /// SelfUpdater.Windows.cs / SelfUpdater.Mac.cs for why they differ). Never returns on success:
    /// both platform paths end by exiting the current process once the new one has been started.
    /// </summary>
    public static Task InstallAndRelaunchAsync(HttpClient http, GitHubAsset asset, SessionResumeArgs resume, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
            return InstallAndRelaunchWindowsAsync(http, asset, resume, ct);
        if (OperatingSystem.IsMacOS())
            return InstallAndRelaunchMacAsync(http, asset, resume, ct);

        throw new PlatformNotSupportedException("Self-update is only supported on Windows and macOS.");
    }

    /// <summary>
    /// Downloads and extracts <paramref name="asset"/> to a fresh temp directory, returning the
    /// path to the extracted payload (an exe on Windows, an .app bundle directory on macOS) —
    /// shared staging step both platform installers build on.
    /// </summary>
    private static async Task<string> DownloadAndExtractAsync(HttpClient http, GitHubAsset asset, CancellationToken ct)
    {
        var stagingDir = Path.Combine(Path.GetTempPath(), $"litos-update-{Guid.NewGuid():n}");
        Directory.CreateDirectory(stagingDir);

        var zipPath = Path.Combine(stagingDir, asset.Name);
        await using (var fileStream = File.Create(zipPath))
        await using (var httpStream = await http.GetStreamAsync(asset.BrowserDownloadUrl, ct))
            await httpStream.CopyToAsync(fileStream, ct);

        var extractDir = Path.Combine(stagingDir, "extracted");
        System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, extractDir);
        return extractDir;
    }
}
