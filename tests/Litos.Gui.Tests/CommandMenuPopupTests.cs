namespace Litos.Gui.Tests;

/// <summary>
/// Covers CommandMenuPopup.Filter, the pure live-narrowing logic behind the "+"/leading-"/"
/// command menu (see CommandMenuPopup.cs and MainWindow.UpdateCommandMenuFromTypedText) —
/// extracted the same way MainWindow.FindBranchPoints is, so it's testable without an Avalonia
/// control tree or an open Popup.
/// </summary>
public class CommandMenuPopupTests
{
    [Fact]
    public void Filter_ReturnsEverythingIncludingAttach_WhenQueryIsEmpty()
    {
        var entries = CommandMenuPopup.Filter("");

        Assert.Contains(entries, e => e.Kind == CommandMenuPopup.MenuEntryKind.Attach);
        Assert.Equal(SlashCommands.All.Count + 1, entries.Count);
    }

    [Fact]
    public void Filter_StripsLeadingSlash_BeforeMatching()
    {
        var withSlash = CommandMenuPopup.Filter("/mod");
        var withoutSlash = CommandMenuPopup.Filter("mod");

        Assert.Equal(withoutSlash.Select(e => e.Title), withSlash.Select(e => e.Title));
        Assert.Contains(withSlash, e => e.Title.StartsWith("/model", StringComparison.Ordinal));
    }

    [Fact]
    public void Filter_MatchesCaseInsensitively_OnNameOrDescription()
    {
        var byName = CommandMenuPopup.Filter("BRANCH");
        Assert.Contains(byName, e => e.Title.StartsWith("/branch", StringComparison.Ordinal));

        var byDescription = CommandMenuPopup.Filter("session");
        Assert.Contains(byDescription, e => e.Title.StartsWith("/resume", StringComparison.Ordinal));
    }

    [Fact]
    public void Filter_ReturnsEmpty_WhenQueryMatchesNothing()
    {
        Assert.Empty(CommandMenuPopup.Filter("zzz_no_such_command"));
    }

    [Fact]
    public void Filter_AttachEntry_HasNullCommand()
    {
        var attach = CommandMenuPopup.Filter("attach").Single(e => e.Kind == CommandMenuPopup.MenuEntryKind.Attach);

        Assert.Null(attach.Command);
    }

    [Fact]
    public void Filter_CommandEntries_CarryTheMatchingSlashCommand()
    {
        var entry = CommandMenuPopup.Filter("/new").Single(e => e.Kind == CommandMenuPopup.MenuEntryKind.Command);

        Assert.Equal("/new", entry.Command!.Name);
    }
}
