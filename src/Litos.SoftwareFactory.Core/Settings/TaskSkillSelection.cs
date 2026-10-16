using System.Globalization;
using System.Text.Json;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Lifecycle;

namespace Litos.SoftwareFactory.Core.Settings;

/// <summary>
/// The skills chosen for a task (m3-architecture.md §13): picked from the factory library when the
/// task is created, starting from its type's defaults, and given whole to every session of its runs
/// in the system prompt, which compaction never summarises. A run takes them when it starts; a
/// change while the task works applies once it is paused and resumed.
/// </summary>
public static class TaskSkillSelection
{
    /// <summary>The most instruction text a task's skills may come to together.</summary>
    public const int MaxChars = 12_000;

    /// <summary>The instruction text these skills put in the system prompt.</summary>
    public static int Size(IEnumerable<LibrarySkill> skills) => skills.Sum(s => Text(s).Length);

    /// <summary>The skills a new task of this type starts with chosen.</summary>
    public static IReadOnlyList<string> DefaultsFor(SkillSettings settings, string type) =>
        [.. settings.Enabled.Where(s => (s.DefaultFor ?? []).Contains(type)).Select(s => s.Name)];

    /// <summary>
    /// The library's names for <paramref name="names"/>, in the library's order, or why they cannot
    /// be chosen: each must be an enabled library skill, once, and all together within <see cref="MaxChars"/>.
    /// </summary>
    public static IReadOnlyList<string> Choose(IReadOnlyList<string> names, SkillSettings settings, out string? refused)
    {
        refused = null;
        var chosen = names.Select(n => n?.Trim() ?? "").ToList();
        if (chosen.Any(n => n.Length == 0))
        {
            refused = "A skill name is empty.";
            return [];
        }

        var duplicate = chosen.GroupBy(n => n, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            refused = $"\"{duplicate.Key}\" is chosen twice.";
            return [];
        }

        var enabled = settings.Enabled;
        var unknown = chosen.Where(n => !enabled.Any(s => string.Equals(s.Name, n, StringComparison.OrdinalIgnoreCase))).ToList();
        if (unknown.Count > 0)
        {
            refused = $"Not a skill in the factory's library, or turned off: {string.Join(", ", unknown)}.";
            return [];
        }

        var skills = enabled.Where(s => chosen.Contains(s.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        var size = Size(skills);
        if (size > MaxChars)
        {
            refused = $"These skills come to {size.ToString("N0", CultureInfo.InvariantCulture)} characters; " +
                $"a task can take at most {MaxChars.ToString("N0", CultureInfo.InvariantCulture)}.";
            return [];
        }

        return [.. skills.Select(s => s.Name)];
    }

    /// <summary>What a run is given for the task's chosen skills.</summary>
    /// <param name="Missing">Chosen, but no longer in the library or turned off.</param>
    /// <param name="OverLimit">Left out because a skill grew after it was chosen and they no longer fit.</param>
    public sealed record Resolved(IReadOnlyList<TaskSkill> Applied, IReadOnlyList<string> Missing, IReadOnlyList<string> OverLimit);

    /// <summary>
    /// The chosen skills' text as the library has it now. Nothing that runs depends on a skill
    /// still being there: one deleted or turned off since it was chosen is left out and named.
    /// </summary>
    public static Resolved Resolve(IReadOnlyList<string> names, SkillSettings settings)
    {
        var enabled = settings.Enabled;
        List<TaskSkill> applied = [];
        List<string> missing = [];
        List<string> overLimit = [];
        var size = 0;
        foreach (var name in names)
        {
            if (enabled.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) is not { } skill)
            {
                missing.Add(name);
                continue;
            }

            var text = Text(skill);
            if (size + text.Length > MaxChars)
            {
                overLimit.Add(skill.Name);
                continue;
            }

            size += text.Length;
            applied.Add(new TaskSkill(skill.Name, text));
        }

        return new Resolved(applied, missing, overLimit);
    }

    /// <summary>What the thread is told about chosen skills a run could not apply; null when all were.</summary>
    public static string? Describe(Resolved resolved)
    {
        var lines = new List<string>();
        if (resolved.Missing.Count > 0)
            lines.Add($"Skills chosen for this task that are no longer in the factory's library, or are turned off, are not applied: {string.Join(", ", resolved.Missing)}.");
        if (resolved.OverLimit.Count > 0)
            lines.Add($"Skills left out because the task's skills now pass {MaxChars.ToString("N0", CultureInfo.InvariantCulture)} characters: {string.Join(", ", resolved.OverLimit)}.");
        return lines.Count == 0 ? null : string.Join("\n", lines);
    }

    /// <summary>What the thread is told when someone changes its skills, by when the change takes effect.</summary>
    public static string ChangeNote(IReadOnlyList<string> names, LifecycleState state)
    {
        var what = names.Count == 0
            ? "The skills for this task were cleared."
            : $"The skills for this task changed to: {string.Join(", ", names)}.";
        var when = state switch
        {
            LifecycleState.Running => "They will be applied after the task is paused and resumed.",
            LifecycleState.Queued => "They apply when the task starts.",
            LifecycleState.AwaitingDecision or LifecycleState.PausedUser or LifecycleState.PausedBudget
                or LifecycleState.Blocked or LifecycleState.Interrupted => "They will be applied when the task resumes.",
            _ => "They apply from the task's next run.",
        };
        return $"{what} {when}";
    }

    /// <summary>A thread's stored choice; an unreadable one is no choice.</summary>
    public static IReadOnlyList<string> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string Serialize(IReadOnlyList<string> names) => JsonSerializer.Serialize(names);

    private static string Text(LibrarySkill skill) => skill.Body.Replace("\r\n", "\n").Trim();
}
