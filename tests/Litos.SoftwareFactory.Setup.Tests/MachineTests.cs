namespace Litos.SoftwareFactory.Setup.Tests;

/// <summary>Ports and the toolchain PATH, as this machine has them.</summary>
public sealed class MachineTests
{
    [Fact]
    public void APort_IsTheFirstFreeOneFromThePreferred()
    {
        Assert.Equal(5180, Ports.FirstFree(5180, _ => true));
        Assert.Equal(5182, Ports.FirstFree(5180, port => port > 5181));
        Assert.Throws<InvalidOperationException>(() => Ports.FirstFree(5180, _ => false));
    }

    [Fact]
    public void APortInUse_IsNotFree()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var machine = new WindowsMachine();

        Assert.False(machine.IsPortFree(port));
        Assert.NotEqual(port, Ports.FirstFree(port, machine.IsPortFree));
    }

    [Fact]
    public void TheToolchainPath_IsMachineThenUser_Expanded_WithoutRepeats()
    {
        var combined = ToolchainPath.Combine(
            @"C:\Windows;%SystemRoot%\System32;;C:\Program Files\Git\cmd",
            @"%USERPROFILE%\.dotnet\tools;C:\Windows\;C:\maven\bin",
            text => text.Replace("%SystemRoot%", @"C:\Windows").Replace("%USERPROFILE%", @"C:\Users\me"));

        Assert.Equal(@"C:\Windows;C:\Windows\System32;C:\Program Files\Git\cmd;C:\Users\me\.dotnet\tools;C:\maven\bin", combined);
    }

    [Fact]
    public void AScript_IsFoundByItsExtension()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\node\npm.cmd" };

        Assert.Null(ExecutableSearch.Find("npm", @"C:\node", files.Contains));
        Assert.Equal(@"C:\node\npm.cmd", ExecutableSearch.Find("npm", @"C:\node", files.Contains, ".cmd"));
    }
}
