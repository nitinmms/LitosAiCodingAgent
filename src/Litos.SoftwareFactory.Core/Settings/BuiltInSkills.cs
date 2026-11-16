using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Litos.SoftwareFactory.Core.Settings;

/// <summary>One skill the factory ships, as its embedded SKILL.md gives it.</summary>
/// <param name="Version">Raised whenever the shipped text changes, so a factory upgraded to it
/// updates the copies nobody edited.</param>
public sealed record BuiltInSkill(string Name, int Version, string Description, string Body, IReadOnlyList<string> DefaultFor)
{
    /// <summary>The library skill a factory starts with: on, stamped as this version.</summary>
    public LibrarySkill ToLibrarySkill() => Stamp(new LibrarySkill { Name = Name, Description = Description, Body = Body, DefaultFor = DefaultFor });

    /// <summary>The skill with this version's text and defaults, keeping its name and whether it is on.</summary>
    public LibrarySkill Apply(LibrarySkill skill) => Stamp(skill with { Description = Description, Body = Body, DefaultFor = DefaultFor });

    /// <summary>The skill marked as this built-in at this version, whatever its text now is.</summary>
    public LibrarySkill Stamp(LibrarySkill skill) =>
        skill with { BuiltIn = Name, BuiltInVersion = Version, BuiltInHash = BuiltInSkills.ContentHash(Description, Body, DefaultFor) };

    /// <summary>Whether the skill reads as this version shipped it: description, instructions and defaults.</summary>
    public bool Matches(LibrarySkill skill) =>
        BuiltInSkills.ContentHash(skill) == BuiltInSkills.ContentHash(Description, Body, DefaultFor);
}

/// <summary>Where a library skill stands against the built-in it came from, for the Skills tab.</summary>
public enum BuiltInState
{
    /// <summary>An Admin's own skill.</summary>
    NotBuiltIn,

    /// <summary>The text of the version it was stamped from, unedited.</summary>
    AsShipped,

    /// <summary>Edited since it was stamped.</summary>
    Modified,
}

/// <summary>
/// The skills the factory ships (docs/software-factory/built-in-skills.md): SKILL.md files embedded
/// here, under Skills/BuiltIn, like the brief templates. They are development practices — test first,
/// find the root cause, verify before submitting, review by the spec — written to work inside the
/// factory's briefs, which own the process: each one says which turns it applies to and that the brief
/// wins. The host adds them to the library on start (<see cref="BuiltInSkillSync"/>).
/// </summary>
public static class BuiltInSkills
{
    private const string ResourceSuffix = ".md";
    private const string ResourceInfix = ".Skills.BuiltIn.";

    private static readonly Lazy<IReadOnlyList<BuiltInSkill>> Loaded = new(Load);

    /// <summary>Every built-in skill, in name order.</summary>
    public static IReadOnlyList<BuiltInSkill> All => Loaded.Value;

    public static BuiltInSkill? Find(string? name) => All.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal));

    /// <summary>Where a library skill stands against the built-in it came from.</summary>
    public static BuiltInState StateOf(LibrarySkill skill) => skill.BuiltIn is null
        ? BuiltInState.NotBuiltIn
        : skill.BuiltInHash == ContentHash(skill) ? BuiltInState.AsShipped : BuiltInState.Modified;

    /// <summary>The hash of what a skill says: its description, instructions and default task types.
    /// Its name and whether it is on are an Admin's to change without the skill counting as edited.</summary>
    public static string ContentHash(LibrarySkill skill) => ContentHash(skill.Description, skill.Body, skill.DefaultFor);

    public static string ContentHash(string description, string body, IReadOnlyList<string>? defaultFor)
    {
        var text = string.Join("\n",
            (description ?? "").Trim(),
            (body ?? "").Replace("\r\n", "\n").Trim(),
            string.Join(",", (defaultFor ?? []).Order(StringComparer.Ordinal)));
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>Reads one SKILL.md: a frontmatter of name, version, description and defaultFor, then the instructions.</summary>
    /// <exception cref="FormatException">The text is not such a file.</exception>
    public static BuiltInSkill Parse(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length < 2 || lines[0] != "---")
            throw new FormatException("A built-in skill starts with a '---' frontmatter line.");

        var end = Array.IndexOf(lines, "---", 1);
        if (end < 0)
            throw new FormatException("A built-in skill's frontmatter has no closing '---' line.");

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines[1..end])
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
                throw new FormatException($"Frontmatter line \"{line}\" is not \"key: value\".");
            fields[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        string Field(string key) => fields.TryGetValue(key, out var value) && value.Length > 0
            ? value
            : throw new FormatException($"A built-in skill's frontmatter needs \"{key}\".");

        if (!int.TryParse(Field("version"), NumberStyles.None, CultureInfo.InvariantCulture, out var version) || version < 1)
            throw new FormatException("A built-in skill's version is a whole number from 1.");

        var defaults = fields.TryGetValue("defaultFor", out var types)
            ? types.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];
        var body = string.Join("\n", lines[(end + 1)..]).Trim();
        return new BuiltInSkill(Field("name"), version, Field("description"), body, [.. Lifecycle.TaskTypes.All.Where(defaults.Contains)]);
    }

    private static IReadOnlyList<BuiltInSkill> Load()
    {
        var assembly = typeof(BuiltInSkills).Assembly;
        return
        [
            .. assembly.GetManifestResourceNames()
                .Where(n => n.Contains(ResourceInfix, StringComparison.Ordinal) && n.EndsWith(ResourceSuffix, StringComparison.Ordinal))
                .Select(n =>
                {
                    using var reader = new StreamReader(assembly.GetManifestResourceStream(n)!);
                    return Parse(reader.ReadToEnd());
                })
                .OrderBy(s => s.Name, StringComparer.Ordinal),
        ];
    }
}
