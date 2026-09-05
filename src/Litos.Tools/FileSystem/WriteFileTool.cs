using System.Text.Json;
using Litos.Agent.Tools;
using Litos.Tools.Shell;

namespace Litos.Tools.FileSystem;

public sealed class WriteFileTool(IToolApprovalGate approvalGate) : ITool
{
    public string Name => "write_file";

    public string Description => "Create or overwrite a text file at the given path with the given content.";

    public JsonElement ParameterSchema { get; } = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            path = new { type = "string", description = "Path to the file to write." },
            content = new { type = "string", description = "Full content to write to the file." },
        },
        required = new[] { "path", "content" },
    });

    public async Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken ct)
    {
        var path = arguments.GetStringOrNull("path");
        var content = arguments.GetStringOrNull("content");
        if (string.IsNullOrWhiteSpace(path) || content is null)
            return ToolResult.Error("Both 'path' and 'content' arguments are required.");

        var existing = File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : null;
        var diff = UnifiedDiff.Render(existing, content, path);

        var decision = await approvalGate.RequestAsync(
            new ToolInvocationPreview(Name, $"Write to {path}", diff), ct);

        if (decision == ApprovalDecision.Deny)
            return ToolResult.Error("User denied this file write.");

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        await File.WriteAllTextAsync(path, content, ct);
        var (added, removed) = LineDelta.Count(existing, content);
        return ToolResult.Ok($"Wrote {content.Length} characters to {path}. [+{added} -{removed}]");
    }
}
