using System.Windows.Forms;
using Litos.SoftwareFactory.Tray;
using Litos.SoftwareFactory.Tray.Windows;

// litos-factory-tray: the Windows tray app (ReadMe_LitosSoftwareFactory_V1.md §26.3).
//
//   (no arguments)   start; open Settings if it is not connected to a factory yet
//   --background     start quietly: the sign-in start (Run key), and the installer's
//   --settings       open Settings, in the copy already running if there is one
//
// For the installers, each doing its job and exiting:
//   --register       start at sign-in, unless the person turned that off in Settings
//   --quit           ask the running copy to exit, and wait for it (before its file is replaced)
//   --unregister     --quit, and no longer start at sign-in (uninstall); with --purge, also
//                    delete its settings and every token it kept (the factory's PURGE=1)
//
// One copy per Windows user: a second start only asks the first to show its Settings window
// (unless started --background), so the Start-menu shortcut doubles as "open the settings".

var settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LitosSoftwareFactory", "tray.json");
bool Has(string option) => args.Contains(option, StringComparer.OrdinalIgnoreCase);

if (Has("--register"))
{
    InstallerCommands.Register(new WindowsDesktop(), TraySettings.Load(settingsPath));
    return 0;
}

if (Has("--quit") || Has("--unregister"))
{
    var stopped = TrayInstance.Quit(TimeSpan.FromSeconds(10));
    if (Has("--unregister"))
        InstallerCommands.Unregister(new WindowsDesktop());
    if (Has("--unregister") && Has("--purge"))
        InstallerCommands.Purge(settingsPath, new CredentialStore());
    return stopped ? 0 : 1;
}

var background = Has("--background");
using var instance = new Mutex(initiallyOwned: true, TrayInstance.MutexName, out var first);
using var showSettings = new EventWaitHandle(false, EventResetMode.AutoReset, TrayInstance.ShowSettingsEvent);
using var quit = new EventWaitHandle(false, EventResetMode.AutoReset, TrayInstance.QuitEvent);
if (!first)
{
    if (!background)
        showSettings.Set();
    return 0;
}

ApplicationConfiguration.Initialize();
using var app = new TrayApplication(settingsPath, new WindowsDesktop(), new CredentialStore(), LocalFactory.ForThisProgram(), openSettingsIfNotSetUp: !background);

// Another start asked for the Settings window, or an installer asked it to exit: on the UI thread.
var ui = SynchronizationContext.Current!;
var onShow = ThreadPool.RegisterWaitForSingleObject(showSettings, (_, _) => ui.Post(_ => app.ShowSettings(), null), null, Timeout.Infinite, executeOnlyOnce: false);
var onQuit = ThreadPool.RegisterWaitForSingleObject(quit, (_, _) => ui.Post(_ => app.ExitThread(), null), null, Timeout.Infinite, executeOnlyOnce: true);
if (Has("--settings"))
    ui.Post(_ => app.ShowSettings(), null);

Application.Run(app);
onShow.Unregister(null);
onQuit.Unregister(null);
instance.ReleaseMutex();
return 0;
