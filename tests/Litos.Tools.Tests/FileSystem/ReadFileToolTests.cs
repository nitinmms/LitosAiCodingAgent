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
        Assert.Equal("hello world", result.Text);
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
    public async Task InvokeAsync_MissingPathProperty_Throws()
    {
        var tool = new ReadFileTool();

        await Assert.ThrowsAsync<KeyNotFoundException>(() => tool.InvokeAsync(Args(new { }), CancellationToken.None));
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
}
