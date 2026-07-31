using Litos.Agent.Messages;
using Litos.Agent.Session;
using Litos.Agent.Tools;

namespace Litos.Gui.Tests;

/// <summary>
/// Covers MainWindow.FindBranchPoints, the pure candidate-list logic behind the /branch picker
/// (see HandleBranchAsync in MainWindow.axaml.cs) — extracted the same way
/// Program.ResolveStartupWorkingDirectory is, so it's testable without instantiating an Avalonia
/// window. EntryIndex must exactly match the position ITranscriptStore.BranchAsync expects for
/// "keep the first N entries", so these tests pin that index isn't shifted by filtering.
/// </summary>
public class MainWindowBranchTests
{
    private static TranscriptEntry UserEntry(string text, DateTimeOffset? at = null) =>
        TranscriptEntry.FromMessage(ChatMessage.User(text)) with { Timestamp = at ?? DateTimeOffset.UtcNow };

    private static TranscriptEntry AssistantEntry(string text) =>
        TranscriptEntry.FromMessage(ChatMessage.Assistant([new TextBlock(text)]));

    private static TranscriptEntry ToolResultEntry(string callId, string text) =>
        TranscriptEntry.FromMessage(ChatMessage.ToolResult(callId, new ToolResult(text)));

    [Fact]
    public void FindBranchPoints_ReturnsEmpty_WhenTranscriptHasNoEntries()
    {
        Assert.Empty(MainWindow.FindBranchPoints([]));
    }

    [Fact]
    public void FindBranchPoints_ReturnsEmpty_WhenOnlyASessionHeaderEntryExists()
    {
        List<TranscriptEntry> entries = [TranscriptEntry.SessionHeader("/repo")];

        Assert.Empty(MainWindow.FindBranchPoints(entries));
    }

    [Fact]
    public void FindBranchPoints_IncludesGenuineUserMessages_WithCorrectEntryIndex()
    {
        List<TranscriptEntry> entries =
        [
            TranscriptEntry.SessionHeader("/repo"),  // index 0: not a candidate
            UserEntry("first question"),              // index 1
            AssistantEntry("first answer"),            // index 2: not a candidate (not User)
            UserEntry("second question"),              // index 3
        ];

        var points = MainWindow.FindBranchPoints(entries);

        Assert.Equal(2, points.Count);
        Assert.Equal(1, points[0].EntryIndex);
        Assert.Equal("first question", points[0].Text);
        Assert.Equal(3, points[1].EntryIndex);
        Assert.Equal("second question", points[1].Text);
    }

    [Fact]
    public void FindBranchPoints_ExcludesToolResultsEvenThoughTheyAreRoleUser()
    {
        // ChatMessage.ToolResult wraps its result as Role.User too (pairing convention), but it's
        // never something the user typed, so it must not appear as something to branch "from".
        List<TranscriptEntry> entries =
        [
            UserEntry("read the file"),
            AssistantEntry("(tool call omitted for this test)"),
            ToolResultEntry("call_1", "file contents"),
        ];

        var points = MainWindow.FindBranchPoints(entries);

        Assert.Single(points);
        Assert.Equal(0, points[0].EntryIndex);
        Assert.Equal("read the file", points[0].Text);
    }

    [Fact]
    public void FindBranchPoints_UsesNoTextPlaceholder_WhenUserMessageHasNoTextBlock()
    {
        // An image-only user message (e.g. a pasted screenshot with no accompanying caption)
        // still has Role.User and no ToolResultBlock, so it's a valid candidate, but there's no
        // TextBlock to preview.
        var imageOnly = TranscriptEntry.FromMessage(ChatMessage.User([new ImageBlock("image/png", [1, 2, 3])]));

        var points = MainWindow.FindBranchPoints([imageOnly]);

        Assert.Single(points);
        Assert.Equal("(no text)", points[0].Text);
    }

    [Fact]
    public void FindBranchPoints_PreservesTimestampFromTheEntry()
    {
        var at = new DateTimeOffset(2026, 7, 18, 9, 30, 0, TimeSpan.Zero);

        var points = MainWindow.FindBranchPoints([UserEntry("hi", at)]);

        Assert.Equal(at, points[0].Timestamp);
    }
}
