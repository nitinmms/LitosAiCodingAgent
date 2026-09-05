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
        var path = arguments.GetStringOrNull("path");
        var oldText = arguments.GetStringOrNull("old_text");
        var newText = arguments.GetStringOrNull("new_text");
        if (string.IsNullOrWhiteSpace(path) || oldText is null || newText is null)
            return ToolResult.Error("'path', 'old_text', and 'new_text' arguments are required.");

        if (!File.Exists(path))
            return ToolResult.Error($"File not found: {path}");

        var original = await File.ReadAllTextAsync(path, ct);
        var firstIndex = original.IndexOf(oldText, StringComparison.Ordinal);
        var matchLength = oldText.Length;
        var usedNormalizedMatch = false;

        if (firstIndex < 0)
        {
            var normalizedMatch = TryFindNormalizedMatch(original, oldText);
            if (normalizedMatch is null)
                return ToolResult.Error(
                    "'old_text' was not found in the file. Re-read the file and copy the anchor text " +
                    "exactly, including indentation; a mismatch is often due to a paraphrased anchor " +
                    "or a whitespace/line-ending difference that isn't visible in the read output.");

            (firstIndex, matchLength) = normalizedMatch.Value;
            usedNormalizedMatch = true;
        }

        var hasDuplicate = usedNormalizedMatch
            ? CountNormalizedMatches(original, oldText) > 1
            : original.IndexOf(oldText, firstIndex + 1, StringComparison.Ordinal) >= 0;
        if (hasDuplicate)
            return ToolResult.Error("'old_text' matches more than once in the file; make it more specific.");

        var updated = string.Concat(original.AsSpan(0, firstIndex), newText, original.AsSpan(firstIndex + matchLength));
        var diff = UnifiedDiff.Render(original, updated, path);

        var decision = await approvalGate.RequestAsync(
            new ToolInvocationPreview(Name, $"Edit {path}", diff), ct);

        if (decision == ApprovalDecision.Deny)
            return ToolResult.Error("User denied this file edit.");

        await File.WriteAllTextAsync(path, updated, ct);
        var (added, removed) = LineDelta.Count(oldText, newText);
        return ToolResult.Ok($"Edited {path}. [+{added} -{removed}]");
    }

    /// <summary>
    /// Falls back to a line-ending-insensitive search: normalizes CRLF to LF on both sides,
    /// finds the match in normalized space, then maps that span back onto the original string
    /// (which may use CRLF) so the replacement lands at the right offsets and the file's
    /// existing line-ending convention is preserved.
    /// </summary>
    private static (int Index, int Length)? TryFindNormalizedMatch(string original, string oldText)
    {
        if (!oldText.Contains('\r') && !original.Contains('\r'))
            return null;

        var normalizedOriginal = original.Replace("\r\n", "\n");
        var normalizedOldText = oldText.Replace("\r\n", "\n");
        var normalizedIndex = normalizedOriginal.IndexOf(normalizedOldText, StringComparison.Ordinal);
        if (normalizedIndex < 0)
            return null;

        var originalIndex = MapNormalizedOffsetToOriginal(original, normalizedIndex);
        var originalEnd = MapNormalizedOffsetToOriginal(original, normalizedIndex + normalizedOldText.Length);
        return (originalIndex, originalEnd - originalIndex);
    }

    private static int CountNormalizedMatches(string original, string oldText)
    {
        var normalizedOriginal = original.Replace("\r\n", "\n");
        var normalizedOldText = oldText.Replace("\r\n", "\n");
        if (normalizedOldText.Length == 0)
            return 0;

        var count = 0;
        var searchFrom = 0;
        int index;
        while ((index = normalizedOriginal.IndexOf(normalizedOldText, searchFrom, StringComparison.Ordinal)) >= 0)
        {
            count++;
            searchFrom = index + 1;
        }

        return count;
    }

    private static int MapNormalizedOffsetToOriginal(string original, int normalizedOffset)
    {
        var seen = 0;
        for (var i = 0; i < original.Length; i++)
        {
            if (seen == normalizedOffset)
                return i;

            if (original[i] == '\r' && i + 1 < original.Length && original[i + 1] == '\n')
                continue;

            seen++;
        }

        return original.Length;
    }
}
