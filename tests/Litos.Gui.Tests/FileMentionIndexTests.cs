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

/// <summary>
/// Covers the real filesystem walk in FileMentionIndex.GetOrBuild/Build, specifically the
/// attach-eligibility filtering added so the "@" popup doesn't offer files that AttachHandler
/// (AttachPathAsync) always rejects — see FileMentionIndex.IsAttachable.
/// </summary>
public class FileMentionIndexBuildTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"litos-gui-mention-index-test-{Guid.NewGuid():n}");

    public FileMentionIndexBuildTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void GetOrBuild_ExcludesBinaryExtensions()
    {
        File.WriteAllText(Path.Combine(_root, "app.exe"), "");
        File.WriteAllText(Path.Combine(_root, "native.dll"), "");
        File.WriteAllText(Path.Combine(_root, "Program.cs"), "");

        var result = new FileMentionIndex(_root).GetOrBuild();

        Assert.DoesNotContain("app.exe", result);
        Assert.DoesNotContain("native.dll", result);
        Assert.Contains("Program.cs", result);
    }

    [Fact]
    public void GetOrBuild_IncludesImagesAndKnownDocumentFormats()
    {
        File.WriteAllText(Path.Combine(_root, "screenshot.png"), "");
        File.WriteAllText(Path.Combine(_root, "report.pdf"), "");

        var result = new FileMentionIndex(_root).GetOrBuild();

        Assert.Contains("screenshot.png", result);
        Assert.Contains("report.pdf", result);
    }

    [Fact]
    public void GetOrBuild_StillIncludesDirectories_EvenThoughDirectoriesArentAttachable()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));

        var result = new FileMentionIndex(_root).GetOrBuild();

        Assert.Contains("src/", result);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
