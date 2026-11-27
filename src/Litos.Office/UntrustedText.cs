using System.Text;

namespace Litos.Office;

/// <summary>
/// Cell text comes from whoever made the workbook, so it is data, never an instruction (spec §11).
/// Everything derived from a cell that reaches a model goes through <see cref="Sanitize"/> (bounded,
/// single-line, no control characters, no way to fake a boundary marker) and is enclosed by
/// <see cref="Wrap"/>. Litos.Office keeps its own marker rather than depending on Litos.Tools; the
/// shape matches Litos's EXTERNAL_UNTRUSTED_CONTENT convention.
/// </summary>
public static class UntrustedText
{
    public const string OpenMarker = "<<<UNTRUSTED_CELL_TEXT";
    public const string CloseMarker = "<<<END_UNTRUSTED_CELL_TEXT>>>";

    /// <summary>One line of at most <paramref name="maxChars"/>: control characters removed,
    /// newlines and tabs turned into spaces, runs of spaces collapsed, and any "&lt;&lt;&lt;" or
    /// "&gt;&gt;&gt;" defused so cell text can't close or open a marker.</summary>
    public static string Sanitize(string? text, int maxChars = 40)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        var builder = new StringBuilder(Math.Min(text.Length, maxChars + 1));
        var lastWasSpace = false;
        foreach (var ch in text)
        {
            var c = ch is '\r' or '\n' or '\t' ? ' ' : ch;
            if (char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format)
                continue;
            if (c == ' ')
            {
                if (lastWasSpace)
                    continue;
                lastWasSpace = true;
            }
            else
            {
                lastWasSpace = false;
            }
            builder.Append(c);
        }

        var clean = Defuse(builder.ToString().Trim());
        return clean.Length <= maxChars ? clean : clean[..Math.Max(0, maxChars - 1)].TrimEnd() + "…";
    }

    /// <summary>Encloses already-sanitized content between markers naming where it came from.</summary>
    public static string Wrap(string source, string content) =>
        $"{OpenMarker} source=\"{Sanitize(source, 80).Replace("\"", "'")}\">>>\n{Defuse(content)}\n{CloseMarker}";

    private static string Defuse(string text) =>
        text.Replace("<<<", "‹‹‹", StringComparison.Ordinal).Replace(">>>", "›››", StringComparison.Ordinal);
}
