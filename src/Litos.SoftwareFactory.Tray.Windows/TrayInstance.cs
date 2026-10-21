namespace Litos.SoftwareFactory.Tray.Windows;

/// <summary>
/// The one running copy per Windows user, and how other starts talk to it: a named mutex says it
/// runs, and two named events ask it to show Settings or to exit. Local\ names are per session.
/// </summary>
internal static class TrayInstance
{
    public const string MutexName = @"Local\LitosFactoryTray";
    public const string ShowSettingsEvent = @"Local\LitosFactoryTray.ShowSettings";
    public const string QuitEvent = @"Local\LitosFactoryTray.Quit";

    public static bool IsRunning(string mutexName = MutexName)
    {
        if (!Mutex.TryOpenExisting(mutexName, out var mutex))
            return false;
        mutex.Dispose();
        return true;
    }

    /// <summary>Asks the running copy to exit and waits until it has. True when none runs any more.</summary>
    public static bool Quit(TimeSpan wait, string mutexName = MutexName, string quitEvent = QuitEvent)
    {
        if (!IsRunning(mutexName))
            return true;
        if (EventWaitHandle.TryOpenExisting(quitEvent, out var quit))
        {
            using (quit)
                quit.Set();
        }

        var deadline = DateTime.UtcNow + wait;
        while (IsRunning(mutexName))
        {
            if (DateTime.UtcNow > deadline)
                return false;
            Thread.Sleep(100);
        }

        return true;
    }
}

/// <summary>What the installers ask of the tray app besides starting it (Program.cs).</summary>
internal static class InstallerCommands
{
    /// <summary>Start at sign-in, unless the person turned that off in Settings: a reinstall or an
    /// upgrade keeps their choice. A new install has no settings yet, and starts at sign-in.</summary>
    public static void Register(IDesktop desktop, TraySettings settings) => desktop.SetStartAtSignIn(settings.StartAtSignIn);

    public static void Unregister(IDesktop desktop) => desktop.SetStartAtSignIn(false);

    /// <summary>A purged uninstall of the factory (msiexec /x … PURGE=1) removes everything: the tray
    /// app's settings and the token of every factory it was connected to.</summary>
    public static void Purge(string settingsPath, CredentialStore tokens)
    {
        // litos-factory's own purge may have removed the folder already: that is not an error.
        if (File.Exists(settingsPath))
            File.Delete(settingsPath);
        tokens.DeleteAll();
    }
}
