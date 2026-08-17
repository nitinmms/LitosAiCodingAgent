using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using LineStyle = Terminal.Gui.Drawing.LineStyle;

namespace Litos.Console.Terminal;

/// <summary>
/// Root Window: TranscriptView filling available height above the composer, WorkingIndicator
/// docked just above the composer, Composer pinned to the bottom via Pos.AnchorEnd(). This is
/// the app's entire "own the screen" footprint (ReadMe_AgentDesign.md §7.5) — everything else
/// (approval dialogs, pickers, the setup wizard) is a modal Dialog run on top of this Window.
/// </summary>
public sealed class LitosApp : Window
{
    public TranscriptView Transcript { get; } = new();
    public WorkingIndicator Working { get; } = new();
    public Composer Composer { get; }

    public LitosApp(IApplication app, Func<string> workingDirectoryProvider)
    {
        Composer = new Composer(app, workingDirectoryProvider);
        BorderStyle = LineStyle.None;
        Width = Dim.Fill();
        Height = Dim.Fill();

        Composer.Height = Dim.Auto(minimumContentDim: Dim.Absolute(1));
        Composer.Width = Dim.Fill();
        Composer.Y = Pos.AnchorEnd();

        Working.Y = Pos.Top(Composer) - 1;
        Working.Height = 1;
        Working.Width = Dim.Fill();

        Transcript.X = 0;
        Transcript.Y = 0;
        Transcript.Width = Dim.Fill();
        // WorkingIndicator.Visible itself is never toggled (only its child spinner/label are,
        // in Start()/Stop()) — IsRunning reflects the actual on/off state that determines
        // whether the extra row above the composer is needed.
        Transcript.Height = Dim.Fill(Dim.Func(_ => Working.IsRunning ? 2 : 1));

        Add(Transcript, Working, Composer);
        Composer.SetFocus();
    }

    /// <summary>Forwards to Composer's @-mention cache invalidation — call after /new, /resume, /branch reassign the working directory.</summary>
    public void InvalidateMentionCache() => Composer.InvalidateMentionCache();
}
