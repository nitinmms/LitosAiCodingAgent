using Avalonia;
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
            desktop.MainWindow = new MainWindow(Session, WorkingDirectory, ResumeSessionId);

        base.OnFrameworkInitializationCompleted();
    }
}
