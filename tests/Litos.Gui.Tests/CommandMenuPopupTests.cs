using ModelContextProtocol.Protocol;

namespace Litos.Gui.Tests;

/// <summary>
/// Covers CommandMenuPopup.Filter, the pure live-narrowing logic behind the "+"/leading-"/"
/// command menu (see CommandMenuPopup.cs and MainWindow.UpdateCommandMenuFromTypedText) —
/// extracted the same way MainWindow.FindBranchPoints is, so it's testable without an Avalonia
/// control tree or an open Popup.
///
/// MCP prompt entries are exercised here only with an empty prompt list (regression coverage
/// that Filter/BuildAllEntries's existing behavior is unchanged) — McpPromptEntry wraps an SDK
/// McpClientPrompt, which has no construction path other than a live McpClient connection (no
/// fake/mockable transport exists anywhere in this codebase, the same reason
/// McpToolProviderTests never asserts on a genuinely Connected server's real Tools either).
/// CommandMenuPopup.FormatSubtitle — the one piece of prompt-entry-specific formatting logic
/// that doesn't require an McpClientPrompt — is covered directly below instead.
/// </summary>
public class CommandMenuPopupTests
{
    private static IReadOnlyList<CommandMenuPopup.MenuEntry> AllEntries() => CommandMenuPopup.BuildAllEntries([]);

    [Fact]
    public void Filter_ReturnsEverythingIncludingAttach_WhenQueryIsEmpty()
    {
        var entries = CommandMenuPopup.Filter(AllEntries(), "");

        Assert.Contains(entries, e => e.Kind == CommandMenuPopup.MenuEntryKind.Attach);
        Assert.Equal(SlashCommands.All.Count + 1, entries.Count);
    }

    [Fact]
    public void Filter_StripsLeadingSlash_BeforeMatching()
    {
        var entries = AllEntries();
        var withSlash = CommandMenuPopup.Filter(entries, "/mod");
        var withoutSlash = CommandMenuPopup.Filter(entries, "mod");

        Assert.Equal(withoutSlash.Select(e => e.Title), withSlash.Select(e => e.Title));
        Assert.Contains(withSlash, e => e.Title.StartsWith("/model", StringComparison.Ordinal));
    }

    [Fact]
    public void Filter_MatchesCaseInsensitively_OnNameOrDescription()
    {
        var entries = AllEntries();
        var byName = CommandMenuPopup.Filter(entries, "BRANCH");
        Assert.Contains(byName, e => e.Title.StartsWith("/branch", StringComparison.Ordinal));

        var byDescription = CommandMenuPopup.Filter(entries, "session");
        Assert.Contains(byDescription, e => e.Title.StartsWith("/resume", StringComparison.Ordinal));
    }

    [Fact]
    public void Filter_ReturnsEmpty_WhenQueryMatchesNothing()
    {
        Assert.Empty(CommandMenuPopup.Filter(AllEntries(), "zzz_no_such_command"));
    }

    [Fact]
    public void Filter_AttachEntry_HasNullCommand()
    {
        var attach = CommandMenuPopup.Filter(AllEntries(), "attach").Single(e => e.Kind == CommandMenuPopup.MenuEntryKind.Attach);

        Assert.Null(attach.Command);
    }

    [Fact]
    public void Filter_CommandEntries_CarryTheMatchingSlashCommand()
    {
        var entry = CommandMenuPopup.Filter(AllEntries(), "/new").Single(e => e.Kind == CommandMenuPopup.MenuEntryKind.Command);

        Assert.Equal("/new", entry.Command!.Name);
    }

    [Fact]
    public void BuildAllEntries_EmptyPromptList_BehavesExactlyAsBeforePromptsExisted()
    {
        var entries = CommandMenuPopup.BuildAllEntries([]);

        Assert.DoesNotContain(entries, e => e.Kind == CommandMenuPopup.MenuEntryKind.Prompt);
        Assert.Equal(SlashCommands.All.Count + 1, entries.Count);
    }

    [Fact]
    public void FormatSubtitle_NoDescription_FallsBackToPlaceholder()
    {
        Assert.Equal("(no description)", CommandMenuPopup.FormatSubtitle(null, []));
        Assert.Equal("(no description)", CommandMenuPopup.FormatSubtitle("", []));
    }

    [Fact]
    public void FormatSubtitle_WithDescriptionNoArguments_ReturnsDescriptionAlone()
    {
        Assert.Equal("Reviews a PR", CommandMenuPopup.FormatSubtitle("Reviews a PR", []));
    }

    [Fact]
    public void FormatSubtitle_WithArguments_AppendsArgsHint()
    {
        var args = new List<PromptArgument> { new() { Name = "pr", Required = true } };

        Assert.Equal("Reviews a PR — args: <pr>", CommandMenuPopup.FormatSubtitle("Reviews a PR", args));
    }
}
