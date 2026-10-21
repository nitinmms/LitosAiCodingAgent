using System.Windows.Forms;
using Litos.SoftwareFactory.Tray;
using Litos.SoftwareFactory.Tray.Windows;

// litos-factory-tray: the Windows tray app (ReadMe_LitosSoftwareFactory_V1.md §26.3).
//
//   (no arguments)   start; open Settings if it is not connected to a factory yet
//   --background     start quietly: the sign-in start (Run key), and the installer's
//   --settings       open Settings, in the copy already running if there is one
//
// One copy per Windows user: a second start only asks the first to show its Settings window
// (unless started --background), so the Start-menu shortcut doubles as "open the settings".

const string InstanceName = @"Local\LitosFactoryTray";
const string ShowSettingsName = @"Local\LitosFactoryTray.ShowSettings";

var background = args.Contains("--background", StringComparer.OrdinalIgnoreCase);
var settings = args.Contains("--settings", StringComparer.OrdinalIgnoreCase);

using var instance = new Mutex(initiallyOwned: true, InstanceName, out var first);
using var showSettings = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSettingsName);
if (!first)
{
    if (!background)
        showSettings.Set();
    return 0;
}

ApplicationConfiguration.Initialize();
var settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LitosSoftwareFactory", "tray.json");
using var app = new TrayApplication(settingsPath, new WindowsDesktop(), new CredentialStore(), LocalFactory.ForThisProgram(), openSettingsIfNotSetUp: !background);

// Another start asked for the Settings window: show it on the UI thread.
var ui = SynchronizationContext.Current!;
var registration = ThreadPool.RegisterWaitForSingleObject(showSettings, (_, _) => ui.Post(_ => app.ShowSettings(), null), null, Timeout.Infinite, executeOnlyOnce: false);
if (settings)
    ui.Post(_ => app.ShowSettings(), null);

Application.Run(app);
registration.Unregister(null);
GC.KeepAlive(instance);
return 0;
