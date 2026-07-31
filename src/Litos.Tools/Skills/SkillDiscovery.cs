namespace Litos.Tools.Skills;

public sealed class SkillDiscovery(string? startDirectory = null) : ISkillDiscovery
{
    public Task<IReadOnlyList<SkillMetadata>> DiscoverAsync(CancellationToken ct)
    {
        var byName = new Dictionary<string, SkillMetadata>();
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // User-global .litos first, so project-level skills can override on name collision.
        var userSkillsRoot = Path.Combine(userProfile, ".litos", "skills");
        foreach (var skill in ScanRoot(userSkillsRoot))
            byName[skill.Name] = skill;

        // User-global Claude Code skills fill gaps the .litos roots don't cover: scanned after
        // .litos-global (so .litos wins on collision) but before the project walk (so any
        // project-level .litos skill still wins too). Global-only, matching Claude Code's own
        // ~/.claude/skills/ convention — no per-project .claude/skills/ walk.
        var claudeSkillsRoot = Path.Combine(userProfile, ".claude", "skills");
        foreach (var skill in ScanRoot(claudeSkillsRoot))
            if (!byName.ContainsKey(skill.Name))
                byName[skill.Name] = skill;

        foreach (var projectRoot in FindProjectSkillRoots())
            foreach (var skill in ScanRoot(projectRoot))
                byName[skill.Name] = skill;

        return Task.FromResult<IReadOnlyList<SkillMetadata>>([.. byName.Values]);
    }

    /// <summary>
    /// Walks up from the start directory looking for .litos/skills/ folders, like a
    /// .gitignore search — every ancestor's .litos/skills/ counts as project-level,
    /// closer directories processed last so they win on collision.
    /// </summary>
    private IEnumerable<string> FindProjectSkillRoots()
    {
        var roots = new List<string>();
        var current = new DirectoryInfo(startDirectory ?? Directory.GetCurrentDirectory());
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, ".litos", "skills");
            if (Directory.Exists(candidate))
                roots.Add(candidate);
            current = current.Parent;
        }

        roots.Reverse();
        return roots;
    }

    private static IEnumerable<SkillMetadata> ScanRoot(string root)
    {
        if (!Directory.Exists(root))
            yield break;

        foreach (var skillDirectory in Directory.EnumerateDirectories(root))
        {
            var skillMdPath = Path.Combine(skillDirectory, "SKILL.md");
            if (!File.Exists(skillMdPath))
                continue;

            var (fields, _) = SkillFrontmatter.Parse(File.ReadAllText(skillMdPath));
            if (!fields.TryGetValue("name", out var name) || !fields.TryGetValue("description", out var description))
                continue;

            yield return new SkillMetadata(name, description, skillDirectory);
        }
    }
}
