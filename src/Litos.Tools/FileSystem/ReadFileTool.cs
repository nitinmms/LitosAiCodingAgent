using System.Text;
using System.Text.Json;
using Litos.Agent.Tools;

namespace Litos.Tools.FileSystem;

public sealed class ReadFileTool : ITool
{
    private const int DefaultMaxLines = 2000;
    private const int MaxLineLength = 2000;

    public string Name => "read_file";

    public string Description =>
        "Read the contents of a text file at the given path, formatted with line numbers " +
        "(like 'cat -n'). Reads up to 2000 lines by default, starting at the top of the file. " +
        "For larger files, use 'offset' and 'limit' to page through the rest. " +
        "The line-number prefixes are for display only — do NOT pass this output back into " +
        "write_file or otherwise write it to disk verbatim, since the prefixes ('123\\t') are not " +
        "part of the file's real content and will corrupt it. Use edit_file for targeted changes, " +
        "or re-read the file's raw bytes yourself if you need an unprefixed copy.";

    public JsonElement ParameterSchema { get; } = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            path = new { type = "string", description = "Path to the file to read." },
            offset = new { type = "integer", description = "1-indexed line number to start reading from. Defaults to 1." },
            limit = new { type = "integer", description = "Maximum number of lines to read. Defaults to 2000." },
        },
        required = new[] { "path" },
    });

    public async Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken ct)
    {
        var path = arguments.GetProperty("path").GetString();
        if (string.IsNullOrWhiteSpace(path))
            return ToolResult.Error("A 'path' argument is required.");

        if (!File.Exists(path))
            return ToolResult.Error($"File not found: {path}");

        if (!TryGetPositiveInt(arguments, "offset", 1, out var offset, out var offsetError))
            return ToolResult.Error(offsetError);
        if (!TryGetPositiveInt(arguments, "limit", DefaultMaxLines, out var limit, out var limitError))
            return ToolResult.Error(limitError);

        var lines = await File.ReadAllLinesAsync(path, ct);
        if (lines.Length == 0)
            return ToolResult.Ok("");

        var startLine = Math.Min(offset - 1, lines.Length);
        if (startLine >= lines.Length)
            return ToolResult.Error($"Offset {offset} is beyond the end of the file ({lines.Length} lines).");

        var endLine = Math.Min(startLine + limit, lines.Length);

        var output = new StringBuilder();
        for (var i = startLine; i < endLine; i++)
        {
            var line = lines[i];
            if (line.Length > MaxLineLength)
                line = line[..MaxLineLength] + "… [line truncated]";
            output.Append(i + 1).Append('\t').Append(line).Append('\n');
        }

        if (endLine < lines.Length)
            output.Append($"\n[Showing lines {startLine + 1}-{endLine} of {lines.Length}. Use offset={endLine + 1} to continue.]");

        return ToolResult.Ok(output.ToString().TrimEnd('\n'));
    }

    private static bool TryGetPositiveInt(JsonElement arguments, string propertyName, int defaultValue, out int value, out string error)
    {
        value = defaultValue;
        error = "";
        if (!arguments.TryGetProperty(propertyName, out var prop) || prop.ValueKind == JsonValueKind.Null)
            return true;
        if (prop.ValueKind != JsonValueKind.Number || !prop.TryGetInt32(out value) || value < 1)
        {
            value = defaultValue;
            error = $"'{propertyName}' must be a positive integer.";
            return false;
        }
        return true;
    }
}
