using Litos.VsCodeHost.Files;

namespace Litos.VsCodeHost.Tests.Files;

/// <summary>
/// Covers FileMentionIndex.Filter (ported verbatim from Litos.Gui.Tests/FileMentionIndexTests.cs
/// — same pure logic, same expected ranking) and Build's real filesystem walk, backing the
/// webview's "@"-mention dropdown (see FilesEndpoints' /sessions/{id}/mentions).
/// </summary>
public class FileMentionIndexTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"litos-vscodehost-mention-index-test-{Guid.NewGuid():n}");

    public FileMentionIndexTests() => Directory.CreateDirectory(_root);

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

    [Fact]
    public void Build_WalksRealDirectoryTree_ReturningRelativeForwardSlashPaths()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "Foo.cs"), "");
        File.WriteAllText(Path.Combine(_root, "README.md"), "");

        var result = FileMentionIndex.Build(_root);

        Assert.Contains("src/Foo.cs", result);
        Assert.Contains("README.md", result);
        Assert.Contains("src/", result);
    }

    [Fact]
    public void Build_ExcludesGitignoredEntries()
    {
        File.WriteAllText(Path.Combine(_root, ".gitignore"), "ignored.txt\n");
        File.WriteAllText(Path.Combine(_root, "ignored.txt"), "");
        File.WriteAllText(Path.Combine(_root, "kept.txt"), "");

        var result = FileMentionIndex.Build(_root);

        Assert.DoesNotContain("ignored.txt", result);
        Assert.Contains("kept.txt", result);
    }

    [Fact]
    public void Build_ExcludesBinaryExtensions()
    {
        File.WriteAllText(Path.Combine(_root, "app.exe"), "");
        File.WriteAllText(Path.Combine(_root, "native.dll"), "");
        File.WriteAllText(Path.Combine(_root, "Program.cs"), "");

        var result = FileMentionIndex.Build(_root);

        Assert.DoesNotContain("app.exe", result);
        Assert.DoesNotContain("native.dll", result);
        Assert.Contains("Program.cs", result);
    }

    [Fact]
    public void Build_IncludesImagesAndKnownDocumentFormats()
    {
        File.WriteAllText(Path.Combine(_root, "screenshot.png"), "");
        File.WriteAllText(Path.Combine(_root, "report.pdf"), "");

        var result = FileMentionIndex.Build(_root);

        Assert.Contains("screenshot.png", result);
        Assert.Contains("report.pdf", result);
    }

    [Fact]
    public void Build_StillIncludesDirectories_EvenThoughDirectoriesArentAttachable()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));

        var result = FileMentionIndex.Build(_root);

        Assert.Contains("src/", result);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
