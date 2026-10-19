using Litos.SoftwareFactory.Infrastructure.Processes;

namespace Litos.SoftwareFactory.Infrastructure.Tests;

/// <summary>The captured toolchain PATH (SoftwareFactoryInstallation.md, G6).</summary>
public class ToolchainPathTests
{
    private static readonly Dictionary<string, string> Host = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PATH"] = "/usr/bin:/bin",
        ["HOME"] = "/home/asha",
        ["ConnectionStrings__FactoryState"] = "Host=db;Password=secret",
    };

    [Fact]
    public void WithoutASetting_TheHostsPathIsPassedOn()
    {
        Assert.Equal("/usr/bin:/bin", ToolchainEnvironment.Scrub(Host)["PATH"]);
    }

    /// <summary>A service starts with a minimal PATH: the captured login PATH replaces it for every toolchain process.</summary>
    [Fact]
    public void WithASetting_ItIsThePath_AndTheSettingItselfIsNotPassedOn()
    {
        var host = new Dictionary<string, string>(Host, StringComparer.OrdinalIgnoreCase)
        {
            [ToolchainEnvironment.ToolchainPathVariable] = "/opt/homebrew/bin:/home/asha/.dotnet:/usr/bin:/bin",
        };

        var scrubbed = ToolchainEnvironment.Scrub(host);

        Assert.Equal("/opt/homebrew/bin:/home/asha/.dotnet:/usr/bin:/bin", scrubbed["PATH"]);
        Assert.False(scrubbed.ContainsKey(ToolchainEnvironment.ToolchainPathVariable));
        Assert.False(scrubbed.ContainsKey("ConnectionStrings__FactoryState"));
    }

    [Fact]
    public void ABlankSetting_IsIgnored()
    {
        var host = new Dictionary<string, string>(Host, StringComparer.OrdinalIgnoreCase) { [ToolchainEnvironment.ToolchainPathVariable] = "  " };

        Assert.Equal("/usr/bin:/bin", ToolchainEnvironment.Scrub(host)["PATH"]);
    }
}
