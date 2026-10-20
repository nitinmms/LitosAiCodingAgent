namespace Litos.SoftwareFactory.Setup.Tests;

/// <summary>Finding docker.exe and the Docker app, and reading what Docker says (§4.3 step 1, G10).</summary>
public sealed class DockerTests
{
    [Fact]
    public void Docker_IsTheExeOnThePath_NotAStrayExtensionlessFileBeforeIt()
    {
        // G10: C:\WINDOWS\system32\docker, an empty file, shadowed docker.exe for PowerShell.
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\WINDOWS\system32\docker", @"C:\Docker\bin\docker.exe" };

        var found = Docker.Find(@"C:\WINDOWS\system32;""C:\Docker\bin""", @"C:\Program Files", @"C:\Local", files.Contains);

        Assert.Equal(@"C:\Docker\bin\docker.exe", found);
    }

    [Fact]
    public void Docker_NotOnThePath_IsFoundWhereDockerDesktopOrRancherDesktopInstallIt()
    {
        var rancher = @"C:\Local\Programs\Rancher Desktop\resources\resources\win32\bin\docker.exe";
        var desktop = @"C:\Program Files\Docker\Docker\resources\bin\docker.exe";

        Assert.Equal(rancher, Docker.Find(@"C:\Windows", @"C:\Program Files", @"C:\Local", path => path == rancher));
        Assert.Equal(desktop, Docker.Find(null, @"C:\Program Files", @"C:\Local", path => path == desktop || path == rancher));
        Assert.Null(Docker.Find(@"C:\Windows", @"C:\Program Files", @"C:\Local", _ => false));
    }

    [Fact]
    public void TheDockerApp_IsDockerDesktopFirst_ThenRancherDesktop()
    {
        var desktop = @"C:\Program Files\Docker\Docker\Docker Desktop.exe";
        var rancher = @"C:\Local\Programs\Rancher Desktop\Rancher Desktop.exe";

        // The machine's own registry may name Docker Desktop's folder too; it is the same folder here.
        Assert.Equal(("Docker Desktop", desktop), Pick(path => path == desktop || path == rancher));
        Assert.Equal(("Rancher Desktop", rancher), Pick(path => path == rancher));
        Assert.Null(Docker.FindEngine(@"C:\Nowhere", @"C:\Nowhere", @"C:\Nowhere", _ => false));

        static (string, string)? Pick(Func<string, bool> exists) =>
            Docker.FindEngine(@"C:\Program Files", @"C:\Local", @"C:\Nowhere", exists) is { } engine ? (engine.Name, engine.Application) : null;
    }

    [Theory]
    [InlineData("""{ "AutoStart": false, "Other": 1 }""", false)]
    [InlineData("""{ "autoStart": true }""", true)]
    [InlineData("""{ "Other": 1 }""", null)]
    [InlineData("not json", null)]
    public void DockerDesktopsStartAtSignIn_IsReadFromItsSettings(string json, bool? expected) =>
        Assert.Equal(expected, Docker.ReadAutoStart(json));

    [Theory]
    [InlineData("2.29.7", 2)]
    [InlineData("v2.40.3-desktop.1", 2)]
    [InlineData("1.29.2\n", 1)]
    [InlineData("", null)]
    public void ComposesMajorVersion_IsParsed(string version, int? expected) =>
        Assert.Equal(expected, Docker.ParseMajorVersion(version));
}
