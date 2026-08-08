using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Litos.Gui;

/// <summary>
/// Standalone mini Avalonia app used only for the first-run "no API key found" flow (Program.cs)
/// — runs to completion via its own StartWithClassicDesktopLifetime call *before* the real App/
/// MainWindowSession are ever constructed, since AppBuilder.Configure/Start is one-shot per
/// process. Exposes whether the user saved a key so Program.Main can decide whether to exit.
/// </summary>
public sealed class ApiKeysApp : Application
{
    public bool KeysSaved { get; private set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = ApiKeysWindow.CreateForFirstRun();
            window.Closed += (_, _) => KeysSaved = window.Saved;
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }

    public static bool Run(string[] args)
    {
        var app = new ApiKeysApp();
        AppBuilder.Configure(() => app)
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .StartWithClassicDesktopLifetime(args);
        return app.KeysSaved;
    }
}
