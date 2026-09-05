using System.Text.Json;
using Litos.Tools.FileSystem;
using Litos.Tools.Shell;
using Litos.Tools.Tests.Fakes;

namespace Litos.Tools.Tests.FileSystem;

public class WriteFileToolTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("litos-writefile-").FullName;

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private static JsonElement Args(object obj) => JsonSerializer.SerializeToElement(obj);

    [Fact]
    public async Task InvokeAsync_Approved_WritesFile_AndReturnsCharacterCount()
    {
        var gate = new FakeApprovalGate { Decision = ApprovalDecision.Approve };
        var tool = new WriteFileTool(gate);
        var path = Path.Combine(_tempDir, "new.txt");

        var result = await tool.InvokeAsync(Args(new { path, content = "hello" }), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("Wrote 5 characters to " + path + ". [+1 -0]", result.Text);
        Assert.Equal("hello", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task InvokeAsync_MissingRequiredProperties_ReturnsError()
    {
        // A local model's tool-call JSON omitting a required argument is a real, model-driven
        // failure mode — must degrade to a clean ToolResult.Error, not throw.
        var gate = new FakeApprovalGate { Decision = ApprovalDecision.Approve };
        var tool = new WriteFileTool(gate);

        var result = await tool.InvokeAsync(Args(new { }), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("Both 'path' and 'content' arguments are required.", result.Text);
    }

    [Fact]
    public async Task InvokeAsync_Denied_DoesNotCreateFile()
    {
        var gate = new FakeApprovalGate { Decision = ApprovalDecision.Deny };
        var tool = new WriteFileTool(gate);
        var path = Path.Combine(_tempDir, "denied.txt");

        var result = await tool.InvokeAsync(Args(new { path, content = "hello" }), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("User denied this file write.", result.Text);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task InvokeAsync_ApproveAlways_AlsoWritesFile()
    {
        var gate = new FakeApprovalGate { Decision = ApprovalDecision.ApproveAlways };
        var tool = new WriteFileTool(gate);
        var path = Path.Combine(_tempDir, "always.txt");

        var result = await tool.InvokeAsync(Args(new { path, content = "x" }), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task InvokeAsync_CreatesParentDirectories_WhenTheyDoNotExist()
    {
        var gate = new FakeApprovalGate { Decision = ApprovalDecision.Approve };
        var tool = new WriteFileTool(gate);
        var path = Path.Combine(_tempDir, "nested", "deeper", "file.txt");

        await tool.InvokeAsync(Args(new { path, content = "x" }), CancellationToken.None);

        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task InvokeAsync_EmptyStringContent_IsAllowed_NotTreatedAsMissing()
    {
        var gate = new FakeApprovalGate { Decision = ApprovalDecision.Approve };
        var tool = new WriteFileTool(gate);
        var path = Path.Combine(_tempDir, "empty.txt");

        var result = await tool.InvokeAsync(Args(new { path, content = "" }), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task InvokeAsync_WhitespacePath_ReturnsError_WithoutCallingGate()
    {
        var gate = new FakeApprovalGate { Decision = ApprovalDecision.Approve };
        var tool = new WriteFileTool(gate);

        var result = await tool.InvokeAsync(Args(new { path = "  ", content = "x" }), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal(0, gate.CallCount);
    }

    [Fact]
    public async Task InvokeAsync_GateReceivesDiffPreview_ContainingNewContent()
    {
        var gate = new FakeApprovalGate { Decision = ApprovalDecision.Approve };
        var tool = new WriteFileTool(gate);
        var path = Path.Combine(_tempDir, "preview.txt");

        await tool.InvokeAsync(Args(new { path, content = "new stuff" }), CancellationToken.None);

        Assert.NotNull(gate.LastPreview);
        Assert.Contains("+new stuff", gate.LastPreview!.DiffOrCommand);
    }

    [Fact]
    public async Task InvokeAsync_OverwritingExistingFile_DiffShowsOldContentRemoved()
    {
        var path = Path.Combine(_tempDir, "existing.txt");
        await File.WriteAllTextAsync(path, "old content");
        var gate = new FakeApprovalGate { Decision = ApprovalDecision.Approve };
        var tool = new WriteFileTool(gate);

        await tool.InvokeAsync(Args(new { path, content = "new content" }), CancellationToken.None);

        Assert.Contains("-old content", gate.LastPreview!.DiffOrCommand);
        Assert.Equal("new content", await File.ReadAllTextAsync(path));
    }
}
