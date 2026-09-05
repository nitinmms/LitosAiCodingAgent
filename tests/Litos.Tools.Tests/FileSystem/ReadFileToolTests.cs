using System.Text.Json;
using Litos.Tools.FileSystem;

namespace Litos.Tools.Tests.FileSystem;

public class ReadFileToolTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("litos-readfile-").FullName;

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private static JsonElement Args(object obj) => JsonSerializer.SerializeToElement(obj);

    [Fact]
    public async Task InvokeAsync_ReadsExistingFile()
    {
        var path = Path.Combine(_tempDir, "file.txt");
        await File.WriteAllTextAsync(path, "hello world");
        var tool = new ReadFileTool();

        var result = await tool.InvokeAsync(Args(new { path }), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("1\thello world", result.Text);
    }

    [Fact]
    public async Task InvokeAsync_MissingFile_ReturnsError()
    {
        var tool = new ReadFileTool();

        var result = await tool.InvokeAsync(Args(new { path = Path.Combine(_tempDir, "nope.txt") }), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("File not found", result.Text);
    }

    [Fact]
    public async Task InvokeAsync_WhitespacePath_ReturnsError()
    {
        var tool = new ReadFileTool();

        var result = await tool.InvokeAsync(Args(new { path = "   " }), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("A 'path' argument is required.", result.Text);
    }

    [Fact]
    public async Task InvokeAsync_MissingPathProperty_ReturnsError()
    {
        // A local model's tool-call JSON omitting a required argument is a real, model-driven
        // failure mode (more common for local/smaller models than hosted ones) — must degrade to
        // a clean ToolResult.Error the model can act on, not a raw JsonElement.GetProperty
        // KeyNotFoundException with no actionable message.
        var tool = new ReadFileTool();

        var result = await tool.InvokeAsync(Args(new { }), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("A 'path' argument is required.", result.Text);
    }

    [Fact]
    public async Task InvokeAsync_EmptyFile_ReturnsEmptyOkResult()
    {
        var path = Path.Combine(_tempDir, "empty.txt");
        await File.WriteAllTextAsync(path, "");
        var tool = new ReadFileTool();

        var result = await tool.InvokeAsync(Args(new { path }), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("", result.Text);
    }

    [Fact]
    public async Task InvokeAsync_MultipleLines_NumbersEachLine()
    {
        var path = Path.Combine(_tempDir, "file.txt");
        await File.WriteAllTextAsync(path, "alpha\nbeta\ngamma");
        var tool = new ReadFileTool();

        var result = await tool.InvokeAsync(Args(new { path }), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("1\talpha\n2\tbeta\n3\tgamma", result.Text);
    }

    [Fact]
    public async Task InvokeAsync_LargeFile_TruncatesToDefaultLimitWithContinuationHint()
    {
        var path = Path.Combine(_tempDir, "large.txt");
        await File.WriteAllTextAsync(path, string.Join('\n', Enumerable.Range(1, 2500).Select(i => $"line{i}")));
        var tool = new ReadFileTool();

        var result = await tool.InvokeAsync(Args(new { path }), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.StartsWith("1\tline1\n", result.Text);
        Assert.Contains("2000\tline2000", result.Text);
        Assert.DoesNotContain("2001\tline2001", result.Text);
        Assert.EndsWith("[Showing lines 1-2000 of 2500. Use offset=2001 to continue.]", result.Text);
    }

    [Fact]
    public async Task InvokeAsync_WithOffsetAndLimit_ReadsRequestedWindow()
    {
        var path = Path.Combine(_tempDir, "file.txt");
        await File.WriteAllTextAsync(path, string.Join('\n', Enumerable.Range(1, 10).Select(i => $"line{i}")));
        var tool = new ReadFileTool();

        var result = await tool.InvokeAsync(Args(new { path, offset = 4, limit = 2 }), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(
            "4\tline4\n5\tline5\n\n[Showing lines 4-5 of 10. Use offset=6 to continue.]",
            result.Text);
    }

    [Fact]
    public async Task InvokeAsync_OffsetBeyondEndOfFile_ReturnsError()
    {
        var path = Path.Combine(_tempDir, "file.txt");
        await File.WriteAllTextAsync(path, "one\ntwo");
        var tool = new ReadFileTool();

        var result = await tool.InvokeAsync(Args(new { path, offset = 10 }), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("beyond the end of the file", result.Text);
    }

    [Fact]
    public async Task InvokeAsync_VeryLongLine_IsTruncatedPerLine()
    {
        var path = Path.Combine(_tempDir, "file.txt");
        await File.WriteAllTextAsync(path, new string('x', 3000));
        var tool = new ReadFileTool();

        var result = await tool.InvokeAsync(Args(new { path }), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("[line truncated]", result.Text);
        Assert.DoesNotContain(new string('x', 2001), result.Text);
    }

    [Fact]
    public async Task InvokeAsync_InvalidOffset_ReturnsError()
    {
        var path = Path.Combine(_tempDir, "file.txt");
        await File.WriteAllTextAsync(path, "hello");
        var tool = new ReadFileTool();

        var result = await tool.InvokeAsync(Args(new { path, offset = 0 }), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("'offset' must be a positive integer.", result.Text);
    }
}
