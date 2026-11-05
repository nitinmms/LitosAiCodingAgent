using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Litos.Agent.Messages;

namespace Litos.Agent.Session;

/// <summary>A skill's full instructions, as the <c>skill</c> tool returned them.</summary>
public sealed record RetainedSkill(string Name, string Body);

/// <summary>
/// Keeps loaded skills alive across compaction. A skill's instructions reach the model only as
/// the <c>skill</c> tool's result in the transcript, and Compactor replaces everything before its
/// cut with an LLM-written summary — so without this, a skill loaded early in a long session (the
/// artifact-design rules, say) silently vanishes at the first compaction while the model carries
/// on as if it still knew them.
///
/// The bodies are re-attached verbatim inside the new CompactionSummaryBlock's text, in a
/// delimited section, rather than as extra messages. That way every provider already renders them
/// (summary blocks are sent as plain text), they persist with the checkpoint in the JSONL and
/// survive Transcript.LoadAsync's replay, and the next compaction finds them again by parsing the
/// section back out of the earlier summary — so a skill survives any number of compactions.
/// See ReadMe_ArtifactsFeature.md §6.
///
/// A skill loaded from inside a PTC kernel script never appears as a <c>skill</c> tool call in
/// the transcript, so it is not retained.
/// </summary>
public static partial class RetainedSkills
{
    /// <summary>The skill-loading tool's name — Litos.Tools' SkillTool uses this constant.</summary>
    public const string SkillToolName = "skill";

    /// <summary>At most this many skills are re-attached, most recently loaded first.</summary>
    public const int MaxSkills = 5;

    /// <summary>Upper bound on the combined body budget for re-attached skills, whatever the window.</summary>
    public const int MaxTotalChars = 60_000;

    /// <summary>
    /// The combined body budget for one compaction: at most half the kept tail's token budget (at
    /// ~4 chars/token), capped at <see cref="MaxTotalChars"/>. Proportional because a fixed budget
    /// would, on a small local window (16K–32K), leave the context at or over the compaction
    /// trigger straight after the compaction meant to free it — compacting again every round — and
    /// would add the same fixed cost to every compaction on any window. KeepRecentTokens is already
    /// scaled to the window by CompactionSettings.ForContextWindow (a quarter of it on small ones).
    /// </summary>
    public static int CharBudgetFor(CompactionSettings settings) =>
        Math.Min(MaxTotalChars, settings.KeepRecentTokens * 2);

    private const string SectionStart = "<litos-retained-skills>";
    private const string SectionEnd = "</litos-retained-skills>";

    private const string Preamble =
        "These skills were loaded earlier in this session and are re-attached verbatim so they survive "
        + "context compaction. Treat their instructions as already loaded; do not call the skill tool for them again.";

    [GeneratedRegex("<litos-skill name=\"([^\"]*)\">\\n(.*?)\\n</litos-skill>", RegexOptions.Singleline)]
    private static partial Regex SkillPattern();

    /// <summary>
    /// The skills loaded in <paramref name="messages"/> — successful <c>skill</c> tool calls, plus
    /// skills an earlier compaction already re-attached — oldest load first, one per name (the
    /// latest load of a name wins and counts as that name's load time).
    /// </summary>
    public static IReadOnlyList<RetainedSkill> Collect(IEnumerable<ChatMessage> messages)
    {
        var namesByCallId = new Dictionary<string, string>();
        var latest = new Dictionary<string, (RetainedSkill Skill, int Order)>();
        var order = 0;

        void Record(string name, string body) => latest[name] = (new RetainedSkill(name, body), order++);

        foreach (var message in messages)
            foreach (var block in message.Content)
                switch (block)
                {
                    case CompactionSummaryBlock summary:
                        foreach (var skill in Parse(summary.Summary))
                            Record(skill.Name, skill.Body);
                        break;
                    case ToolUseBlock use when use.ToolName == SkillToolName && SkillNameOf(use.Arguments) is { } name:
                        namesByCallId[use.CallId] = name;
                        break;
                    case ToolResultBlock result when namesByCallId.Remove(result.CallId, out var loadedName):
                        if (!result.IsError)
                            Record(loadedName, result.Text);
                        break;
                }

        return [.. latest.Values.OrderBy(v => v.Order).Select(v => v.Skill)];
    }

    /// <summary>
    /// <paramref name="summary"/> with <paramref name="skills"/> appended in a retained-skills
    /// section; unchanged when there are none. Skills over <see cref="MaxSkills"/> or
    /// <paramref name="maxTotalChars"/> are dropped oldest-first and named, so the model can reload
    /// them. A large recent skill that doesn't fit is skipped while smaller older ones still can.
    /// </summary>
    /// <param name="maxTotalChars">Combined body budget — Compactor passes <see cref="CharBudgetFor"/>.</param>
    public static string Append(string summary, IReadOnlyList<RetainedSkill> skills, int maxTotalChars = MaxTotalChars)
    {
        if (skills.Count == 0)
            return summary;

        var kept = new List<RetainedSkill>();
        var dropped = new List<string>();
        var totalChars = 0;
        foreach (var skill in skills.Reverse())
        {
            if (kept.Count < MaxSkills && totalChars + skill.Body.Length <= maxTotalChars)
            {
                kept.Add(skill);
                totalChars += skill.Body.Length;
            }
            else
            {
                dropped.Add(skill.Name);
            }
        }
        kept.Reverse();
        dropped.Reverse();

        var text = new StringBuilder(StripSection(summary).TrimEnd());
        text.Append("\n\n").Append(SectionStart).Append('\n').Append(Preamble).Append('\n');
        foreach (var skill in kept)
            text.Append("<litos-skill name=\"").Append(skill.Name).Append("\">\n").Append(skill.Body).Append("\n</litos-skill>\n");
        if (dropped.Count > 0)
            text.Append("Not re-attached (over the size budget), load again with the skill tool if needed: ")
                .Append(string.Join(", ", dropped)).Append('\n');
        text.Append(SectionEnd);
        return text.ToString();
    }

    /// <summary>The skills re-attached in a summary's retained-skills section, in order.</summary>
    public static IReadOnlyList<RetainedSkill> Parse(string summary)
    {
        var start = summary.IndexOf(SectionStart, StringComparison.Ordinal);
        if (start < 0)
            return [];

        var section = summary[start..];
        return [.. SkillPattern().Matches(section).Select(m => new RetainedSkill(m.Groups[1].Value, m.Groups[2].Value))];
    }

    /// <summary>
    /// The summary without its retained-skills section — what the summarizer is shown (so it
    /// never paraphrases or duplicates the verbatim bodies) and what a UI displays to the user.
    /// </summary>
    public static string StripSection(string summary)
    {
        var start = summary.IndexOf(SectionStart, StringComparison.Ordinal);
        return start < 0 ? summary : summary[..start].TrimEnd();
    }

    private static string? SkillNameOf(JsonElement arguments) =>
        arguments.ValueKind == JsonValueKind.Object
        && arguments.TryGetProperty("name", out var name)
        && name.ValueKind == JsonValueKind.String
        && name.GetString() is { Length: > 0 } value
            ? value
            : null;
}
