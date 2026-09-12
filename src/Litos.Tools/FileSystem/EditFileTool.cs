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
                return ToolResult.Error(DescribeAnchorMismatch(original, oldText, path));

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
    /// Builds the not-found error, quoting the file text that most resembles the anchor the model
    /// supplied. The bare "re-read the file and copy the anchor exactly" message this replaced was
    /// observed to not work: in a real local-model session (Gemma 4 27B) the same anchor was
    /// submitted three times and rejected three times, with a full re-read of the file between each
    /// attempt — re-reading doesn't help when the text being guessed at isn't in the file at all,
    /// because nothing in the message tells the model how its guess differs from what's there.
    /// Showing the closest real lines turns "try again" into "here is what that region looks like",
    /// which is actionable without another read round-trip.
    /// </summary>
    private static string DescribeAnchorMismatch(string original, string oldText, string path)
    {
        const string guidance =
            "'old_text' was not found in the file. Copy the anchor exactly as it appears in the file, " +
            "including indentation — do not retype it from memory.";

        var anchorFirstLine = FirstNonBlankLine(oldText);
        if (anchorFirstLine is null)
            return guidance;

        var originalLines = original.Replace("\r\n", "\n").Split('\n');
        var best = -1;
        var bestScore = 0.0;
        for (var i = 0; i < originalLines.Length; i++)
        {
            var score = SimilarityScore(anchorFirstLine, originalLines[i]);
            if (score > bestScore)
                (best, bestScore) = (i, score);
        }

        // Below this the "closest" line is noise (a stray brace, a blank-ish line) and quoting it
        // would mislead more than help — fall back to the plain guidance.
        if (best < 0 || bestScore < 0.4)
            return guidance;

        var from = Math.Max(0, best - 2);
        var to = Math.Min(originalLines.Length - 1, best + 6);
        var excerpt = string.Join('\n', Enumerable.Range(from, to - from + 1)
            .Select(i => $"{i + 1}\t{originalLines[i]}"));

        return $"{guidance}\n\nThe closest text in {path} is at line {best + 1}:\n\n{excerpt}";
    }

    private static string? FirstNonBlankLine(string text) =>
        text.Replace("\r\n", "\n").Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim();

    /// <summary>
    /// Cheap token-overlap ratio (Jaccard over whitespace-split tokens), deliberately not an edit
    /// distance: the aim is only to locate roughly the right region to quote back, and a paraphrased
    /// anchor typically shares most of its identifiers with the real line while differing in
    /// punctuation and spacing — which token overlap handles well and character distance does not.
    /// </summary>
    private static double SimilarityScore(string a, string b)
    {
        var tokensA = a.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var tokensB = b.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        if (tokensA.Count == 0 || tokensB.Count == 0)
            return 0;

        var intersection = tokensA.Intersect(tokensB, StringComparer.Ordinal).Count();
        return (double)intersection / Math.Max(tokensA.Count, tokensB.Count);
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
