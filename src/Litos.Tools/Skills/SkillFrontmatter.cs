namespace Litos.Tools.Skills;

/// <summary>
/// Parses only the flat "name: value" YAML frontmatter block a SKILL.md is expected to
/// have (name, description) — not a general YAML parser, since that's all this format needs.
/// </summary>
internal static class SkillFrontmatter
{
    public static (IReadOnlyDictionary<string, string> Fields, string Body) Parse(string content)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---")
            return (new Dictionary<string, string>(), content);

        var fields = new Dictionary<string, string>();
        var endIndex = -1;
        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].Trim() == "---")
            {
                endIndex = i;
                break;
            }

            var separatorIndex = lines[i].IndexOf(':');
            if (separatorIndex < 0)
                continue;

            var key = lines[i][..separatorIndex].Trim();
            var value = lines[i][(separatorIndex + 1)..].Trim();
            fields[key] = value;
        }

        if (endIndex < 0)
            return (new Dictionary<string, string>(), content);

        var body = string.Join('\n', lines.Skip(endIndex + 1)).TrimStart('\n');
        return (fields, body);
    }
}
