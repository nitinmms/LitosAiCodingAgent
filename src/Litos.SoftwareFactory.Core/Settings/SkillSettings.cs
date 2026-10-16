using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Litos.SoftwareFactory.Core.Settings;

/// <summary>How runs treat the skills a repository brings in its <c>.litos/skills</c> (blueprint §8.2).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RepositorySkillPolicy
{
    /// <summary>Each one, and each change to one, is used once an Admin approves it.</summary>
    ApproveEach,

    /// <summary>Every one is used as found.</summary>
    Always,

    /// <summary>None is used.</summary>
    Never,
}

/// <summary>One skill of the factory library: a SKILL.md, its frontmatter written from these fields.</summary>
public sealed record LibrarySkill
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public string Body { get; init; } = "";

    /// <summary>Given to runs. A skill that is off is kept, but no run sees it.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>The task types (<see cref="Lifecycle.TaskTypes"/>) whose new tasks start with this
    /// skill chosen (m3-architecture.md §13). The person creating a task can still untick it.</summary>
    public IReadOnlyList<string> DefaultFor { get; init; } = [];

    /// <summary>The SKILL.md the worker reads.</summary>
    public string Markdown() => $"---\nname: {Name}\ndescription: {Description}\n---\n\n{Body.Replace("\r\n", "\n")}\n";
}

/// <summary>
/// The Skills tab (blueprint §8.2, m3-architecture.md §7.3): the factory's own skill library and
/// what runs do with a repository's skills. By default the library is empty and a repository
/// skill waits for an Admin's approval.
/// </summary>
public sealed partial record SkillSettings
{
    public RepositorySkillPolicy RepositoryPolicy { get; init; } = RepositorySkillPolicy.ApproveEach;
    public IReadOnlyList<LibrarySkill> Library { get; init; } = [];

    public const int MaxSkills = 100;
    public const int MaxDescriptionChars = 1_024;
    public const int MaxBodyChars = 100_000;

    /// <summary>The library skills runs are given. Not part of the stored or sent section.</summary>
    [JsonIgnore]
    public IReadOnlyList<LibrarySkill> Enabled => [.. Library.Where(s => s.Enabled)];

    /// <summary>What is wrong with these settings, one sentence each; empty when they are usable.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!Enum.IsDefined(RepositoryPolicy))
            errors.Add("Choose how repository skills are treated.");
        if (Library.Count > MaxSkills)
            errors.Add($"The library can hold at most {MaxSkills} skills.");

