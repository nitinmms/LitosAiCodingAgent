using System.Text;
using System.Text.Json;
using Litos.Agent.Tools;

namespace Litos.Tools.FileSystem;

public sealed class ReadFileTool(int? maxOutputBytes = null) : ITool
{
    private const int DefaultMaxLines = 2000;
    private const int MaxLineLength = 2000;

    private readonly int _maxOutputBytes = maxOutputBytes ?? OutputTruncation.DefaultMaxBytes;

    public string Name => "read_file";

    public string Description =>
        "Read the contents of a text file at the given path, formatted with line numbers " +
        "(like 'cat -n'). Output is truncated at 2000 lines or 50KB, whichever comes first. " +
        "For larger files, use 'offset' and 'limit' to page through the rest.";

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
        var path = arguments.GetStringOrNull("path");
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

        // Byte-capped after line numbering so the limit binds against what actually reaches the
        // transcript. The line limit is already enforced above via `limit`/endLine, so only the
        // byte limit can bind here — hence maxLines: int.MaxValue rather than DefaultMaxLines,
        // which would otherwise re-cut an already-correct window and misreport the cause.
        var truncation = OutputTruncation.Truncate(
            output.ToString(), RetainEnd.Head, _maxOutputBytes, maxLines: int.MaxValue);

        // Where paging resumes: the byte cap can stop short of the line window the caller asked
        // for, so continue from what was actually kept, not from endLine.
        var lastLineShown = startLine + truncation.OutputLines;
        var body = truncation.Text;

        if (truncation.Truncated)
            body += $"\n\n[Truncated: showing lines {startLine + 1}-{lastLineShown} of {lines.Length} ({_maxOutputBytes / 1024}KB limit). Use offset={lastLineShown + 1} to continue.]";
        else if (endLine < lines.Length)
            body += $"\n\n[Showing lines {startLine + 1}-{endLine} of {lines.Length}. Use offset={endLine + 1} to continue.]";

        return ToolResult.Ok(body.TrimEnd('\n'));
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
