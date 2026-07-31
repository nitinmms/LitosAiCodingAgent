using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Litos.Agent.Tools;

namespace Litos.Tools.FileSystem;

public sealed class GrepTool : ITool
{
    private const int DefaultMaxMatches = 50;
    private const int HardMaxMatches = 200;

    public string Name => "search_code";

    public string Description =>
        "Search file contents for a regular expression across a directory tree. " +
        "Returns matching file:line locations with a short snippet, token-budgeted " +
        "and truncated with guidance if there are more matches than fit in one result. " +
        "Prefer this over reading files one by one to locate where something is defined or used.";

    public JsonElement ParameterSchema { get; } = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            pattern = new { type = "string", description = ".NET regular expression to search for." },
            path = new { type = "string", description = "Directory to search under. Defaults to the current working directory." },
            glob = new { type = "string", description = "Optional glob (e.g. '*.cs') restricting which files are searched." },
            case_sensitive = new { type = "boolean", description = "Defaults to false." },
            context_lines = new { type = "integer", description = "Lines of context before/after each match. Defaults to 0." },
            max_matches = new { type = "integer", description = "Cap on returned matches. Defaults to 50, capped at 200." },
        },
        required = new[] { "pattern" },
    });

    public Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken ct)
    {
        var pattern = arguments.TryGetProperty("pattern", out var patternProp) ? patternProp.GetString() : null;
        if (string.IsNullOrWhiteSpace(pattern))
            return Task.FromResult(ToolResult.Error("A 'pattern' argument is required."));

        var path = arguments.TryGetProperty("path", out var pathProp) ? pathProp.GetString() : null;
        path = string.IsNullOrWhiteSpace(path) ? Directory.GetCurrentDirectory() : path;
        if (!Directory.Exists(path))
            return Task.FromResult(ToolResult.Error($"Directory not found: {path}"));

        var glob = arguments.TryGetProperty("glob", out var globProp) ? globProp.GetString() : null;

        if (!TryGetBoolean(arguments, "case_sensitive", out var caseSensitive, out var caseSensitiveError))
            return Task.FromResult(ToolResult.Error(caseSensitiveError));
        if (!TryGetInt(arguments, "context_lines", 0, out var contextLines, out var contextLinesError))
            return Task.FromResult(ToolResult.Error(contextLinesError));
        if (!TryGetInt(arguments, "max_matches", DefaultMaxMatches, out var maxMatches, out var maxMatchesError))
            return Task.FromResult(ToolResult.Error(maxMatchesError));
        maxMatches = Math.Clamp(maxMatches <= 0 ? DefaultMaxMatches : maxMatches, 1, HardMaxMatches);

        var regexOptions = RegexOptions.Compiled;
        if (!caseSensitive)
            regexOptions |= RegexOptions.IgnoreCase;

        Regex regex;
        try
        {
            regex = new Regex(pattern, regexOptions);
        }
        catch (RegexParseException ex)
        {
            return Task.FromResult(ToolResult.Error($"Invalid regex pattern '{pattern}': {ex.Message}"));
        }

        var result = Search(path, glob, regex, contextLines, maxMatches);
        return Task.FromResult(ToolResult.Ok(result));
    }

    private static bool TryGetBoolean(JsonElement arguments, string propertyName, out bool value, out string error)
    {
        value = false;
        error = "";
        if (!arguments.TryGetProperty(propertyName, out var prop))
            return true;
        if (prop.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            error = $"'{propertyName}' must be a boolean.";
            return false;
        }
        value = prop.GetBoolean();
        return true;
    }

    private static bool TryGetInt(JsonElement arguments, string propertyName, int defaultValue, out int value, out string error)
    {
        value = defaultValue;
        error = "";
        if (!arguments.TryGetProperty(propertyName, out var prop))
            return true;
        if (prop.ValueKind != JsonValueKind.Number || !prop.TryGetInt32(out value))
        {
            value = defaultValue;
            error = $"'{propertyName}' must be an integer.";
            return false;
        }
        return true;
    }

    private static string Search(string rootPath, string? glob, Regex regex, int contextLines, int maxMatches)
    {
        var ignoreFilter = IgnoreFilter.ForDirectory(rootPath);
        var globRegex = BuildGlobRegex(glob);
        var output = new StringBuilder();
        var matchCount = 0;
        var truncated = false;

        foreach (var filePath in EnumerateFiles(rootPath, globRegex, ignoreFilter))
        {
            if (matchCount >= maxMatches)
            {
                truncated = true;
                break;
            }

            if (IsBinaryFile(filePath))
                continue;

            string[] lines;
            try
            {
                lines = File.ReadAllLines(filePath);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(rootPath, filePath).Replace('\\', '/');

            for (var i = 0; i < lines.Length; i++)
            {
                if (!regex.IsMatch(lines[i]))
                    continue;

                if (matchCount >= maxMatches)
                {
                    // This file has at least one more match than the cap allows for —
                    // confirmed without a second full-tree rescan.
                    truncated = true;
                    break;
                }

                matchCount++;
                AppendMatch(output, relativePath, lines, i, contextLines);
            }
        }

        if (matchCount == 0)
            return "No matches found.";

        if (truncated)
            output.Append($"\n[Truncated: showing {matchCount} of {matchCount}+ matches. Narrow with `glob`, `path`, or a more specific `pattern`.]");

        return output.ToString().TrimStart('\n');
    }

    private static void AppendMatch(StringBuilder output, string relativePath, string[] lines, int matchLineIndex, int contextLines)
    {
        var start = Math.Max(0, matchLineIndex - contextLines);
        var end = Math.Min(lines.Length - 1, matchLineIndex + contextLines);
        for (var i = start; i <= end; i++)
            output.Append('\n').Append(relativePath).Append(':').Append(i + 1).Append(':').Append(lines[i].Trim());
    }

    /// <summary>
    /// Builds an exact-extension-aware glob matcher instead of relying on
    /// Directory.EnumerateFiles' search pattern, whose legacy 8.3 short-name
    /// fallback can make "*.cs" also match files like "foo.csx".
    /// </summary>
    private static Regex? BuildGlobRegex(string? glob)
    {
        if (string.IsNullOrWhiteSpace(glob))
            return null;
        var pattern = "^" + Regex.Escape(glob).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    private static IEnumerable<string> EnumerateFiles(string rootPath, Regex? globRegex, IgnoreFilter ignoreFilter) =>
        EnumerateFilesRecursive(rootPath, globRegex, ignoreFilter);

    private static IEnumerable<string> EnumerateFilesRecursive(string currentPath, Regex? globRegex, IgnoreFilter ignoreFilter)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(currentPath, "*", SearchOption.TopDirectoryOnly);
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file);
            if (ignoreFilter.IsFileIgnored(fileName))
                continue;
            if (globRegex is not null && !globRegex.IsMatch(fileName))
                continue;
            yield return file;
        }

        IEnumerable<string> subdirs;
        try
        {
            subdirs = Directory.EnumerateDirectories(currentPath);
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var dir in subdirs)
        {
            if (ignoreFilter.IsDirectoryIgnored(Path.GetFileName(dir)))
                continue;

            foreach (var file in EnumerateFilesRecursive(dir, globRegex, ignoreFilter))
                yield return file;
        }
    }

    /// <summary>NUL byte in the first 8 KB — the same heuristic git itself uses.</summary>
    private static bool IsBinaryFile(string filePath)
    {
        const int sampleSize = 8192;
        Span<byte> buffer = stackalloc byte[sampleSize];

        try
        {
            using var stream = File.OpenRead(filePath);
            var read = stream.Read(buffer);
            return buffer[..read].Contains((byte)0);
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }
}
