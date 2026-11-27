using System.Text;

namespace Litos.Office;

/// <summary>
/// Skill names as Litos.Gui's WorkflowStore requires them (lower-case letters, digits and hyphens,
/// starting with a letter, at most 48 characters, no trailing hyphen). Duplicated here so
/// Litos.Office stays independent of the Gui; Gui tests assert the two agree.
/// </summary>
public static class SkillNames
{
    public const int MaxLength = 48;

    public static bool IsValid(string? name) =>
        !string.IsNullOrEmpty(name)
        && name.Length <= MaxLength
        && name[0] is >= 'a' and <= 'z'
        && name[^1] != '-'
        && name.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    /// <summary>A valid name from free text ("Monthly Regional Sales!" → "monthly-regional-sales"),
    /// or <paramref name="fallback"/> when nothing usable is left.</summary>
    public static string From(string? text, string fallback = "learned-workflow")
    {
        var builder = new StringBuilder();
        foreach (var ch in (text ?? "").ToLowerInvariant())
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9')
                builder.Append(ch);
            else if (builder.Length > 0 && builder[^1] != '-')
                builder.Append('-');
        }

        var slug = builder.ToString().Trim('-');
        while (slug.Length > 0 && slug[0] is >= '0' and <= '9' or '-')
            slug = slug[1..];
        if (slug.Length > MaxLength)
            slug = slug[..MaxLength].TrimEnd('-');
        return IsValid(slug) ? slug : fallback;
    }
}
