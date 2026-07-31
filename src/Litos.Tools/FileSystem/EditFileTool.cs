using System.Text.Json;
using Litos.Agent.Tools;
using Litos.Tools.Shell;

namespace Litos.Tools.FileSystem;

public sealed class EditFileTool(IToolApprovalGate approvalGate) : ITool
{
    public string Name => "edit_file";

    public string Description =>
        "Replace an exact, unique block of text (the anchor) in an existing file with new text. " +
        "The anchor must match exactly once in the file, including whitespace.";

    public JsonElement ParameterSchema { get; } = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            path = new { type = "string", description = "Path to the file to edit." },
            old_text = new { type = "string", description = "Exact, unique existing text to find." },
            new_text = new { type = "string", description = "Text to replace it with." },
        },
        required = new[] { "path", "old_text", "new_text" },
    });

    public async Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken ct)
    {
        var path = arguments.GetProperty("path").GetString();
        var oldText = arguments.GetProperty("old_text").GetString();
        var newText = arguments.GetProperty("new_text").GetString();
        if (string.IsNullOrWhiteSpace(path) || oldText is null || newText is null)
            return ToolResult.Error("'path', 'old_text', and 'new_text' arguments are required.");

        if (!File.Exists(path))
            return ToolResult.Error($"File not found: {path}");

        var original = await File.ReadAllTextAsync(path, ct);
        var firstIndex = original.IndexOf(oldText, StringComparison.Ordinal);
        if (firstIndex < 0)
            return ToolResult.Error("'old_text' was not found in the file.");
        if (original.IndexOf(oldText, firstIndex + 1, StringComparison.Ordinal) >= 0)
            return ToolResult.Error("'old_text' matches more than once in the file; make it more specific.");

        var updated = string.Concat(original.AsSpan(0, firstIndex), newText, original.AsSpan(firstIndex + oldText.Length));
        var diff = UnifiedDiff.Render(original, updated, path);

        var decision = await approvalGate.RequestAsync(
            new ToolInvocationPreview(Name, $"Edit {path}", diff), ct);

        if (decision == ApprovalDecision.Deny)
            return ToolResult.Error("User denied this file edit.");

        await File.WriteAllTextAsync(path, updated, ct);
        var (added, removed) = LineDelta.Count(oldText, newText);
        return ToolResult.Ok($"Edited {path}. [+{added} -{removed}]");
    }
}
