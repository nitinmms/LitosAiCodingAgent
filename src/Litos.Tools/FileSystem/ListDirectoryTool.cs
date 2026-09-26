using System.Text.Json;
using Litos.Agent.Tools;

namespace Litos.Tools.FileSystem;

public sealed class ListDirectoryTool : ITool
{
    public string Name => "list_directory";

    /// <summary>
    /// Returned instead of an empty string when the directory holds nothing. An empty result was
    /// indistinguishable from a silently failed call, and that ambiguity cost real round trips in a
    /// Programmatic Tool Calling session: having listed an empty directory, the model re-printed the
    /// variable to check it wasn't null, then fell back to shell("pwd; ls -la") purely to establish
    /// that the directory really was empty — two model turns to learn what this sentence says
    /// outright. Sibling tools already state the nothing-found case explicitly (GrepTool's
    /// "No matches found.", WebSearchTool's "No results found.").
    /// </summary>
    public const string EmptyDirectoryMessage = "Directory is empty.";

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

        var listing = string.Join('\n', entries);
        return Task.FromResult(ToolResult.Ok(listing.Length == 0 ? EmptyDirectoryMessage : listing));
    }
}
