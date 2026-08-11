using System.Text.Json;
using Litos.Tools.FileSystem;
using Litos.Tools.Shell;
using Litos.Tools.Tests.Fakes;

namespace Litos.Tools.Tests.FileSystem;

public class EditFileToolTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("litos-editfile-").FullName;

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private static JsonElement Args(object obj) => JsonSerializer.SerializeToElement(obj);

    [Fact]
    public async Task InvokeAsync_UniqueMatch_Approved_ReplacesText()
    {
        var path = Path.Combine(_tempDir, "file.cs");
        await File.WriteAllTextAsync(path, "before target after");
        var gate = new FakeApprovalGate { Decision = ApprovalDecision.Approve };
        var tool = new EditFileTool(gate);

        var result = await tool.InvokeAsync(Args(new { path, old_text = "target", new_text = "replaced" }), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("before replaced after", await File.ReadAllTextAsync(path));
        Assert.Equal($"Edited {path}. [+1 -1]", result.Text);
    }

    [Fact]
    public async Task InvokeAsync_MissingFile_ReturnsError()
    {
        var gate = new FakeApprovalGate { Decision = ApprovalDecision.Approve };
        var tool = new EditFileTool(gate);

        var result = await tool.InvokeAsync(
            Args(new { path = Path.Combine(_tempDir, "nope.cs"), old_text = "a", new_text = "b" }),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("File not found", result.Text);
    }

    [Fact]
    public async Task InvokeAsync_AnchorNotFound_ReturnsError()
    {
        var path = Path.Combine(_tempDir, "file.cs");
        await File.WriteAllTextAsync(path, "some content");
        var gate = new FakeApprovalGate { Decision = ApprovalDecision.Approve };
        var tool = new EditFileTool(gate);

        var result = await tool.InvokeAsync(Args(new { path, old_text = "missing", new_text = "x" }), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("'old_text' was not found in the file.", result.Text);
    }

    [Fact]
    public async Task InvokeAsync_AnchorMatchesMultipleTimes_ReturnsError()
    {
        var path = Path.Combine(_tempDir, "file.cs");
        await File.WriteAllTextAsync(path, "foo bar foo");
        var gate = new FakeApprovalGate { Decision = ApprovalDecision.Approve };
        var tool = new EditFileTool(gate);

        var result = await tool.InvokeAsync(Args(new { path, old_text = "foo", new_text = "x" }), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("'old_text' matches more than once in the file; make it more specific.", result.Text);
    }

    [Fact]
    public async Task InvokeAsync_Denied_LeavesFileUnchanged()
    {
        var path = Path.Combine(_tempDir, "file.cs");
        await File.WriteAllTextAsync(path, "original content");
        var gate = new FakeApprovalGate { Decision = ApprovalDecision.Deny };
        var tool = new EditFileTool(gate);

        var result = await tool.InvokeAsync(Args(new { path, old_text = "original", new_text = "changed" }), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("User denied this file edit.", result.Text);
        Assert.Equal("original content", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task InvokeAsync_EmptyAnchor_AgainstNonTrivialFile_ReportsMultipleMatches()
    {
        // "" matches everywhere in a string, so IndexOf("", 0) finds a match immediately and
        // the second IndexOf("", 1) also finds one — the "matches more than once" branch,
        // not a crash, as long as the file has at least 1 character.
        var path = Path.Combine(_tempDir, "file.cs");
        await File.WriteAllTextAsync(path, "x");
        var gate = new FakeApprovalGate { Decision = ApprovalDecision.Approve };
        var tool = new EditFileTool(gate);

        var result = await tool.InvokeAsync(Args(new { path, old_text = "", new_text = "y" }), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("'old_text' matches more than once in the file; make it more specific.", result.Text);
    }

    [Fact]
    public async Task InvokeAsync_EmptyAnchor_AgainstEmptyFile_ThrowsArgumentOutOfRange()
    {
        // Regression test for a real edge case: original="" means firstIndex=0 from the
        // first IndexOf("", 0), but the second IndexOf(oldText, firstIndex + 1) = IndexOf("", 1)
        // is called with startIndex=1 against a zero-length string, which is out of range
        // for string.IndexOf and throws rather than returning "not found". This documents
        // current (buggy) behavior rather than asserting it's correct.
        var path = Path.Combine(_tempDir, "empty.cs");
        await File.WriteAllTextAsync(path, "");
        var gate = new FakeApprovalGate { Decision = ApprovalDecision.Approve };
        var tool = new EditFileTool(gate);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            tool.InvokeAsync(Args(new { path, old_text = "", new_text = "y" }), CancellationToken.None));
    }

    [Fact]
    public async Task InvokeAsync_OrdinalComparison_IsCaseSensitive()
    {
        var path = Path.Combine(_tempDir, "file.cs");
        await File.WriteAllTextAsync(path, "Target");
        var gate = new FakeApprovalGate { Decision = ApprovalDecision.Approve };
        var tool = new EditFileTool(gate);

        var result = await tool.InvokeAsync(Args(new { path, old_text = "target", new_text = "x" }), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("'old_text' was not found in the file.", result.Text);
    }

    [Fact]
    public async Task InvokeAsync_AnchorUsesLfButFileUsesCrlf_FallsBackToNormalizedMatch()
    {
        var path = Path.Combine(_tempDir, "file.cs");
        await File.WriteAllTextAsync(path, "line1\r\nline2\r\nline3");
        var gate = new FakeApprovalGate { Decision = ApprovalDecision.Approve };
        var tool = new EditFileTool(gate);

        var result = await tool.InvokeAsync(
            Args(new { path, old_text = "line1\nline2", new_text = "replaced" }),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("replaced\r\nline3", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task InvokeAsync_AnchorUsesCrlfButFileUsesLf_FallsBackToNormalizedMatch()
    {
        var path = Path.Combine(_tempDir, "file.cs");
        await File.WriteAllTextAsync(path, "line1\nline2\nline3");
        var gate = new FakeApprovalGate { Decision = ApprovalDecision.Approve };
        var tool = new EditFileTool(gate);

        var result = await tool.InvokeAsync(
            Args(new { path, old_text = "line1\r\nline2", new_text = "replaced" }),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("replaced\nline3", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task InvokeAsync_NormalizedAnchorMatchesMultipleTimes_ReturnsError()
    {
        var path = Path.Combine(_tempDir, "file.cs");
        await File.WriteAllTextAsync(path, "foo\r\nbar foo\r\nbar");
        var gate = new FakeApprovalGate { Decision = ApprovalDecision.Approve };
        var tool = new EditFileTool(gate);

        var result = await tool.InvokeAsync(
            Args(new { path, old_text = "foo\nbar", new_text = "x" }),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("'old_text' matches more than once in the file; make it more specific.", result.Text);
    }

    [Fact]
    public async Task InvokeAsync_AnchorTrulyMissing_LineEndingFallbackDoesNotMaskError()
    {
        var path = Path.Combine(_tempDir, "file.cs");
        await File.WriteAllTextAsync(path, "line1\r\nline2\r\nline3");
        var gate = new FakeApprovalGate { Decision = ApprovalDecision.Approve };
        var tool = new EditFileTool(gate);

        var result = await tool.InvokeAsync(
            Args(new { path, old_text = "totally\nmissing", new_text = "x" }),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("'old_text' was not found in the file.", result.Text);
    }
}
