using System.Text.Json;
using Litos.Tools.FileSystem;

namespace Litos.Tools.Tests.FileSystem;

public class ListDirectoryToolTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("litos-listdir-").FullName;

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private static JsonElement Args(object obj) => JsonSerializer.SerializeToElement(obj);

    [Fact]
    public async Task InvokeAsync_ListsFilesAndDirectories_DirectoriesGetTrailingSlash()
    {
        File.WriteAllText(Path.Combine(_tempDir, "file.txt"), "");
        Directory.CreateDirectory(Path.Combine(_tempDir, "subdir"));
        var tool = new ListDirectoryTool();

        var result = await tool.InvokeAsync(Args(new { path = _tempDir }), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("file.txt", result.Text);
        Assert.Contains("subdir/", result.Text);
    }

    [Fact]
    public async Task InvokeAsync_MissingDirectory_ReturnsError()
    {
        var tool = new ListDirectoryTool();

        var result = await tool.InvokeAsync(Args(new { path = Path.Combine(_tempDir, "nope") }), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("Directory not found", result.Text);
    }

    // Deliberately NOT an empty string: that was indistinguishable from a silently failed call and
    // cost two extra model round trips in a real PTC session (see EmptyDirectoryMessage's own remarks).
    [Fact]
    public async Task InvokeAsync_EmptyDirectory_SaysSoExplicitly_RatherThanReturningEmptyText()
    {
        var tool = new ListDirectoryTool();

        var result = await tool.InvokeAsync(Args(new { path = _tempDir }), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(ListDirectoryTool.EmptyDirectoryMessage, result.Text);
        Assert.NotEqual("", result.Text);
    }

    [Fact]
    public async Task InvokeAsync_WhitespacePath_ReturnsError()
    {
        var tool = new ListDirectoryTool();

        var result = await tool.InvokeAsync(Args(new { path = "   " }), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("A 'path' argument is required.", result.Text);
    }

    [Fact]
    public async Task InvokeAsync_MissingPathProperty_ReturnsError()
    {
        // A local model's tool-call JSON omitting a required argument is a real, model-driven
        // failure mode — must degrade to a clean ToolResult.Error, not throw.
        var tool = new ListDirectoryTool();

        var result = await tool.InvokeAsync(Args(new { }), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("A 'path' argument is required.", result.Text);
    }

    [Fact]
    public async Task InvokeAsync_EntriesAreJoinedByNewlineOnly_NotEnvironmentNewLine()
    {
        File.WriteAllText(Path.Combine(_tempDir, "a.txt"), "");
        File.WriteAllText(Path.Combine(_tempDir, "b.txt"), "");
        var tool = new ListDirectoryTool();

        var result = await tool.InvokeAsync(Args(new { path = _tempDir }), CancellationToken.None);

        Assert.Equal("a.txt\nb.txt", result.Text);
    }

    [Fact]
    public async Task InvokeAsync_SortsCaseInsensitively()
    {
        File.WriteAllText(Path.Combine(_tempDir, "Zebra.txt"), "");
        File.WriteAllText(Path.Combine(_tempDir, "apple.txt"), "");
        var tool = new ListDirectoryTool();

        var result = await tool.InvokeAsync(Args(new { path = _tempDir }), CancellationToken.None);

        Assert.Equal("apple.txt\nZebra.txt", result.Text);
    }
}
