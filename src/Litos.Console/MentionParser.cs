using System.Text.RegularExpressions;

namespace Litos.Console;

public static partial class MentionParser
{
    // Matches @ followed by everything up to the next @ or end of line, including spaces —
    // a filename like "Plant Resource.png" is a valid mention, but so is
    // "@foo.png please describe it", where only "foo.png" is really a path and the rest is
    // the user's sentence. Since a regex alone can't know which words are part of a real
    // file, this captures the whole greedy run; ExpandCandidates (below) turns it into an
    // ordered list of shrinking prefixes for the caller to test against the filesystem,
    // longest (most likely intended) first.
    [GeneratedRegex(@"(?<![\w@])@([A-Za-z0-9_.\-\\/:~][^@]*?)(?=@|$)")]
    private static partial Regex MentionRegex();

    public static IReadOnlyList<string> ExtractPaths(string input) =>
        MentionRegex().Matches(input).Select(m => m.Groups[1].Value.TrimEnd()).Distinct().ToList();

    /// <summary>
    /// A raw mention capture ("Plant Resource.png please describe it") may include trailing
    /// words that are part of the sentence, not the filename. Returns candidate strings from
    /// longest to shortest, trimmed of trailing sentence punctuation, so the caller can test
    /// each against the filesystem and stop at the first one that actually exists.
    /// </summary>
    public static IEnumerable<string> ExpandCandidates(string rawMention)
    {
        var words = rawMention.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var take = words.Length; take >= 1; take--)
            yield return string.Join(' ', words.Take(take)).TrimEnd('.', ',', ')', ':', ';', '!', '?');
    }
}