        foreach (var duplicate in Library.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            errors.Add($"There are two skills named \"{duplicate.Key}\".");

        foreach (var skill in Library)
        {
            if (!NamePattern().IsMatch(skill.Name ?? ""))
            {
                errors.Add($"\"{skill.Name}\" is not a usable skill name: letters, digits, '.', '-' and '_', up to 64, starting with a letter or digit.");
                continue;
            }

            // The frontmatter is read a line at a time, so a description must be one line.
            if (string.IsNullOrWhiteSpace(skill.Description))
                errors.Add($"{skill.Name}: describe when the agent should use it; the agent sees only the description until it loads the skill.");
            else if (skill.Description.Contains('\n') || skill.Description.Contains('\r'))
                errors.Add($"{skill.Name}: its description must be a single line.");
            else if (skill.Description.Length > MaxDescriptionChars)
                errors.Add($"{skill.Name}: its description can be at most {MaxDescriptionChars.ToString("N0", CultureInfo.InvariantCulture)} characters.");

            if (string.IsNullOrWhiteSpace(skill.Body))
                errors.Add($"{skill.Name}: write its instructions.");
            else if (skill.Body.Length > MaxBodyChars)
                errors.Add($"{skill.Name}: its instructions can be at most {MaxBodyChars.ToString("N0", CultureInfo.InvariantCulture)} characters.");

            foreach (var type in (skill.DefaultFor ?? []).Where(t => !Lifecycle.TaskTypes.IsKnown(t)).Distinct())
                errors.Add($"{skill.Name}: \"{type}\" is not a task type; choose from {string.Join(", ", Lifecycle.TaskTypes.All)}.");
        }

        // A new task starts with its type's defaults chosen, so they must fit in a task together.
        foreach (var type in Lifecycle.TaskTypes.All)
        {
            var defaults = Library.Where(s => s.Enabled && (s.DefaultFor ?? []).Contains(type)).ToList();
            var chars = TaskSkillSelection.Size(defaults);
            if (chars > TaskSkillSelection.MaxChars)
                errors.Add($"The skills chosen by default for a {type} task come to {chars.ToString("N0", CultureInfo.InvariantCulture)} characters; " +
                    $"a task can take at most {TaskSkillSelection.MaxChars.ToString("N0", CultureInfo.InvariantCulture)}.");
        }

        return errors;
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$")]
    private static partial Regex NamePattern();
}

/// <summary>What became of one repository skill in a run.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RepositorySkillStatus
{
    Used,
    WaitingForApproval,

    /// <summary>An earlier version was approved; this one is waiting for its own approval.</summary>
    ChangedSinceApproved,

    /// <summary>The factory's policy uses no repository skill.</summary>
    NotUsedByPolicy,
}

/// <summary>A skill a repository has, by the name its SKILL.md gives and the hash of its folder.</summary>
public sealed record FoundSkill(string Name, string Description, string Hash);

/// <param name="ReplacesLibrarySkill">It is used, and a library skill of the same name is not (the repository's wins).</param>
public sealed record RepositorySkillUse(string Name, string Hash, RepositorySkillStatus Status, bool ReplacesLibrarySkill = false)
{
    public bool Used => Status == RepositorySkillStatus.Used;
}

/// <summary>Which of a repository's skills a run uses (m3-architecture.md §7.3).</summary>
public static class SkillSelection
{
    /// <param name="found">The skills in the working copy. Where two folders give one name, the first is the skill.</param>
    /// <param name="approved">The (name, hash) pairs an Admin approved for this project.</param>
    /// <param name="library">The names of the library skills the run has, which a used repository skill replaces.</param>
    public static IReadOnlyList<RepositorySkillUse> Decide(
        RepositorySkillPolicy policy, IEnumerable<FoundSkill> found, IReadOnlyCollection<(string Name, string Hash)> approved,
        IEnumerable<string> library)
    {
        var libraryNames = library.ToHashSet(StringComparer.Ordinal);
        return
        [
            .. found.DistinctBy(s => s.Name, StringComparer.Ordinal).Select(skill =>
            {
                var status = policy switch
                {
                    RepositorySkillPolicy.Always => RepositorySkillStatus.Used,
                    RepositorySkillPolicy.Never => RepositorySkillStatus.NotUsedByPolicy,
                    _ when approved.Contains((skill.Name, skill.Hash)) => RepositorySkillStatus.Used,
                    _ when approved.Any(a => a.Name == skill.Name) => RepositorySkillStatus.ChangedSinceApproved,
                    _ => RepositorySkillStatus.WaitingForApproval,
                };
                return new RepositorySkillUse(skill.Name, skill.Hash, status, status == RepositorySkillStatus.Used && libraryNames.Contains(skill.Name));
            }),
        ];
    }

    /// <summary>What a run's thread is told about its repository's skills; null when there is nothing to say.</summary>
    public static string? Describe(IReadOnlyList<RepositorySkillUse> uses)
    {
        var lines = new List<string>();
        var off = uses.Where(u => u.Status == RepositorySkillStatus.NotUsedByPolicy).Select(u => u.Name).ToList();
        if (off.Count > 0)
            lines.Add($"The repository's skills are not used, by the factory's settings: {string.Join(", ", off)}.");

        var waiting = uses
            .Where(u => u.Status is RepositorySkillStatus.WaitingForApproval or RepositorySkillStatus.ChangedSinceApproved)
            .Select(u => u.Status == RepositorySkillStatus.ChangedSinceApproved ? $"{u.Name} (changed since it was approved)" : u.Name)
            .ToList();
        if (waiting.Count > 0)
            lines.Add($"Skipped repository skills waiting for an Admin's approval: {string.Join(", ", waiting)}.");

        foreach (var replacing in uses.Where(u => u.ReplacesLibrarySkill))
            lines.Add($"The repository's skill \"{replacing.Name}\" is used in place of the factory library's.");

        return lines.Count == 0 ? null : string.Join("\n", lines);
    }
}
