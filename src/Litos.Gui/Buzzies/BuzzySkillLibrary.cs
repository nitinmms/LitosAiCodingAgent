using System.Text;
using Litos.Gui.Buzzies.Workflows;

namespace Litos.Gui.Buzzies;

/// <summary>One editable skill file: its folder name, the frontmatter's name and description, and
/// the Markdown body the skill tool hands the model.</summary>
public sealed record BuzzySkillFile(string Folder, string Name, string Description, string Body)
{
    /// <summary>The whole SKILL.md text, as it is written to disk.</summary>
    public string ToMarkdown() =>
        $"---\nname: {Name}\ndescription: {Description}\n---\n\n{Body.Trim()}\n";
}

/// <summary>
/// A Buzzy's own skills, read and written inside the app rather than in an external editor
/// (Implement_Buzzies.md, Phase 3). They are the ordinary <c>skills/&lt;name&gt;/SKILL.md</c> files
/// that the skill tool and the prompt's catalog read, so editing one here changes exactly what the
/// Buzzy sees on its next turn, with no second copy to drift.
///
/// Versions are deliberately not touched: an edited SKILL.md is the newest version for the skill
/// tool, while <c>versions/vN.md</c> under a workflow's folder stays as it was, so a routine pinned
/// to an earlier version keeps getting the text it was approved with. Editing is not a re-approval.
/// </summary>
public sealed class BuzzySkillLibrary(BuzzyStore store)
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public string RootOf(string buzzyId) => store.SkillsOf(buzzyId);

    /// <summary>Every skill folder that has a SKILL.md, in folder order, with its frontmatter read
    /// as written. A file that won't parse still lists, by folder name, so it can be repaired here
    /// rather than only on disk.</summary>
    public IReadOnlyList<BuzzySkillFile> List(string buzzyId)
    {
        var root = RootOf(buzzyId);
        if (!Directory.Exists(root))
            return [];

        return [.. Directory.EnumerateDirectories(root)
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(dir => Path.GetFileName(dir)!)
            .Where(folder => File.Exists(Path.Combine(root, folder, "SKILL.md")))
            .Select(folder => Read(buzzyId, folder))
            .OfType<BuzzySkillFile>()];
    }

    public BuzzySkillFile? Read(string buzzyId, string folder)
    {
        if (!IsSafeFolder(folder))
            return null;
        var path = Path.Combine(RootOf(buzzyId), folder, "SKILL.md");
        if (!File.Exists(path))
            return null;

        var (fields, body) = WorkflowMarkdown.SplitFrontmatter(File.ReadAllText(path));
        var name = fields.TryGetValue("name", out var n) && !string.IsNullOrWhiteSpace(n) ? n.Trim() : folder;
        var description = fields.TryGetValue("description", out var d) ? d.Trim() : "";
        return new BuzzySkillFile(folder, name, description, body);
    }

    /// <summary>Writes a skill, creating its folder when new. Throws <see cref="ArgumentException"/>
    /// for a folder or name that can't be one, so the window can show why.</summary>
    public void Save(string buzzyId, BuzzySkillFile skill)
    {
        if (!IsSafeFolder(skill.Folder))
            throw new ArgumentException("A skill's folder must be lower-case letters, digits and hyphens.", nameof(skill));
        if (string.IsNullOrWhiteSpace(skill.Name) || skill.Name.Contains('\n'))
            throw new ArgumentException("A skill needs a one-line name.", nameof(skill));
        if (string.IsNullOrWhiteSpace(skill.Body))
            throw new ArgumentException("A skill needs a description of what to do.", nameof(skill));

        var folder = Path.Combine(RootOf(buzzyId), skill.Folder);
        Directory.CreateDirectory(folder);
        var cleaned = skill with { Description = OneLine(skill.Description), Name = skill.Name.Trim() };
        File.WriteAllText(Path.Combine(folder, "SKILL.md"), cleaned.ToMarkdown(), Utf8NoBom);
    }

    /// <summary>The slug a new skill's folder would use, from what the user typed as its name.</summary>
    public static string FolderFor(string name) => WorkflowStore.NameFrom(name);

    /// <summary>Lower-case letters, digits and single hyphens, the same shape
    /// <see cref="WorkflowStore.IsValidName"/> accepts, so a skill made here is one the skill tool
    /// and the prompt's catalog both find.</summary>
    public static bool IsSafeFolder(string folder) => WorkflowStore.IsValidName(folder);

    /// <summary>One line, so the frontmatter can't be broken by a newline or a stray "---".</summary>
    private static string OneLine(string text) =>
        text.ReplaceLineEndings(" ").Replace("---", "—").Trim();
}