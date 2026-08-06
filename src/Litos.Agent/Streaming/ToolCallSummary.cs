using System.Text.Json;
using System.Text.RegularExpressions;
using Litos.Agent.Tools;

namespace Litos.Agent.Streaming;

/// <summary>
/// Pure "verb + target" / outcome formatting for tool calls, shared by every face so each
/// doesn't grow its own JsonElement-property-by-tool-name switch. Returns plain, unstyled
/// strings only — glyph/color selection for running/success/error states is a face concern.
/// </summary>
public static partial class ToolCallSummary
{
    /// <summary>e.g. "Read src/Foo.cs", "$ npm test", "Search "TODO" in src/**/*.cs".</summary>
    public static string DescribeCall(string toolName, JsonElement arguments) => toolName switch
    {
        "read_file" => DescribeReadFile(arguments),
        "write_file" => $"Write {Str(arguments, "path")}",
        "edit_file" => $"Edit {Str(arguments, "path")}",
        "list_directory" => $"List {Str(arguments, "path")}",
        "search_code" => DescribeSearch(arguments),
        "shell" => $"$ {Str(arguments, "command")}",
        "skill" => $"Skill {Str(arguments, "name")}",
        "web_search" => $"Search web \"{Str(arguments, "query")}\"",
        "send_file" => $"Send {Str(arguments, "path")}",
        _ => toolName,
    };

    /// <summary>e.g. "120 lines", "+1 -1", "exit 0", "file not found".</summary>
    public static string DescribeResult(string toolName, ToolResult result)
    {
        if (result.IsError)
            return FirstLine(result.Text);

        return toolName switch
        {
            "read_file" => DescribeReadFileResult(result.Text),
            "write_file" or "edit_file" => DiffStatSuffix().Match(result.Text) is { Success: true } m ? m.Groups[1].Value : "done",
            "list_directory" => $"{CountNonEmptyLines(result.Text)} entries",
            "search_code" => DescribeMatchCount(result.Text),
            "shell" => ExitCodeSuffix().Match(result.Text) is { Success: true } m ? $"exit {m.Groups[1].Value}" : "done",
            "skill" => "loaded",
            "web_search" => DescribeWebSearchResultCount(result.Text),
            _ => FirstLine(result.Text),
        };
    }

    private static string Str(JsonElement arguments, string propertyName) =>
        arguments.ValueKind == JsonValueKind.Object
        && arguments.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : "";

    private static int? Int(JsonElement arguments, string propertyName) =>
        arguments.ValueKind == JsonValueKind.Object
        && arguments.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number)
            ? number
            : null;

    private static string DescribeReadFile(JsonElement arguments)
    {
        var path = Str(arguments, "path");
        var offset = Int(arguments, "offset");
        var limit = Int(arguments, "limit");
        if (offset is null && limit is null)
            return $"Read {path}";

        var start = offset ?? 1;
        var end = limit is int l ? start + l - 1 : (int?)null;
        return end is int e ? $"Read {path}:{start}-{e}" : $"Read {path}:{start}+";
    }

    private static string DescribeSearch(JsonElement arguments)
    {
        var pattern = Str(arguments, "pattern");
        var glob = Str(arguments, "glob");
        var path = Str(arguments, "path");
        var where = glob.Length > 0 ? glob : path;
        return where.Length > 0 ? $"Search \"{pattern}\" in {where}" : $"Search \"{pattern}\"";
    }

    private static string DescribeMatchCount(string text)
    {
        if (text.StartsWith("No matches", StringComparison.Ordinal))
            return "0 matches";

        // GrepTool's truncation note ("[Truncated: showing N of N+ matches...]") already
        // states the exact count; counting lines would otherwise also count that note itself.
        var truncated = TruncatedMatchCount().Match(text);
        return truncated.Success ? $"{truncated.Groups[1].Value}+ matches" : $"{CountNonEmptyLines(text)} matches";
    }

    private static string DescribeReadFileResult(string text)
    {
        if (text.Length == 0)
            return "0 lines";

        // ReadFileTool's continuation hint ("[Showing lines X-Y of Z. Use offset=N to continue.]")
        // already states the shown range and the file's exact total line count; counting lines
        // here would otherwise also count the blank separator and hint line themselves as if they
        // were file content.
        var truncated = ReadTruncationRange().Match(text);
        if (!truncated.Success)
            return $"{CountLines(text)} lines";

        var shown = int.Parse(truncated.Groups[2].Value) - int.Parse(truncated.Groups[1].Value) + 1;
        return $"{shown} of {truncated.Groups[3].Value} lines";
    }

    private static string DescribeWebSearchResultCount(string text)
    {
        if (text.StartsWith("No results", StringComparison.Ordinal))
            return "0 results";

        var count = text.Split('\n').Count(line => line.Contains(" — ", StringComparison.Ordinal));
        return $"{count} results";
    }

    private static int CountLines(string text) =>
        text.Length == 0 ? 0 : text.Split('\n').Length;

    private static int CountNonEmptyLines(string text) =>
        text.Length == 0 ? 0 : text.Split('\n').Count(line => line.Trim().Length > 0);

    private static string FirstLine(string text)
    {
        var index = text.IndexOf('\n');
        return index < 0 ? text : text[..index];
    }

    [GeneratedRegex(@"\[(\+\d+ -\d+)\]")]
    private static partial Regex DiffStatSuffix();

    [GeneratedRegex(@"^\[exit (\d+)\]")]
    private static partial Regex ExitCodeSuffix();

    [GeneratedRegex(@"showing (\d+) of \d+\+ matches")]
    private static partial Regex TruncatedMatchCount();

    [GeneratedRegex(@"\[Showing lines (\d+)-(\d+) of (\d+)\.")]
    private static partial Regex ReadTruncationRange();
}
