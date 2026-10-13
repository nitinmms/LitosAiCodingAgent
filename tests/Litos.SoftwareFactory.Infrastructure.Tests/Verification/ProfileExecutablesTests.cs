using Litos.SoftwareFactory.Core.Verification;
using Litos.SoftwareFactory.Infrastructure.Verification;

namespace Litos.SoftwareFactory.Infrastructure.Tests.Verification;

/// <summary>Registration's check that the host can start what a profile names (m3-architecture.md §8).</summary>
public sealed class ProfileExecutablesTests : IDisposable
{
    private readonly string _bin = Directory.CreateTempSubdirectory("litos-path-").FullName;

    public void Dispose() => Directory.Delete(_bin, recursive: true);

    /// <summary>A program in the test's own PATH folder, as the platform names one.</summary>
    private void Install(string name) =>
        File.WriteAllText(Path.Combine(_bin, OperatingSystem.IsWindows() ? name + ".cmd" : name), "");

    private static VerificationProfile Using(params string[] executables) => new(
        VerificationProfile.CurrentVersion,
        [
            new VerificationStep(
                "app",
                Restore: new CommandSpec(executables[0], []),
                UnitTests: [.. executables.Skip(1).Select(e => new UnitTestCommand(e, []))]),
        ]);

    [Fact]
    public void AProgramOnThePath_IsFound_AndOneThatIsNot_IsNamed_Once()
    {
        Install("mvn");

        var missing = ProfileExecutables.Missing(Using("mvn", "gradle", "Gradle", "mvn"), _bin);

        Assert.Equal(["gradle"], missing);
    }

    [Fact]
    public void APathRelativeToTheRepository_IsNotChecked_ForTheRepositoryIsNotThereYet()
    {
        Assert.Empty(ProfileExecutables.Missing(Using("./gradlew", "tools/run-tests", @"scripts\test.cmd"), _bin));
    }

    [Fact]
    public void WithNoPath_EveryBareNameIsMissing()
    {
        Assert.Equal(["dotnet"], ProfileExecutables.Missing(Using("dotnet", "dotnet"), null));
    }

    [Fact]
    public void ANameWithItsExtension_IsFoundAsWritten()
    {
        Install("tool");
        var name = OperatingSystem.IsWindows() ? "tool.cmd" : "tool";

        Assert.Empty(ProfileExecutables.Missing(Using(name, name), _bin));
        Assert.Equal(["tool.exe"], ProfileExecutables.Missing(Using("tool.exe", "tool.exe"), _bin));
    }
}
