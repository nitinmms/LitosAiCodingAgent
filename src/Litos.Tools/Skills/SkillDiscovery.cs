namespace Litos.Tools.Skills;

/// <param name="startDirectory">Where the walk for project-level .litos/skills folders starts;
/// null uses the process's current directory.</param>
/// <param name="userRoots">The user-level skill folders, most authoritative first. Null uses the
/// two under the current user's profile (see <see cref="DefaultUserRoots"/>). A host that must not
/// depend on whichever account started it — the software factory's worker — passes its own list,
/// which may be empty.</param>
public sealed class SkillDiscovery(string? startDirectory = null, IReadOnlyList<string>? userRoots = null) : ISkillDiscovery
{
    /// <summary>
    /// ~/.litos/skills, then ~/.claude/skills. User-global Claude Code skills fill gaps the .litos
    /// root doesn't cover — global-only, matching Claude Code's own ~/.claude/skills/ convention,
    /// with no per-project .claude/skills/ walk.
    /// </summary>
    public static IReadOnlyList<string> DefaultUserRoots()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return [Path.Combine(userProfile, ".litos", "skills"), Path.Combine(userProfile, ".claude", "skills")];
    }

    public Task<IReadOnlyList<SkillMetadata>> DiscoverAsync(CancellationToken ct)
    {
        var byName = new Dictionary<string, SkillMetadata>();

        // User roots first, so project-level skills can override on name collision. Among the
        // user roots themselves an earlier root wins: a later one only fills names not yet taken.
        foreach (var userRoot in userRoots ?? DefaultUserRoots())
            foreach (var skill in ScanRoot(userRoot))
                byName.TryAdd(skill.Name, skill);

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
