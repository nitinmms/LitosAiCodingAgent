using System.Text.RegularExpressions;

namespace Litos.Kernel.Host;

/// <summary>
/// Turns a raw Roslyn diagnostic into something the model can act on, for the small set of compile
/// failures that are both common in kernel scripts and mechanically diagnosable from the source.
///
/// The tool description already explains these rules, but the description is read once at the top
/// of a long session while the diagnostic is what the model sees at the moment it is wrong — and a
/// bare "CS8997: Unterminated raw string literal" points at the line where the literal ran off the
/// end, not at the opening quotes that actually caused it. Attaching the correction to the error
/// closes that gap without spending prompt budget on every turn.
/// </summary>
internal static partial class CompileErrorHints
{
    public static string? For(string diagnostics, string code)
    {
        if (diagnostics.Contains("CS8997", StringComparison.Ordinal) && InlineOpenedRawString().IsMatch(code))
        {
            return
                "Hint: a multi-line raw string literal must begin on the line AFTER its opening quotes, "
                + "and its closing quotes must be on their own line. This script opens one inline "
                + "(e.g. var s = \"\"\"first line), which is what CS8997 is reporting.\n"
                + "Write it as:\n"
                + "  var s = \"\"\"\n"
                + "  first line\n"
                + "  second line\n"
                + "  \"\"\";\n"
                + "The content needs no escaping. If it contains a run of three or more double quotes, "
                + "fence it with more quotes than the longest run (\"\"\"\" ... \"\"\"\").";
        }

        return null;
    }

    /// <summary>
    /// Matches a raw-string opener (three or more double quotes) followed on the SAME line by
    /// anything other than whitespace — the malformed multi-line form. A single-line raw string
    /// ("""text""") is legal and closes on the same line, so the match deliberately requires no
    /// closing run before end-of-line.
    /// </summary>
    [GeneratedRegex("\"{3,}[^\"\\r\\n]*[^\\s\"][^\"\\r\\n]*(\\r?\\n)", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex InlineOpenedRawString();
}
