using System.Text.Json;
using Litos.Agent.Tools;

namespace Litos.Tools.FileSystem;

public sealed class ReadFileTool : ITool
{
    public string Name => "read_file";

    public string Description => "Read the contents of a text file at the given path.";

    public JsonElement ParameterSchema { get; } = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new { path = new { type = "string", description = "Path to the file to read." } },
        required = new[] { "path" },
    });

    public async Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken ct)
    {
        var path = arguments.GetProperty("path").GetString();
        if (string.IsNullOrWhiteSpace(path))
            return ToolResult.Error("A 'path' argument is required.");

        if (!File.Exists(path))
            return ToolResult.Error($"File not found: {path}");

        var text = await File.ReadAllTextAsync(path, ct);
        return ToolResult.Ok(text);
    }
}
