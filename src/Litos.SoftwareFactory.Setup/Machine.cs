using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Win32;

namespace Litos.SoftwareFactory.Setup;

/// <summary>What litos-factory looks up and changes on this machine outside its own folders; faked in tests.</summary>
public interface IMachine
{
    /// <summary>docker.exe, or null when Docker is not installed.</summary>
    string? FindDocker();

    /// <summary>The Docker app to start, Docker Desktop first; null when neither it nor Rancher Desktop is installed.</summary>
    DockerEngine? FindDockerEngine();

    /// <summary>Starts the Docker app as this user; it is ready once <see cref="Docker.IsRunningAsync"/> says so.</summary>
    void StartDockerEngine(DockerEngine engine);

    bool IsPortFree(int port);

    /// <summary>The machine and user PATH as the registry has them now (G6).</summary>
    string CaptureToolchainPath();

    bool LongPathsEnabled();

    /// <summary>Opens a URL or a file as Explorer would: the browser, Notepad.</summary>
    void Open(string target);

    /// <summary>A value in the user's Run key (the start at sign-in when the Scheduled Task is refused); null removes it.</summary>
    void SetRunKey(string name, string? command);

    bool HasRunKey(string name);
}

public sealed class WindowsMachine : IMachine
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private static string ProgramFiles => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private static string AppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    /// <summary>On this process's PATH and on the PATH as the registry has it now: Docker installed
    /// after sign-in is found without signing out.</summary>
    public string? FindDocker() =>
        Docker.Find($"{Environment.GetEnvironmentVariable("PATH")};{CaptureToolchainPath()}", ProgramFiles, LocalAppData);

    public DockerEngine? FindDockerEngine() => Docker.FindEngine(ProgramFiles, LocalAppData, AppData);

    public void StartDockerEngine(DockerEngine engine) => Shell.Open(engine.Application);

    public bool IsPortFree(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    public string CaptureToolchainPath() => ToolchainPath.Capture();

    public bool LongPathsEnabled()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\FileSystem");
        return key?.GetValue("LongPathsEnabled") is int value && value == 1;
    }

    public void Open(string target) => Shell.Open(target);

    public void SetRunKey(string name, string? command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (command is null)
            key.DeleteValue(name, throwOnMissingValue: false);
        else
            key.SetValue(name, command);
    }

    public bool HasRunKey(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(name) is not null;
    }
}

public static class Ports
{
    /// <summary>The first free port from <paramref name="preferred"/> up: a dev setup on this machine
    /// already holds 5180 and 5433 (§14.5).</summary>
    public static int FirstFree(int preferred, Func<int, bool> isFree)
    {
        for (var port = preferred; port < preferred + 100; port++)
        {
            if (isFree(port))
                return port;
        }

        throw new InvalidOperationException($"No free port was found from {preferred} to {preferred + 99}.");
    }
}

public static class Shell
{
    /// <summary>Through the shell, which passes on no handles: a terminal running setup would
    /// otherwise wait for Notepad to close before it returned.</summary>
    public static void Open(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
}
