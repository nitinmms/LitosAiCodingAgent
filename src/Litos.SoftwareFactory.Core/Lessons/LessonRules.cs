using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Litos.SoftwareFactory.Core.Lessons;

/// <summary>
/// What a lesson may be (ReadMe_LitosSoftwareFactory_V1.md §21, m4-architecture.md §3.1): one
/// instruction, with topics to retrieve it by and the paths it applies to. Never a secret, a stack
/// trace or a whole file; the host refuses those, whoever writes them.
/// </summary>
public static partial class LessonRules
{
    public const int MaxRuleChars = 600;
    public const int MaxReasonChars = 600;
    public const int MaxTopics = 8;
    public const int MaxTopicChars = 40;
    public const int MaxPaths = 10;
    public const int MaxPathChars = 200;

    /// <summary>The draft as stored: trimmed, topics lower-cased and distinct, paths with forward slashes.</summary>
    public static LessonDraft Clean(LessonDraft draft) => draft with
    {
        Rule = (draft.Rule ?? "").Replace("\r\n", "\n").Trim(),
        Topics = [.. (draft.Topics ?? []).Select(t => (t ?? "").Trim().ToLowerInvariant()).Where(t => t.Length > 0).Distinct()],
        Paths = [.. (draft.Paths ?? []).Select(p => (p ?? "").Trim().Replace('\\', '/')).Where(p => p.Length > 0).Distinct()],
        Reason = (draft.Reason ?? "").Trim(),
    };

    /// <summary>What is wrong with a cleaned draft, one sentence each; empty when it can be stored.</summary>
    public static IReadOnlyList<string> Validate(LessonDraft draft)
    {
        var errors = new List<string>();
        if (draft.Rule.Length == 0)
            errors.Add("Write the lesson: one instruction for later tasks.");
        else if (draft.Rule.Length > MaxRuleChars)
            errors.Add($"A lesson can be at most {MaxRuleChars} characters; this one is {draft.Rule.Length.ToString("N0", CultureInfo.InvariantCulture)}. Keep it to one instruction.");

        if (draft.Topics.Count == 0)
            errors.Add("Give it at least one topic: a word a task must mention for the lesson to be used.");
        else if (draft.Topics.Count > MaxTopics)
            errors.Add($"A lesson can have at most {MaxTopics} topics.");
        foreach (var topic in draft.Topics.Where(t => t.Length > MaxTopicChars))
            errors.Add($"The topic \"{topic[..20]}…\" is longer than {MaxTopicChars} characters.");

        if (draft.Paths.Count > MaxPaths)
            errors.Add($"A lesson can name at most {MaxPaths} paths.");
        foreach (var path in draft.Paths.Where(p => p.Length > MaxPathChars || p.Split('/').Contains("..")))
            errors.Add($"\"{(path.Length > 40 ? path[..40] + "…" : path)}\" is not a usable path: a glob within the repository, at most {MaxPathChars} characters.");

        if (draft.Reason.Length > MaxReasonChars)
            errors.Add($"The reason can be at most {MaxReasonChars} characters.");
        if (draft.Source != LessonSource.Manual && draft.SourceRef is null)
            errors.Add("Name what the lesson rests on: the message, decision, finding or handoff.");

        var text = draft.Rule + "\n" + draft.Reason;
        if (LooksLikeSecret(text))
            errors.Add("It looks like it contains a secret (a key, token or password). Every run of the project is shown its lessons; leave secrets out.");
        if (LooksLikeStackTrace(text))
            errors.Add("It looks like a stack trace or a log. Say what to do, not what happened.");
        return errors;
    }

    /// <summary>The rule as compared for duplicates: lower-cased, punctuation dropped, whitespace collapsed, hashed.</summary>
    public static string Normalize(string rule)
    {
        var sb = new StringBuilder(rule.Length);
        var pendingSpace = false;
        foreach (var c in rule.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                if (pendingSpace && sb.Length > 0)
                    sb.Append(' ');
                sb.Append(c);
                pendingSpace = false;
            }
            else if (char.IsWhiteSpace(c))
            {
                pendingSpace = true;
            }
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    public static bool LooksLikeSecret(string text) => SecretPattern().IsMatch(text);

    public static bool LooksLikeStackTrace(string text) => StackTracePattern().IsMatch(text);

    /// <summary>Common key and token formats, a private key block, and a long value given to a
    /// secret-sounding name. Deliberately broad: a refused lesson is rewritten in a moment.</summary>
    [GeneratedRegex(
        """\b(sk-[A-Za-z0-9_-]{20,}|gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{20,}|AKIA[0-9A-Z]{16}|xox[abprs]-[A-Za-z0-9-]{10,}|AIza[0-9A-Za-z_-]{30,}|eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,})|-----BEGIN [A-Z ]*PRIVATE KEY-----|\b(password|passwd|secret|api[_-]?key|access[_-]?token|client[_-]?secret)\s*[:=]\s*['"]?[^\s'"]{8,}""",
        RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SecretPattern();

    /// <summary>A .NET, Java or Python stack frame, or a Python traceback header.</summary>
    [GeneratedRegex(
        """^\s*at [\w.$<>`]+\(.*\)( in .+:line \d+)?\s*$|^\s*at [\w.$]+\([\w.]+\.java:\d+\)|Traceback \(most recent call last\)|^\s*File ".+", line \d+""",
        RegexOptions.Multiline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex StackTracePattern();
}
