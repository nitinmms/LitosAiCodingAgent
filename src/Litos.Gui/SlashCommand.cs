namespace Litos.Gui;

/// <summary>
/// One entry in the "+"/leading-"/" command menu (CommandMenuPopup): a slash command's display
/// metadata, kept separate from TryHandleSlashCommandAsync's dispatch switch
/// (MainWindow.axaml.cs) since that switch has no natural place to carry a human-readable
/// description. SlashCommands.All below is the single source of truth for what the picker lists;
/// TryHandleSlashCommandAsync remains the single source of truth for what actually runs a
/// command, so the two are consulted independently but intentionally kept in sync by hand (adding
/// a case there should add an entry here too). Picking any entry runs TryHandleSlashCommandAsync
/// with no argument — for /resume, /attach, /provider, /model, /skill that means the command's
/// own no-argument fallback (its ListPickerWindow or, for /attach, the native file picker) rather
/// than leaving text in InputBox for the user to type an argument themselves.
/// </summary>
internal sealed record SlashCommand(string Name, string Description);

internal static class SlashCommands
{
    public static readonly IReadOnlyList<SlashCommand> All =
    [
        new("/new", "Start a new session"),
        new("/resume", "Resume a previous session"),
        new("/attach", "Attach a file, folder, or URL"),
        new("/provider", "Switch chat provider"),
        new("/model", "Switch model"),
        new("/skills", "Browse available skills"),
        new("/skill", "Load a skill by name"),
        new("/branch", "Rewind to an earlier message in this session"),
        new("/compact", "Summarize and compact the conversation"),
    ];
}
