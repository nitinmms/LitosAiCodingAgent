namespace Litos.Tools.ProjectInstructions;

/// <summary>
/// Discovers AGENTS.md/CLAUDE.md instruction files, mirroring SkillDiscovery's
/// walk-up pattern: one user-global file, plus one per ancestor directory from
/// the filesystem root down to the working directory. No override semantics —
/// callers concatenate everything, global first and cwd last, so the closest
/// file reads most saliently without suppressing the others.
/// </summary>
public sealed class ProjectInstructionsDiscovery(string? startDirectory = null) : IProjectInstructionsDiscovery
{
    private static readonly string[] FileNamesByPreference = ["AGENTS.md", "CLAUDE.md"];

    public Task<IReadOnlyList<ProjectInstructionsFile>> DiscoverAsync(CancellationToken ct)
    {
        var files = new List<ProjectInstructionsFile>();

        var userInstructionsRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".litos");
        if (TryFindFile(userInstructionsRoot, out var globalFile))
            files.Add(globalFile);

        foreach (var directory in FindAncestorDirectories())
            if (TryFindFile(directory, out var projectFile))
                files.Add(projectFile);

        return Task.FromResult<IReadOnlyList<ProjectInstructionsFile>>(files);
    }

    /// <summary>
    /// Root-most ancestor first, working directory last, like SkillDiscovery's
    /// .gitignore-style walk but reversed for concatenation order rather than override order.
    /// </summary>
    private IEnumerable<string> FindAncestorDirectories()
    {
        var directories = new List<string>();
        var current = new DirectoryInfo(startDirectory ?? Directory.GetCurrentDirectory());
        while (current is not null)
        {
            directories.Add(current.FullName);
            current = current.Parent;
        }

        directories.Reverse();
        return directories;
    }

    private static bool TryFindFile(string directory, out ProjectInstructionsFile file)
    {
        foreach (var fileName in FileNamesByPreference)
        {
            var candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate))
            {
                file = new ProjectInstructionsFile(candidate, File.ReadAllText(candidate));
                return true;
            }
        }

        file = null!;
        return false;
    }
}
