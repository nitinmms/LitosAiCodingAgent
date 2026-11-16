namespace Litos.SoftwareFactory.Core.Settings;

/// <summary>
/// How the built-in skills reach a factory's library and stay current (docs/software-factory/built-in-skills.md).
/// Pure: the host reads the skills section, calls these and saves what they return.
/// <list type="bullet">
/// <item>On start, each built-in not offered yet is added once, enabled, with its default task types;
/// one an Admin removes stays removed, as with the shipped presets.</item>
/// <item>A library skill of the same name is the Admin's: it is left alone, unless its text is the
/// built-in's exactly, when it is taken as that built-in (a factory that had the skills added by hand).</item>
/// <item>A built-in nobody edited moves to a newer shipped version on start; an edited one keeps the
/// Admin's text, and the Skills tab offers the newer version.</item>
/// <item>On save, each built-in is stamped: as the current version when it reads as shipped (an
/// Admin's "Reset to shipped"), and otherwise as the version it was before.</item>
/// </list>
/// </summary>
public static class BuiltInSkillSync
{
    /// <param name="Notes">What changed, a sentence each, for the host's log.</param>
    public sealed record Result(SkillSettings Settings, bool Changed, IReadOnlyList<string> Notes);

    public static Result OnStart(SkillSettings current, IReadOnlyList<BuiltInSkill> shipped)
    {
        var library = current.Library.ToList();
        var offered = current.Offered.ToList();
        var notes = new List<string>();
        var changed = false;

        // A built-in nobody edited follows the shipped text.
        for (var i = 0; i < library.Count; i++)
        {
            var skill = library[i];
            if (Shipped(shipped, skill.BuiltIn) is not { } builtIn || (skill.BuiltInVersion ?? 0) >= builtIn.Version
                || BuiltInSkills.StateOf(skill) != BuiltInState.AsShipped)
                continue;

            var upgraded = builtIn.Apply(skill);
            var fitting = WithoutTypesThatDoNotFit(library, i, upgraded);
            library[i] = fitting;
            notes.Add(fitting.DefaultFor.Count == upgraded.DefaultFor.Count
                ? $"Built-in skill \"{skill.Name}\" updated to version {builtIn.Version}."
                : $"Built-in skill \"{skill.Name}\" updated to version {builtIn.Version}, without its defaults for {string.Join(", ", upgraded.DefaultFor.Except(fitting.DefaultFor))}: they would pass {TaskSkillSelection.MaxChars:N0} characters.");
            changed = true;
        }

        foreach (var builtIn in shipped.Where(b => !offered.Contains(b.Name, StringComparer.Ordinal)))
        {
            offered.Add(builtIn.Name);
            changed = true;
            if (library.Any(s => s.BuiltIn == builtIn.Name))
                continue;

            var same = library.FindIndex(s => string.Equals(s.Name, builtIn.Name, StringComparison.OrdinalIgnoreCase));
            if (same >= 0)
            {
                if (builtIn.Matches(library[same]))
                {
                    library[same] = builtIn.Stamp(library[same]);
                    notes.Add($"Skill \"{library[same].Name}\" is the built-in skill of that name, and is marked as it.");
                }
                else
                {
                    notes.Add($"Built-in skill \"{builtIn.Name}\" was not added: the library has its own skill of that name.");
                }

                continue;
            }

            var added = WithoutTypesThatDoNotFit(library, library.Count, builtIn.ToLibrarySkill());
            library.Add(added);
            notes.Add(added.DefaultFor.Count == builtIn.DefaultFor.Count
                ? $"Built-in skill \"{builtIn.Name}\" added."
                : $"Built-in skill \"{builtIn.Name}\" added, without its defaults for {string.Join(", ", builtIn.DefaultFor.Except(added.DefaultFor))}: they would pass {TaskSkillSelection.MaxChars:N0} characters.");
        }

        return new Result(current with { Library = library, Offered = offered }, changed, notes);
    }

    /// <summary>
    /// The settings an Admin's save stores. The version and hash of a built-in come from the host,
    /// never from the request; so does the list of built-ins offered, which only grows.
    /// </summary>
    public static SkillSettings OnSave(SkillSettings incoming, SkillSettings stored, IReadOnlyList<BuiltInSkill> shipped)
    {
        var before = stored.Library.Where(s => s.BuiltIn is not null).GroupBy(s => s.BuiltIn!).ToDictionary(g => g.Key, g => g.First());
        var library = incoming.Library.Select(skill =>
        {
            if (skill.BuiltIn is null)
                return skill with { BuiltInVersion = null, BuiltInHash = null };
            if (Shipped(shipped, skill.BuiltIn) is not { } builtIn)
                return skill; // not a built-in: SkillSettings.Validate refuses it

            if (builtIn.Matches(skill))
                return builtIn.Stamp(skill);
            return before.TryGetValue(skill.BuiltIn, out var was)
                ? skill with { BuiltInVersion = was.BuiltInVersion, BuiltInHash = was.BuiltInHash }
                : builtIn.Stamp(skill); // added back, and edited before it was saved
        }).ToList();

        var offered = stored.Offered.Concat(library.Where(s => s.BuiltIn is not null).Select(s => s.BuiltIn!))
            .Distinct(StringComparer.Ordinal).ToList();
        return incoming with { Library = library, Offered = offered };
    }

    private static BuiltInSkill? Shipped(IReadOnlyList<BuiltInSkill> shipped, string? name) =>
        name is null ? null : shipped.FirstOrDefault(b => b.Name == name);

    /// <summary>The skill without the default task types it would push past a task's limit, with it at <paramref name="index"/>.</summary>
    private static LibrarySkill WithoutTypesThatDoNotFit(List<LibrarySkill> library, int index, LibrarySkill skill)
    {
        var others = library.Where((_, i) => i != index).Where(s => s.Enabled).ToList();
        var fitting = skill.DefaultFor
            .Where(type => TaskSkillSelection.Size([.. others.Where(s => s.DefaultFor.Contains(type)), skill]) <= TaskSkillSelection.MaxChars)
            .ToList();
        return fitting.Count == skill.DefaultFor.Count ? skill : skill with { DefaultFor = fitting };
    }
}
