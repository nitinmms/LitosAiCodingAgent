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

/// <summary>
/// The Windows PTC kernel ships as its own release asset (see SelfUpdater.IsWindowsKernelAsset).
/// What matters most is that it can never be mistaken for the Litos.Gui asset — by this build's
/// matcher, by older builds' matcher (the same IsCurrentPlatformAsset rule), or by
/// deploy/install.ps1's "Litos.Gui-*-win-x64.zip" glob, which the same StartsWith/EndsWith
/// shape stands in for here.
/// </summary>
public class SelfUpdaterKernelAssetTests
{
    [Fact]
    public void IsWindowsKernelAsset_MatchesTheVersionedKernelAsset() =>
        Assert.True(SelfUpdater.IsWindowsKernelAsset(new("Litos.Kernel.Host-v1.2.3-win-x64.zip", "https://example.com/k.zip")));

    [Theory]
    [InlineData("Litos.Kernel.Host-v1.2.3-win-x64.zip.sha256")]
    [InlineData("Litos.Gui-v1.2.3-win-x64.zip")]
    [InlineData("Litos-osx-arm64.zip")]
    public void IsWindowsKernelAsset_RejectsEverythingElse(string name) =>
        Assert.False(SelfUpdater.IsWindowsKernelAsset(new(name, "https://example.com/x")));

    [Fact]
    public void KernelAsset_IsNeverMistakenForTheAppAsset()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Assert.False(SelfUpdater.IsCurrentPlatformAsset(new("Litos.Kernel.Host-v1.2.3-win-x64.zip", "https://example.com/k.zip")));
    }
}

public class SelfUpdaterKernelRelaunchScriptTests
{
    [Fact]
    public void BuildRelaunchScript_WithKernel_MovesItBeforeRelaunching()
    {
        var script = SelfUpdater.BuildRelaunchScript(
            oldPid: 1,
            newExePath: @"C:\Temp\a\Litos.Gui.exe",
            targetExePath: @"C:\Litos\Litos.Gui.exe",
            resumeArgv: [],
            newKernelExePath: @"C:\Temp\b\Litos.Kernel.Host.exe",
            targetKernelExePath: @"C:\Users\O'Brien\Litos\Litos.Kernel.Host.exe");

        var kernelMove = script.IndexOf(@"-Path 'C:\Temp\b\Litos.Kernel.Host.exe'", StringComparison.Ordinal);
        var relaunch = script.IndexOf("Start-Process", StringComparison.Ordinal);
        Assert.True(kernelMove >= 0, script);
        Assert.True(kernelMove < relaunch, "the kernel must be in place before the app relaunches");
        Assert.Contains(@"O''Brien", script);
    }

    [Fact]
    public void BuildRelaunchScript_WithoutKernel_TouchesOnlyTheApp()
    {
        var script = SelfUpdater.BuildRelaunchScript(1, @"C:\Temp\Litos.Gui.exe", @"C:\Litos\Litos.Gui.exe", []);

        Assert.DoesNotContain("Kernel", script);
    }
}

public sealed class SelfUpdaterPickExeTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("litos-pickexe-").FullName;

    [Fact]
    public void PickExe_PrefersTheNamedExe_EvenWhenAnotherSortsFirst()
    {
        File.WriteAllText(Path.Combine(_dir, "Aaa.exe"), "");
        File.WriteAllText(Path.Combine(_dir, "Litos.Gui.exe"), "");

        Assert.Equal(Path.Combine(_dir, "Litos.Gui.exe"), SelfUpdater.PickExe(_dir, "Litos.Gui.exe"));
    }

    [Fact]
    public void PickExe_FallsBackToTheOnlyExe()
    {
        File.WriteAllText(Path.Combine(_dir, "Renamed.exe"), "");

        Assert.Equal(Path.Combine(_dir, "Renamed.exe"), SelfUpdater.PickExe(_dir, "Litos.Gui.exe"));
    }

    [Fact]
    public void PickExe_NoExe_ReturnsNull() => Assert.Null(SelfUpdater.PickExe(_dir, "Litos.Gui.exe"));

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}

public sealed class SelfUpdaterKernelPickTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("litos-pickkernel-").FullName;

    [Fact]
    public void PickExe_WithoutFallback_NeverReturnsAnotherExe()
    {
        File.WriteAllText(Path.Combine(_dir, "Something.exe"), "");

        Assert.Null(SelfUpdater.PickExe(_dir, "Litos.Kernel.Host.exe", allowFallback: false));
    }

    [Fact]
    public void PickExe_WithoutFallback_StillFindsTheNamedExe()
    {
        File.WriteAllText(Path.Combine(_dir, "Litos.Kernel.Host.exe"), "");

        Assert.Equal(Path.Combine(_dir, "Litos.Kernel.Host.exe"), SelfUpdater.PickExe(_dir, "Litos.Kernel.Host.exe", allowFallback: false));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}

public class SelfUpdaterOpenBundleTests
{
    [Fact]
    public void OpenBundleArguments_NoArgs_OpensANewInstance() =>
        Assert.Equal("-n \"/Applications/Litos.app\"", SelfUpdater.OpenBundleArguments("/Applications/Litos.app", []));

    [Fact]
    public void OpenBundleArguments_WithArgs_PassesThemThroughQuoted() =>
        Assert.Equal("-n \"/Applications/Litos.app\" --args \"--resume-session\" \"abc\"",
            SelfUpdater.OpenBundleArguments("/Applications/Litos.app", ["--resume-session", "abc"]));
}
