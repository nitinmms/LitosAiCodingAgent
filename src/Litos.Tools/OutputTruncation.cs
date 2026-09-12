using System.Text;

namespace Litos.Tools;

/// <summary>Which end of the content to keep when it exceeds a limit.</summary>
public enum RetainEnd
{
    /// <summary>Keep the beginning, drop the end — for file reads, where the top orients the
    /// reader (imports, declarations) and paging continues forward from where the cut landed.</summary>
    Head,

    /// <summary>Keep the end, drop the beginning — for command output, where the payload is at the
    /// bottom (compiler errors, test summaries, stack traces) and the head is progress noise. Head-
    /// retaining a failed build keeps "Determining projects to restore..." and discards the error
    /// the command was run to find, which reads as success to a model that trusts what it's given.</summary>
    Tail,
}

/// <param name="Text">The kept content, without any truncation marker appended.</param>
/// <param name="Truncated">True if anything was dropped.</param>
/// <param name="OutputLines">Number of lines kept.</param>
/// <param name="TotalLines">Number of lines before truncation.</param>
/// <param name="TruncatedByBytes">
/// True when the byte limit was the binding constraint, false when the line limit was. Callers use
/// this to explain the cause rather than leaving the model to guess why it was cut off.
/// </param>
public sealed record TruncationResult(string Text, bool Truncated, int OutputLines, int TotalLines, bool TruncatedByBytes);

/// <summary>
/// Shared line+byte truncation for tool output, mirroring pi's truncate.ts: two independent limits,
/// whichever is hit first wins, and a line is never split across the boundary.
///
/// The byte limit is the part Litos was missing. ReadFileTool capped lines only, which does not
/// bound tokens at all — a 2000-line file of long lines (minified source, a lockfile, a wide CSV)
/// passes the line check and still lands hundreds of KB in the transcript. Measured in this repo:
/// src/Litos.Gui/MainWindow.axaml.cs is 102KB and well under 2000 lines, so a single read of it
/// exceeded the entire context window of a small local model (~22K tokens ≈ 88KB), which the
/// provider then rejects outright. ShellTool had neither limit.
///
/// 50KB matches pi's DEFAULT_MAX_BYTES. Measured against this repo's 334 .cs files it truncates
/// exactly one (the 102KB outlier above) — p95 is ~10KB — so it catches the case that actually
/// wedges a session while leaving ordinary reads whole. A tighter default was considered and
/// rejected: truncation forces the model to page with offset/limit, and extra round trips are
/// themselves a leading cause of long-horizon failure, so a cap that fires on ordinary files
/// trades a context problem for a step-count one.
/// </summary>
public static class OutputTruncation
{
    public const int DefaultMaxBytes = 50 * 1024;
    public const int DefaultMaxLines = 2000;

    public static TruncationResult Truncate(string content, RetainEnd retain, int maxBytes = DefaultMaxBytes, int maxLines = DefaultMaxLines)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLines);

        var lines = content.Split('\n');
        // A trailing newline yields a final empty element that isn't a real line — dropping it
        // keeps TotalLines honest and stops an exact-fit input from reporting as truncated.
        if (lines.Length > 0 && lines[^1].Length == 0)
            lines = lines[..^1];

        if (lines.Length == 0)
            return new TruncationResult(content, Truncated: false, 0, 0, TruncatedByBytes: false);

        var kept = new List<string>();
        var keptBytes = 0;
        var truncatedByBytes = false;

        // Walk from whichever end is being retained, so the limits bind against the content that
        // is actually being kept rather than against the content being discarded.
        for (var i = 0; i < lines.Length && kept.Count < maxLines; i++)
        {
            var line = retain == RetainEnd.Head ? lines[i] : lines[^(i + 1)];
            // +1 for the newline rejoining this line to the previous one.
            var lineBytes = Encoding.UTF8.GetByteCount(line) + (kept.Count > 0 ? 1 : 0);

            if (keptBytes + lineBytes > maxBytes)
            {
                truncatedByBytes = true;
                // A single line larger than the whole budget would otherwise yield nothing at all,
                // which is strictly less useful than a partial line — so this is the one case where
                // the never-split-a-line rule is relaxed.
                if (kept.Count == 0)
                {
                    kept.Add(TruncateToBytes(line, maxBytes, retain));
                    keptBytes = Encoding.UTF8.GetByteCount(kept[0]);
                }
                break;
            }

            kept.Add(line);
            keptBytes += lineBytes;
        }

        if (retain == RetainEnd.Tail)
            kept.Reverse();

        var truncated = kept.Count < lines.Length || truncatedByBytes;
        return new TruncationResult(string.Join('\n', kept), truncated, kept.Count, lines.Length, truncatedByBytes);
    }

    /// <summary>
    /// Cuts a single oversized line to fit a byte budget without splitting a UTF-8 code point —
    /// taking from whichever end is being retained, so a tail-retained cut keeps the end of the line.
    /// </summary>
    private static string TruncateToBytes(string line, int maxBytes, RetainEnd retain)
    {
        var bytes = Encoding.UTF8.GetBytes(line);
        if (bytes.Length <= maxBytes)
            return line;

        if (retain == RetainEnd.Head)
        {
            var end = maxBytes;
            // Back off any trailing continuation bytes (10xxxxxx) so the cut lands on a boundary.
            while (end > 0 && (bytes[end] & 0xC0) == 0x80)
                end--;
            return Encoding.UTF8.GetString(bytes, 0, end);
        }

        var start = bytes.Length - maxBytes;
        while (start < bytes.Length && (bytes[start] & 0xC0) == 0x80)
            start++;
        return Encoding.UTF8.GetString(bytes, start, bytes.Length - start);
    }
}
