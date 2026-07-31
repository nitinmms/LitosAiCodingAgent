using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Litos.Gui;

public sealed class App : Application
{
    public required MainWindowSession Session { get; init; }
    public required string WorkingDirectory { get; init; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow(Session, WorkingDirectory);

        base.OnFrameworkInitializationCompleted();
    }
}
