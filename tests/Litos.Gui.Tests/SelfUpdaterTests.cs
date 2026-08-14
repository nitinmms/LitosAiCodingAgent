namespace Litos.Gui.Tests;

/// <summary>
/// Covers SelfUpdater.IsNewerVersion, the pure version-comparison logic behind both the startup
/// update check and /update's on-demand check — extracted the same way ResolveStartupWorkingDirectory
/// is, so it's testable without hitting the real GitHub Releases API.
/// </summary>
public class SelfUpdaterTests
{
    [Fact]
    public void IsNewerVersion_True_WhenLatestTagIsGreater()
    {
        Assert.True(SelfUpdater.IsNewerVersion("1.2.3", "v1.3.0"));
    }

    [Fact]
    public void IsNewerVersion_False_WhenLatestTagIsEqual()
    {
        Assert.False(SelfUpdater.IsNewerVersion("1.2.3", "v1.2.3"));
    }

    [Fact]
    public void IsNewerVersion_False_WhenLatestTagIsOlder()
    {
        Assert.False(SelfUpdater.IsNewerVersion("2.0.0", "v1.9.9"));
    }

    [Fact]
    public void IsNewerVersion_False_WhenCurrentVersionIsUnparseable()
    {
        // The local dotnet-run fallback ("0.0.0" from AssemblyInformationalVersionAttribute being
        // absent) is well-formed, but this guards the general case of a malformed/dev value too —
        // an update check should never throw, just report "not newer".
        Assert.False(SelfUpdater.IsNewerVersion("not-a-version", "v1.0.0"));
    }

    [Fact]
    public void IsNewerVersion_False_WhenLatestTagIsUnparseable()
    {
        Assert.False(SelfUpdater.IsNewerVersion("1.0.0", "not-a-tag"));
    }

    [Fact]
    public void IsNewerVersion_HandlesTagWithoutLeadingV()
    {
        Assert.True(SelfUpdater.IsNewerVersion("1.0.0", "1.0.1"));
    }
}

/// <summary>
/// Covers SelfUpdater.IsCurrentPlatformAsset — regression coverage for a real bug where the
/// Windows branch compared against a fixed literal ("Litos.Gui-win-x64.zip") instead of matching
/// release-gui.yml's actual versioned filename ("Litos.Gui-$version-win-x64.zip"), so every
/// Windows update check failed to find its own release asset. Windows-only (no-op elsewhere) since
/// IsCurrentPlatformAsset branches on OperatingSystem.IsWindows() at runtime — this project builds
/// and tests on Windows today (see ReadMe.md's Status section), and there's no existing
/// Skip-on-platform test infrastructure in this codebase to add just for this.
/// </summary>
public class SelfUpdaterAssetMatchingTests
{
    [Fact]
    public void IsCurrentPlatformAsset_MatchesVersionedWindowsAssetName()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var asset = new SelfUpdater.GitHubAsset("Litos.Gui-v1.2.3-win-x64.zip", "https://example.com/asset.zip");

        Assert.True(SelfUpdater.IsCurrentPlatformAsset(asset));
    }

    [Fact]
    public void IsCurrentPlatformAsset_RejectsUnrelatedAssetWithSimilarName()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var asset = new SelfUpdater.GitHubAsset("Litos.Gui-win-x64.zip.sha256", "https://example.com/asset.zip.sha256");

        Assert.False(SelfUpdater.IsCurrentPlatformAsset(asset));
    }

    [Fact]
    public void IsCurrentPlatformAsset_RejectsMacAssetName_OnWindows()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var asset = new SelfUpdater.GitHubAsset("Litos-osx-arm64.zip", "https://example.com/asset.zip");

        Assert.False(SelfUpdater.IsCurrentPlatformAsset(asset));
    }
}

/// <summary>
/// Covers SelfUpdater.BuildRelaunchScript's quoting — regression coverage for a real bug where
/// newExePath/targetExePath were interpolated into the generated PowerShell script's single-quoted
/// string literals without escaping embedded single quotes (unlike the resume args, which already
/// were), so an install path containing an apostrophe (e.g. "C:\Users\O'Brien\...") would produce
/// an unparseable script and silently strand the app mid-update. Pure string-in/string-out, no
/// Windows APIs actually called, so — unlike InstallAndRelaunchWindowsAsync itself — this runs on
/// any OS the test suite happens to execute on.
/// </summary>
public class SelfUpdaterRelaunchScriptTests
{
    [Fact]
    public void BuildRelaunchScript_EscapesSingleQuoteInTargetExePath()
    {
        var script = SelfUpdater.BuildRelaunchScript(
            oldPid: 1234,
            newExePath: @"C:\Temp\Litos.Gui.exe",
            targetExePath: @"C:\Users\O'Brien\Programs\Litos\Litos.Gui.exe",
            resumeArgv: []);

        Assert.Contains(@"O''Brien", script);
        Assert.DoesNotContain(@"O'Brien\Programs", script);
    }

    [Fact]
    public void BuildRelaunchScript_EscapesSingleQuoteInResumeArgs()
    {
        var script = SelfUpdater.BuildRelaunchScript(
            oldPid: 1234,
            newExePath: @"C:\Temp\Litos.Gui.exe",
            targetExePath: @"C:\Litos\Litos.Gui.exe",
            resumeArgv: ["--resume-dir", @"C:\Users\O'Brien\repo"]);

        Assert.Contains(@"O''Brien", script);
    }

    [Fact]
    public void BuildRelaunchScript_IncludesProcessIdAndTargetPath_WhenNoQuotesPresent()
    {
        var script = SelfUpdater.BuildRelaunchScript(
            oldPid: 4321,
            newExePath: @"C:\Temp\Litos.Gui.exe",
            targetExePath: @"C:\Litos\Litos.Gui.exe",
            resumeArgv: []);

        Assert.Contains("-Id 4321", script);
        Assert.Contains(@"C:\Litos\Litos.Gui.exe", script);
    }
}
