using System.Security.Cryptography;
using System.Text;

namespace Litos.SoftwareFactory.Contracts;

/// <summary>
/// The skills a worker may use (m3-architecture.md §7.3), as the host wrote them for one run:
/// the factory library's folder, and the repository skills that may be used, each by the hash
/// it was allowed at. The worker reads the repository's own <c>.litos/skills</c> itself, and
/// only a skill whose folder still has that hash is used, so one changed during the run drops out.
/// </summary>
/// <param name="LibraryRoot">The materialized library; null when it has no enabled skill.</param>
public sealed record RunSkills(string? LibraryRoot, IReadOnlyList<AllowedRepositorySkill> Repository)
{
    /// <summary>The skills chosen for the task (m3-architecture.md §13): their whole text goes in
    /// every session's system prompt, which compaction never summarises, and they are not offered
    /// to load.</summary>
    public IReadOnlyList<TaskSkill> TaskSkills { get; init; } = [];

    /// <summary>Whether the run has any skill at all.</summary>
    public bool IsEmpty => !HasLoadableSkills && TaskSkills.Count == 0;

    /// <summary>Whether there are skills to load by name; without any, the skill tool is not offered.</summary>
    public bool HasLoadableSkills => LibraryRoot is not null || Repository.Count > 0;
}

public sealed record AllowedRepositorySkill(string Name, string Hash);

/// <summary>A skill chosen for a task, with the instructions the agent is given.</summary>
public sealed record TaskSkill(string Name, string Text);

/// <summary>Where a repository keeps its skills, relative to its root (blueprint §8.2).</summary>
public static class RepositorySkillFolder
{
    public static string In(string workingCopy) => Path.Combine(workingCopy, ".litos", "skills");
}

/// <summary>
/// The identity of a skill's folder: every file in it, by its path within the folder and its
/// bytes. An approval is of this hash, so changing any file — a helper script beside SKILL.md
/// included — makes a skill that must be approved again.
/// </summary>
public static class SkillHash
{
    public static string Of(string skillDirectory)
    {
        var root = Path.GetFullPath(skillDirectory);
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => (Path: path, Relative: Path.GetRelativePath(root, path).Replace('\\', '/')))
            .OrderBy(f => f.Relative, StringComparer.Ordinal);

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[8];
        foreach (var (path, relative) in files)
        {
            var name = Encoding.UTF8.GetBytes(relative);
            var content = File.ReadAllBytes(path);
            // Each part is preceded by its length, so no two folders can run together into one input.
            BitConverter.TryWriteBytes(length, (long)name.Length);
            sha.AppendData(length);
            sha.AppendData(name);
            BitConverter.TryWriteBytes(length, (long)content.Length);
            sha.AppendData(length);
            sha.AppendData(content);
        }

        return "sha256:" + Convert.ToHexStringLower(sha.GetHashAndReset());
    }
}
