using System.Globalization;
using System.Text;

namespace Litos.Gui.Buzzies.Workflows;

/// <summary>One saved version of a workflow skill.</summary>
public sealed record WorkflowVersion(string Name, int Version, string Description, string Body, DateTimeOffset SavedAt)
{
    /// <summary>The whole SKILL.md text for this version, frontmatter included.</summary>
    public string ToSkillMarkdown() =>
        $"---\nname: {Name}\ndescription: {Description}\nversion: {Version}\nsaved: {SavedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}\n---\n\n{Body.Trim()}\n";
}

/// <summary>A workflow and its newest version.</summary>
public sealed record WorkflowSummary(string Name, string Description, int LatestVersion);

/// <summary>
/// "Remember This Workflow" storage (Implement_Buzzies.md, Phase 2): a Buzzy's workflows are
/// ordinary skills in its own <c>skills/</c> folder, so the skill tool and the prompt's skill
/// catalog find them with no special casing. Each save is a new version: <c>skills/&lt;name&gt;/SKILL.md</c>
/// always holds the newest, and <c>skills/&lt;name&gt;/versions/vN.md</c> keeps every version, so a
/// routine can stay on the version it was approved with until the user moves it.
/// </summary>
public sealed class WorkflowStore(BuzzyStore store)
{
    private const string VersionsFolder = "versions";
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public string FolderOf(string buzzyId, string name) => Path.Combine(store.SkillsOf(buzzyId), name);

    /// <summary>Saves a new version and returns it: v1 for a new name, otherwise one past the newest.</summary>
    public WorkflowVersion Save(string buzzyId, string name, string description, string body, DateTimeOffset now)
    {
        if (!IsValidName(name))
            throw new ArgumentException($"'{name}' isn't a valid workflow name: use lower-case letters, digits and hyphens.", nameof(name));

        var version = new WorkflowVersion(name, LatestVersion(buzzyId, name) + 1, OneLine(description), body, now);
        var folder = FolderOf(buzzyId, name);
        Directory.CreateDirectory(Path.Combine(folder, VersionsFolder));
        var text = version.ToSkillMarkdown();
        File.WriteAllText(Path.Combine(folder, VersionsFolder, $"v{version.Version}.md"), text, Utf8NoBom);
        File.WriteAllText(Path.Combine(folder, "SKILL.md"), text, Utf8NoBom);
        return version;
    }

    /// <summary>The newest version number, or 0 when the workflow doesn't exist.</summary>
    public int LatestVersion(string buzzyId, string name)
    {
        var versions = Path.Combine(FolderOf(buzzyId, name), VersionsFolder);
        if (!IsValidName(name) || !Directory.Exists(versions))
            return 0;
        return Directory.EnumerateFiles(versions, "v*.md")
            .Select(f => int.TryParse(Path.GetFileNameWithoutExtension(f)[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0)
            .DefaultIfEmpty(0)
            .Max();
    }

    /// <summary>A specific version, or null if it isn't there (deleted by hand, or never saved).</summary>
    public WorkflowVersion? Read(string buzzyId, string name, int version)
    {
        if (!IsValidName(name) || version < 1)
            return null;
        var path = Path.Combine(FolderOf(buzzyId, name), VersionsFolder, $"v{version}.md");
        return File.Exists(path) ? Parse(File.ReadAllText(path), name, version) : null;
    }

    /// <summary>Every workflow this Buzzy has saved through Remember This Workflow.</summary>
    public IReadOnlyList<WorkflowSummary> List(string buzzyId)
    {
        var root = store.SkillsOf(buzzyId);
        if (!Directory.Exists(root))
            return [];
        return [.. Directory.EnumerateDirectories(root)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(IsValidName)
            .Select(name => (Name: name, Latest: LatestVersion(buzzyId, name)))
            .Where(w => w.Latest > 0)
            .Select(w => new WorkflowSummary(w.Name, Read(buzzyId, w.Name, w.Latest)?.Description ?? "", w.Latest))
            .OrderBy(w => w.Name, StringComparer.Ordinal)];
    }

    public static bool IsValidName(string name) =>
        name.Length is > 0 and <= 48 && name[0] is >= 'a' and <= 'z'
        && name.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-') && !name.EndsWith('-');

    /// <summary>A safe workflow name from free text: "Weekly AI model report!" → "weekly-ai-model-report".</summary>
    public static string NameFrom(string text)
    {
        var slug = BuzzyStore.Slugify(text);
        if (slug.Length > 48)
            slug = slug[..48].TrimEnd('-');
        if (slug.Length == 0 || slug[0] is < 'a' or > 'z')
            slug = "workflow-" + slug;
        return slug.TrimEnd('-');
    }

    private static string OneLine(string text) => text.ReplaceLineEndings(" ").Replace("---", "—").Trim();

    private static WorkflowVersion Parse(string text, string name, int version)
    {
        var (fields, body) = WorkflowMarkdown.SplitFrontmatter(text);
        var saved = fields.TryGetValue("saved", out var s) && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            ? at
            : DateTimeOffset.MinValue;
        return new WorkflowVersion(name, version, fields.GetValueOrDefault("description", ""), body, saved);
    }
}

/// <summary>Frontmatter handling for workflow skills (Litos.Tools' own parser is internal there).</summary>
public static class WorkflowMarkdown
{
    /// <summary>The flat "key: value" frontmatter and the body after it; no frontmatter gives an
    /// empty dictionary and the whole text as body.</summary>
    public static (IReadOnlyDictionary<string, string> Fields, string Body) SplitFrontmatter(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (lines.Length == 0 || lines[0].Trim() != "---")
            return (fields, text.Trim());

        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].Trim() == "---")
                return (fields, string.Join('\n', lines.Skip(i + 1)).Trim());
            var colon = lines[i].IndexOf(':');
            if (colon > 0)
                fields[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
        }

        return (new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), text.Trim());
    }
}
