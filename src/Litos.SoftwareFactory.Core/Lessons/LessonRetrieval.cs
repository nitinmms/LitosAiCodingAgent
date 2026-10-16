using System.Text;
using System.Text.RegularExpressions;

namespace Litos.SoftwareFactory.Core.Lessons;

/// <summary>An approved, enabled lesson a run could be given, at its approved revision.</summary>
public sealed record LessonCandidate(
    Guid LessonId, long Number, int Revision, string Rule, IReadOnlyList<string> Topics, IReadOnlyList<string> Paths, DateTimeOffset ApprovedAt)
{
    /// <summary>How the lesson reads in a run's system prompt.</summary>
    public string Text => $"[L-{Number} r{Revision}] {Rule}";
}

/// <summary>Why a lesson is, or is not, in a run's snapshot.</summary>
public enum LessonInclusion
{
    /// <summary>Its topics or paths matched the task.</summary>
    Retrieved,

    /// <summary>A person picked it for the task.</summary>
    Manual,

    /// <summary>It matched, or was picked, but did not fit the limits; recorded so it is reported, not silently dropped.</summary>
    NotIncluded,
}

public sealed record LessonPick(LessonCandidate Lesson, int Score, LessonInclusion Inclusion, bool Manual = false);

/// <summary>
/// Which approved lessons a run is given (ReadMe_LitosSoftwareFactory_V1.md §22, m4-architecture.md
/// §4.1): deterministic topic and path matching, no model call. Every topic found among the task's
/// words scores 2; a lesson path matching a path the task names scores 1; a lesson scoring 0 is not
/// retrieved. Manual picks come first, then score, then the most recently approved, then number. At
/// most five lessons and 1,500 tokens, and never more than 2% of the task's remaining budget.
/// </summary>
public static partial class LessonRetrieval
{
    public const int MaxLessons = 5;
    public const int MaxTokens = 1_500;
    public const double RemainingBudgetShare = 0.02;
    private const int CharsPerToken = 4;

    public static int Tokens(string text) => (text.Length + CharsPerToken - 1) / CharsPerToken;

    /// <param name="taskText">The task's words: its title, request, type and any approved specification.</param>
    /// <param name="manual">Lessons a person picked for the task; each is included first if it fits.</param>
    /// <param name="remainingBudget">What the task has left under its cap; null for no cap.</param>
    public static IReadOnlyList<LessonPick> Select(
        IEnumerable<LessonCandidate> candidates, string taskText, IReadOnlyCollection<Guid> manual, long? remainingBudget)
    {
        var words = " " + string.Join(' ', Words(taskText)) + " ";
        var named = NamedPaths(taskText);
        var ranked = candidates
            .Select(c => (Candidate: c, Score: Score(c, words, named), Manual: manual.Contains(c.LessonId)))
            .Where(r => r.Manual || r.Score > 0)
            .OrderByDescending(r => r.Manual)
            .ThenByDescending(r => r.Score)
            .ThenByDescending(r => r.Candidate.ApprovedAt)
            .ThenBy(r => r.Candidate.Number)
            .ToList();

        var tokenLimit = remainingBudget is { } left ? Math.Min(MaxTokens, (long)Math.Floor(Math.Max(0, left) * RemainingBudgetShare)) : MaxTokens;
        var picks = new List<LessonPick>();
        var (count, tokens) = (0, 0L);
        foreach (var (candidate, score, isManual) in ranked)
        {
            var cost = Tokens(candidate.Text);
            var fits = count < MaxLessons && tokens + cost <= tokenLimit;
            if (fits)
            {
                count++;
                tokens += cost;
            }

            picks.Add(new LessonPick(candidate, score, fits ? (isManual ? LessonInclusion.Manual : LessonInclusion.Retrieved) : LessonInclusion.NotIncluded, isManual));
        }

        return picks;
    }

    /// <summary>The lessons a run is actually given, in order.</summary>
    public static IReadOnlyList<LessonCandidate> Included(IEnumerable<LessonPick> picks) =>
        [.. picks.Where(p => p.Inclusion != LessonInclusion.NotIncluded).Select(p => p.Lesson)];

    /// <summary>What the thread is told about the lessons a run starts with; null when there are none.</summary>
    public static string? Describe(IReadOnlyList<LessonPick> picks)
    {
        var included = picks.Where(p => p.Inclusion != LessonInclusion.NotIncluded).Select(p => $"L-{p.Lesson.Number}").ToList();
        var left = picks.Where(p => p.Inclusion == LessonInclusion.NotIncluded).Select(p => $"L-{p.Lesson.Number}").ToList();
        if (included.Count == 0 && left.Count == 0)
            return null;

        var lines = new List<string>();
        if (included.Count > 0)
            lines.Add($"Project lessons given to this run: {string.Join(", ", included)}.");
        if (left.Count > 0)
            lines.Add($"Left out, past the limit of {MaxLessons} lessons and {MaxTokens:N0} tokens (or 2% of the remaining budget): {string.Join(", ", left)}.");
        return string.Join("\n", lines);
    }

    private static int Score(LessonCandidate candidate, string words, IReadOnlyList<string> named)
    {
        var score = candidate.Topics.Count(topic => Words(topic) is { Count: > 0 } phrase && words.Contains(" " + string.Join(' ', phrase) + " ", StringComparison.Ordinal)) * 2;
        if (candidate.Paths.Any(glob => named.Any(path => Glob(glob).IsMatch(path))))
            score += 1;
        return score;
    }

    /// <summary>Lower-case words: letters and digits, split on everything else.</summary>
    internal static List<string> Words(string text)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        foreach (var c in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                current.Append(c);
            }
            else if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
            words.Add(current.ToString());
        return words;
    }

    /// <summary>Path-like tokens in the task's words (a slash, or a name with an extension).</summary>
    internal static IReadOnlyList<string> NamedPaths(string text) =>
        [.. PathToken().Matches(text).Select(m => m.Value.Replace('\\', '/').Trim('.', ',', ';', ':', ')', '(', '`', '"', '\'')).Where(p => p.Length > 0).Distinct()];

    /// <summary>A glob as a regular expression: ** any depth, * within one segment, ? one character.</summary>
    internal static Regex Glob(string glob)
    {
        var pattern = new StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                pattern.Append(".*");
                i++;
                if (i + 1 < glob.Length && glob[i + 1] == '/')
                    i++;
            }
            else if (c == '*')
                pattern.Append("[^/]*");
            else if (c == '?')
                pattern.Append("[^/]");
            else
                pattern.Append(Regex.Escape(c.ToString()));
        }

        return new Regex(pattern.Append('$').ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }

    [GeneratedRegex(@"[\w.\-]*[/\\][\w.\-/\\]+|\b[\w\-]+\.[A-Za-z][A-Za-z0-9]{0,5}\b", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PathToken();
}
