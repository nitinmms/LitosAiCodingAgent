namespace Litos.Gui.Tests;

/// <summary>
/// Covers FileMentionIndex.Filter, the pure live-narrowing/ranking logic behind the "@"-mention
/// popup (see FileMentionIndex.cs and MainWindow.UpdateMentionPopupFromTypedText) — extracted the
/// same way CommandMenuPopup.Filter is, so it's testable without an Avalonia control tree, an
/// open Popup, or a real filesystem walk.
/// </summary>
public class FileMentionIndexTests
{
    private static readonly IReadOnlyList<string> Index =
    [
        "src/MainWindow.cs",
        "src/MainWindow.axaml",
        "src/Program.cs",
        "src/Sub/Deep/Program.cs",
        "README.md",
        "docs/",
    ];

    [Fact]
    public void Filter_ReturnsCappedPrefix_WhenTokenIsEmpty()
    {
        var result = FileMentionIndex.Filter(Index, "");

        Assert.Equal(Index.Take(8), result);
    }

    [Fact]
    public void Filter_MatchesCaseInsensitiveSubstring()
    {
        var result = FileMentionIndex.Filter(Index, "readme");

        Assert.Contains("README.md", result);
    }

    [Fact]
    public void Filter_RanksStartsWithMatches_AboveContainsMatches()
    {
        // Both entries contain "main"; only "main.py" starts with it, so it must rank first
        // despite "src/main.py" being the shorter overall path.
        IReadOnlyList<string> index = ["src/main.py", "main.py"];

        var result = FileMentionIndex.Filter(index, "main");

        Assert.Equal("main.py", result[0]);
        Assert.Equal("src/main.py", result[1]);
    }

    [Fact]
    public void Filter_BreaksTiesByShortestPath()
    {
        var result = FileMentionIndex.Filter(Index, "Program.cs");

        Assert.Equal("src/Program.cs", result[0]);
        Assert.Equal("src/Sub/Deep/Program.cs", result[1]);
    }

    [Fact]
    public void Filter_ReturnsEmpty_WhenTokenMatchesNothing()
    {
        Assert.Empty(FileMentionIndex.Filter(Index, "zzz_no_such_file"));
    }

    [Fact]
    public void Filter_CapsAtEightResults()
    {
        var large = Enumerable.Range(0, 20).Select(i => $"file{i}.txt").ToList();

        var result = FileMentionIndex.Filter(large, "file");

        Assert.Equal(8, result.Count);
    }
}
