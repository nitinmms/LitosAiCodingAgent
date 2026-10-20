using System.Windows.Forms;

namespace Litos.SoftwareFactory.Setup;

/// <summary>What a person is told or asked. In a terminal it is text; from the installer and the
/// Start-menu shortcut, where there is no console, it is a dialog.</summary>
public interface IUserInterface
{
    /// <summary>Something stopped the factory from starting: one sentence, the fix, and a link if there is one.</summary>
    void Fail(string message, string? link = null);

    /// <summary>Asks to start Docker now; <c>Always</c> means "and from now on without asking".</summary>
    (bool Start, bool Always) AskToStartDocker(string engineName);
}

public sealed class ConsoleInterface(TextWriter output) : IUserInterface
{
    public void Fail(string message, string? link = null) =>
        output.WriteLine(link is null ? message : $"{message}{Environment.NewLine}See {link}");

    public (bool Start, bool Always) AskToStartDocker(string engineName)
    {
        if (Console.IsInputRedirected)
            return (false, false);
        output.Write($"Docker isn't running. Start {engineName} now? [y]es, [a]lways, [N]o: ");
        return Console.ReadLine()?.Trim().ToLowerInvariant() switch
        {
            "y" or "yes" => (true, false),
            "a" or "always" => (true, true),
            _ => (false, false),
        };
    }
}

public sealed class DialogInterface : IUserInterface
{
    private const string Caption = "Litos Software Factory";

    public void Fail(string message, string? link = null)
    {
        var open = new TaskDialogButton("Open the instructions");
        var page = new TaskDialogPage
        {
            Caption = Caption,
            Heading = "Litos Software Factory can't start yet",
            Text = message,
            Icon = TaskDialogIcon.Warning,
        };
        if (link is not null)
            page.Buttons.Add(open);
        page.Buttons.Add(TaskDialogButton.Close);

        if (Show(page) == open && link is not null)
            Shell.Open(link);
    }

    public (bool Start, bool Always) AskToStartDocker(string engineName)
    {
        var start = new TaskDialogButton($"Start {engineName}");
        var page = new TaskDialogPage
        {
            Caption = Caption,
            Heading = "Docker isn't running",
            Text = "The factory needs Docker for its database. It can take a minute or two to start.",
            Icon = TaskDialogIcon.Information,
            Buttons = { start, TaskDialogButton.Cancel },
            Verification = new TaskDialogVerificationCheckBox("Always start Docker automatically"),
        };

        var answer = Show(page);
        return (answer == start, answer == start && page.Verification.Checked);
    }

    /// <summary>Dialogs need a single-threaded apartment; this program's async code runs on the pool.</summary>
    private static TaskDialogButton Show(TaskDialogPage page)
    {
        TaskDialogButton? result = null;
        var thread = new Thread(() =>
        {
            Application.EnableVisualStyles();
            result = TaskDialog.ShowDialog(page, TaskDialogStartupLocation.CenterScreen);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result!;
    }
}
