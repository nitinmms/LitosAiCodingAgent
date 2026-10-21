using System.Diagnostics;
using System.Media;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Litos.SoftwareFactory.Tray.Windows;

/// <summary>What the tray app asks of Windows, apart from its icon and windows.</summary>
internal interface IDesktop
{
    /// <summary>Windows is holding notifications back: presenting, a full-screen app, quiet time.</summary>
    bool IsBusy();

    void Open(Uri page);

    void Play(AlertSound sound);

    bool StartsAtSignIn();

    void SetStartAtSignIn(bool on);
}

/// <param name="runValue">The Run key value this program starts under; tests use their own.</param>
internal sealed class WindowsDesktop(string runValue = WindowsDesktop.RunValue) : IDesktop
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string RunValue = "LitosFactoryTray";

    private readonly Dictionary<AlertSound, byte[]?> _sounds = [];
    private SoundPlayer? _playing;

    /// <summary>The Run key's command: this program, quietly (no Settings window if not set up yet).</summary>
    public static string StartCommand => $"\"{Environment.ProcessPath}\" --background";

    public bool IsBusy() => SHQueryUserNotificationState(out var state) == 0 && IsBusyState(state);

    /// <summary>
    /// QUNS_BUSY (a full-screen app), QUNS_RUNNING_D3D_FULL_SCREEN, QUNS_PRESENTATION_MODE,
    /// QUNS_QUIET_TIME (the first hour after setup or an upgrade) and QUNS_APP (an app holding
    /// notifications back). Not present (locked, screen saver) and accepting notifications are not busy.
    /// </summary>
    public static bool IsBusyState(int state) => state is 2 or 3 or 4 or 6 or 7;

    public void Open(Uri page) => Process.Start(new ProcessStartInfo(page.ToString()) { UseShellExecute = true })?.Dispose();

    public void Play(AlertSound sound)
    {
        if (!_sounds.TryGetValue(sound, out var wav))
            _sounds[sound] = wav = Chime.Render(sound);
        if (wav is null)
            return;

        _playing?.Stop();
        _playing?.Dispose();
        _playing = new SoundPlayer(new MemoryStream(wav));
        _playing.Play();
    }

    public bool StartsAtSignIn()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(runValue) is string;
    }

    public void SetStartAtSignIn(bool on)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on)
            key.SetValue(runValue, StartCommand);
        else
            key.DeleteValue(runValue, throwOnMissingValue: false);
    }

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);
}

/// <summary>
/// The factory installed on this PC, when there is one: the tray app then sits beside
/// litos-factory.exe (the factory's MSI installs both) and offers Start, Stop, Status and Doctor,
/// and the factory's own settings file says its address.
/// </summary>
internal sealed record LocalFactory(string InstallDirectory, string EnvFile)
{
    public static LocalFactory ForThisProgram() => new(
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LitosSoftwareFactory", "factory.env"));

    public string ConsoleProgram => Path.Combine(InstallDirectory, "litos-factory.exe");

    /// <summary>The windowless build, so Start and Stop flash no console.</summary>
    public string WindowlessProgram => Path.Combine(InstallDirectory, "litos-factory-w.exe");

    public bool IsInstalled => File.Exists(ConsoleProgram);

    /// <summary>The factory's address on this PC, from factory.env's ASPNETCORE_URLS (as litos-factory reads it), or null.</summary>
    public string? Address()
    {
        try
        {
            if (!File.Exists(EnvFile))
                return null;
            var urls = File.ReadLines(EnvFile)
                .Select(line => line.Trim())
                .Where(line => line.StartsWith("ASPNETCORE_URLS=", StringComparison.Ordinal))
                .Select(line => line["ASPNETCORE_URLS=".Length..].Trim().Trim('"'))
                .LastOrDefault();
            return string.IsNullOrEmpty(urls) ? null : urls.Split(';')[0].TrimEnd('/') + "/";
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>How a menu item runs litos-factory: Start and Stop windowless; Status and Doctor in a
    /// console that stays open, as the Start menu's Status shortcut does.</summary>
    public ProcessStartInfo Command(MenuAction action) => action switch
    {
        MenuAction.Start => new ProcessStartInfo(WindowlessProgram, "start") { WorkingDirectory = InstallDirectory, UseShellExecute = false },
        MenuAction.Stop => new ProcessStartInfo(WindowlessProgram, "stop") { WorkingDirectory = InstallDirectory, UseShellExecute = false },
        MenuAction.Status or MenuAction.Doctor => new ProcessStartInfo(
            Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            $"/k \"\"{ConsoleProgram}\" {(action == MenuAction.Status ? "status" : "doctor")}\"")
        {
            WorkingDirectory = InstallDirectory,
            UseShellExecute = true,
        },
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Not a litos-factory command."),
    };
}
