using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Litos.Gui;

public sealed class App : Application
{
    public required MainWindowSession Session { get; init; }
    public required string WorkingDirectory { get; init; }

    // Set only when this process was launched by SelfUpdater's relaunch step (see
    // SessionResumeArgs) — MainWindow resumes this session instead of starting a fresh one, so an
    // in-progress conversation survives the update.
    public string? ResumeSessionId { get; init; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // The main window decides when Litos ends: hidden to the tray it is still open, and a
            // routine's notification window left on screen must not keep the app alive after Quit.
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            desktop.MainWindow = new MainWindow(Session, WorkingDirectory, ResumeSessionId);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
