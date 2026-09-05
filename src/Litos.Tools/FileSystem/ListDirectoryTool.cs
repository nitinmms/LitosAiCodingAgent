using System.Text.Json;
using Litos.Agent.Tools;

namespace Litos.Tools.FileSystem;

public sealed class ListDirectoryTool : ITool
{
    public string Name => "list_directory";

    public string Description => "List files and subdirectories at the given directory path.";

    public JsonElement ParameterSchema { get; } = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new { path = new { type = "string", description = "Directory path to list." } },
        required = new[] { "path" },
    });

    public Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken ct)
    {
        var path = arguments.GetStringOrNull("path");
        if (string.IsNullOrWhiteSpace(path))
            return Task.FromResult(ToolResult.Error("A 'path' argument is required."));

        if (!Directory.Exists(path))
            return Task.FromResult(ToolResult.Error($"Directory not found: {path}"));

        var entries = Directory.EnumerateFileSystemEntries(path)
            .Select(entry => Directory.Exists(entry) ? $"{Path.GetFileName(entry)}/" : Path.GetFileName(entry))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase);

        return Task.FromResult(ToolResult.Ok(string.Join('\n', entries)));
    }
}
