using System.Text.Json;
using Litos.Tools.FileSystem;

namespace Litos.Tools.Tests.FileSystem;

public class GrepToolTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("litos-grep-").FullName;

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private static JsonElement Args(object obj) => JsonSerializer.SerializeToElement(obj);

    [Fact]
    public async Task InvokeAsync_FindsMatchInFile()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "file.cs"), "line one\nfoo bar\nline three");
        var tool = new GrepTool();

        var result = await tool.InvokeAsync(Args(new { pattern = "foo", path = _tempDir }), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("file.cs:2:foo bar", result.Text);
    }

    [Fact]
    public async Task InvokeAsync_MissingPattern_ReturnsError()
    {
        var tool = new GrepTool();

        var result = await tool.InvokeAsync(Args(new { path = _tempDir }), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("A 'pattern' argument is required.", result.Text);
    }

    [Fact]
    public async Task InvokeAsync_MissingDirectory_ReturnsError()
    {
        var tool = new GrepTool();

        var result = await tool.InvokeAsync(Args(new { pattern = "foo", path = Path.Combine(_tempDir, "nope") }), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("Directory not found", result.Text);
    }

    [Fact]
    public async Task InvokeAsync_InvalidRegex_ReturnsError()
    {
        var tool = new GrepTool();

        var result = await tool.InvokeAsync(Args(new { pattern = "(unclosed", path = _tempDir }), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("Invalid regex", result.Text);
    }

    [Fact]
    public async Task InvokeAsync_NoMatches_ReturnsNoMatchesMessage()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "file.txt"), "nothing interesting here");
        var tool = new GrepTool();

        var result = await tool.InvokeAsync(Args(new { pattern = "zzz_not_present", path = _tempDir }), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("No matches found.", result.Text);
    }

    [Fact]
    public async Task InvokeAsync_IsCaseInsensitiveByDefault()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "file.txt"), "FOO bar");
        var tool = new GrepTool();

        var result = await tool.InvokeAsync(Args(new { pattern = "foo", path = _tempDir }), CancellationToken.None);

        Assert.Contains("FOO bar", result.Text);
    }

    [Fact]
    public async Task InvokeAsync_CaseSensitiveTrue_DoesNotMatchDifferentCase()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "file.txt"), "FOO bar");
        var tool = new GrepTool();

        var result = await tool.InvokeAsync(Args(new { pattern = "foo", path = _tempDir, case_sensitive = true }), CancellationToken.None);

        Assert.Equal("No matches found.", result.Text);
    }

    [Fact]
    public async Task InvokeAsync_GlobFiltersFilesSearched()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "match.cs"), "needle");
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "match.md"), "needle");
        var tool = new GrepTool();

        var result = await tool.InvokeAsync(Args(new { pattern = "needle", path = _tempDir, glob = "*.cs" }), CancellationToken.None);

        Assert.Contains("match.cs", result.Text);
        Assert.DoesNotContain("match.md", result.Text);
    }

    [Fact]
    public async Task InvokeAsync_ContextLines_IncludesSurroundingLines()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "file.txt"), "before\nneedle\nafter");
        var tool = new GrepTool();

        var result = await tool.InvokeAsync(Args(new { pattern = "needle", path = _tempDir, context_lines = 1 }), CancellationToken.None);

        Assert.Contains("file.txt:1:before", result.Text);
        Assert.Contains("file.txt:2:needle", result.Text);
        Assert.Contains("file.txt:3:after", result.Text);
    }

    [Fact]
    public async Task InvokeAsync_SkipsDefaultIgnoredDirectories()
    {
        var binDir = Directory.CreateDirectory(Path.Combine(_tempDir, "bin"));
        await File.WriteAllTextAsync(Path.Combine(binDir.FullName, "generated.cs"), "needle");
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "source.cs"), "needle");
        var tool = new GrepTool();

        var result = await tool.InvokeAsync(Args(new { pattern = "needle", path = _tempDir }), CancellationToken.None);

        Assert.Contains("source.cs", result.Text);
        Assert.DoesNotContain("generated.cs", result.Text);
    }

    [Fact]
    public async Task InvokeAsync_RespectsGitignore()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, ".gitignore"), "*.generated.cs\nvendor/\n");
        var vendorDir = Directory.CreateDirectory(Path.Combine(_tempDir, "vendor"));
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "Foo.generated.cs"), "needle");
        await File.WriteAllTextAsync(Path.Combine(vendorDir.FullName, "lib.cs"), "needle");
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "Foo.cs"), "needle");
        var tool = new GrepTool();

        var result = await tool.InvokeAsync(Args(new { pattern = "needle", path = _tempDir }), CancellationToken.None);

        Assert.Contains("Foo.cs:1", result.Text);
        Assert.DoesNotContain("Foo.generated.cs", result.Text);
        Assert.DoesNotContain("lib.cs", result.Text);
    }

    [Fact]
    public async Task InvokeAsync_SkipsBinaryFiles()
    {
        await File.WriteAllBytesAsync(Path.Combine(_tempDir, "data.bin"), [0x4E, 0x00, 0x45, 0x45, 0x44, 0x4C, 0x45]);
        var tool = new GrepTool();

        var result = await tool.InvokeAsync(Args(new { pattern = "NEEDLE", path = _tempDir }), CancellationToken.None);

        Assert.Equal("No matches found.", result.Text);
    }

    [Fact]
    public async Task InvokeAsync_MoreMatchesThanCap_AddsTruncationNotice()
    {
        for (var i = 0; i < 5; i++)
            await File.WriteAllTextAsync(Path.Combine(_tempDir, $"file{i}.txt"), "needle\nneedle\nneedle");
        var tool = new GrepTool();

        var result = await tool.InvokeAsync(Args(new { pattern = "needle", path = _tempDir, max_matches = 3 }), CancellationToken.None);

        Assert.Contains("[Truncated: showing 3", result.Text);
    }

    [Fact]
    public async Task InvokeAsync_MaxMatchesAboveHardCap_IsClamped()
    {
        for (var i = 0; i < 3; i++)
            await File.WriteAllTextAsync(Path.Combine(_tempDir, $"file{i}.txt"), "needle");
        var tool = new GrepTool();

        var result = await tool.InvokeAsync(Args(new { pattern = "needle", path = _tempDir, max_matches = 10_000 }), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.DoesNotContain("Truncated", result.Text);
    }

    [Fact]
    public async Task InvokeAsync_MatchCountExactlyAtCap_NoFalseTruncationNotice()
    {
        for (var i = 0; i < 3; i++)
            await File.WriteAllTextAsync(Path.Combine(_tempDir, $"file{i}.txt"), "needle");
        var tool = new GrepTool();

        var result = await tool.InvokeAsync(Args(new { pattern = "needle", path = _tempDir, max_matches = 3 }), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.DoesNotContain("Truncated", result.Text);
    }

    [Fact]
    public async Task InvokeAsync_SearchesSubdirectoriesRecursively()
    {
        var subDir = Directory.CreateDirectory(Path.Combine(_tempDir, "nested", "deeper"));
        await File.WriteAllTextAsync(Path.Combine(subDir.FullName, "deep.cs"), "needle");
        var tool = new GrepTool();

        var result = await tool.InvokeAsync(Args(new { pattern = "needle", path = _tempDir }), CancellationToken.None);

        Assert.Contains("deep.cs:1:needle", result.Text);
    }

    [Fact]
    public async Task InvokeAsync_GlobDoesNotMatchViaLegacyEightDotThreeFallback()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "foo.cs"), "needle");
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "foo.csx"), "needle");
        var tool = new GrepTool();

        var result = await tool.InvokeAsync(Args(new { pattern = "needle", path = _tempDir, glob = "*.cs" }), CancellationToken.None);

        Assert.Contains("foo.cs:1", result.Text);
        Assert.DoesNotContain("foo.csx", result.Text);
    }

    [Fact]
    public async Task InvokeAsync_NonIntegerContextLines_ReturnsError()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "file.txt"), "needle");
        var tool = new GrepTool();

        var result = await tool.InvokeAsync(
            Args(new { pattern = "needle", path = _tempDir, context_lines = "not-a-number" }), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("'context_lines' must be an integer", result.Text);
    }

    [Fact]
    public async Task InvokeAsync_NonBooleanCaseSensitive_ReturnsError()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "file.txt"), "needle");
        var tool = new GrepTool();

        var result = await tool.InvokeAsync(
            Args(new { pattern = "needle", path = _tempDir, case_sensitive = "yes" }), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("'case_sensitive' must be a boolean", result.Text);
    }

    [Fact]
    public async Task InvokeAsync_MultipleMatchesInSameFileBeyondCap_StillReportsTruncated()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "file.txt"), "needle\nneedle\nneedle\nneedle");
        var tool = new GrepTool();

        var result = await tool.InvokeAsync(Args(new { pattern = "needle", path = _tempDir, max_matches = 2 }), CancellationToken.None);

        Assert.Contains("[Truncated: showing 2", result.Text);
    }
}
