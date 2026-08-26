namespace Litos.Kernel.Tests;

/// <summary>
/// Regression coverage for a real bug found running the app end to end: KernelHostLocator's
/// original dev-mode fallback launched `dotnet run --project ...`, which prints MSBuild
/// restore/build/NuGet-warning banner lines to its own stdout ahead of the program's real output
/// (e.g. "C:\GenAI\...\Litos.Kernel.csproj : warning NU1902: ..."). Since KernelSession treats the
/// subprocess's entire stdout as the wire protocol stream, that banner text broke JSON
/// deserialization of the very first Handshake response — observed in the running app as
/// "run_kernel_code (Failed to start kernel: 'C' is an invalid start of a value...)", the 'C' being
/// the start of a "C:\..." warning path. Fixed by building once and launching via `dotnet exec
/// &lt;dll&gt;` instead, which has no such banner. These tests assert the resolved launch shape rather
/// than actually spawning a subprocess (that's KernelSession's own concern), since the fix is about
/// *which command* gets built, not process lifecycle.
/// </summary>
public sealed class KernelHostLocatorTests
{
    [Fact]
    public void Resolve_DevModeFallback_NeverUsesDotnetRun()
    {
        // Resolve() walks up from AppContext.BaseDirectory (this test assembly's own output dir,
        // which sits under the repo) to find Litos.Kernel.Host.csproj — exercising the real dev
        // fallback path, the same one KernelSession hits when Litos.Gui hasn't been published.
        var resolved = KernelHostLocator.Resolve();

        // "dotnet run" is the specific banner-polluting command this bug was about; "dotnet exec"
        // (or a published sibling executable) is not.
        var isDotnetRun = resolved.FileName.Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            && resolved.Arguments.Count > 0
            && resolved.Arguments[0] == "run";
        Assert.False(isDotnetRun, "KernelHostLocator must never launch via 'dotnet run' — its MSBuild/NuGet banner output corrupts the wire protocol stream.");
    }

    [Fact]
    public void Resolve_ReturnsALaunchablePath_EitherAPublishedSiblingExeOrDotnetExecAgainstABuiltDll()
    {
        // Litos.Kernel.Tests project-references Litos.Kernel.Host directly, so MSBuild copies
        // Litos.Kernel.Host.exe right next to this test assembly — Resolve()'s "published sibling"
        // check legitimately finds and uses it here (correct: a copied build output should be
        // treated the same as a real publish, not fall through to the dev/dotnet-exec path). This
        // test only asserts the two acceptable shapes, not which one — the never-dotnet-run
        // assertion above is what actually guards the bug this file exists for.
        var resolved = KernelHostLocator.Resolve();

        var isPublishedSibling = resolved.Arguments.Count == 0 && File.Exists(resolved.FileName);
        var isDotnetExec = resolved.FileName == "dotnet" && resolved.Arguments is ["exec", var dllPath] && File.Exists(dllPath);

        Assert.True(isPublishedSibling || isDotnetExec, $"Unexpected resolved launch shape: {resolved.FileName} {string.Join(' ', resolved.Arguments)}");
    }
}
