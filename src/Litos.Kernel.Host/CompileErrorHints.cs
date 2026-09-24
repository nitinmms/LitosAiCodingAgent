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
    /// <summary>
    /// All three raw-string diagnostics are answered with ONE combined correction rather than a
    /// per-code message. Observed in a real session: hinting only CS8997's half of the rule ("open
    /// on the next line") produced a script that opened correctly and still closed inline, failing
    /// the very next eval with CS9000. The rules are a single construct and are cheaper to state
    /// together than to learn one diagnostic at a time.
    /// </summary>
    private const string RawStringHint =
        "Hint: a multi-line raw string literal has three rules, and breaking any one of them "
        + "produces the error above:\n"
        + "  1. Content starts on the line AFTER the opening quotes (CS8997 if not).\n"
        + "  2. The closing quotes sit on their OWN line (CS9000 if not).\n"
        + "  3. Every content line is indented at least as much as that closing line (CS8999 if not).\n"
        + "Write it as:\n"
        + "  var s = \"\"\"\n"
        + "  first line\n"
        + "  second line\n"
        + "  \"\"\";\n"
        + "Putting the closing \"\"\" at column 0 makes rule 3 automatic.\n"
        + "IMPORTANT: the fence must be LONGER than the longest run of double quotes anywhere in "
        + "the text. If the text contains \"\"\" (common in many languages and formats), a \"\"\" "
        + "fence closes early and the rest of the script is parsed as code. Count the longest run "
        + "inside, then use at least one more:\n"
        + "  var text = \"\"\"\"\n"
        + "  a line containing \"\"\" three quotes\n"
        + "  \"\"\"\";\n"
        + "The content is never escaped or interpolated (unless you prefix with $), so do not "
        + "backslash-escape quotes inside it — \\\" is not an escape there and the backslash is "
        + "written out literally.";

    public static string? For(string diagnostics, string code)
    {
        var isRawStringError =
            diagnostics.Contains("CS8997", StringComparison.Ordinal)  // unterminated
            || diagnostics.Contains("CS8999", StringComparison.Ordinal)  // content line under-indented vs. closing line
            || diagnostics.Contains("CS9000", StringComparison.Ordinal); // delimiter not on its own line

        // CS8997 also fires for an ordinary unterminated "..." string, so it alone is not proof a
        // raw string is involved; requiring a 3+ quote run in the source keeps the hint off
        // unrelated failures. CS8999/CS9000 are raw-string-specific and need no such check.
        if (isRawStringError && ContainsRawStringDelimiter().IsMatch(code))
            return RawStringHint;

        return null;
    }

    /// <summary>
    /// Any run of three or more double quotes, i.e. the script is plausibly using a raw string at
    /// all. Deliberately broad: CS8999/CS9000 are already raw-string-specific, and this only has to
    /// keep the hint off a CS8997 raised by an ordinary unterminated "..." string.
    /// </summary>
    [GeneratedRegex("\"{3,}", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ContainsRawStringDelimiter();
}
