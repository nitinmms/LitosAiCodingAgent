using System.Text.Json;
using Litos.SoftwareFactory.Contracts;
using Litos.Tools.Skills;

namespace Litos.SoftwareFactory.Worker;

/// <summary>
/// The worker's skills (m3-architecture.md §7.3): the factory library the host wrote for the run,
/// and the working copy's own <c>.litos/skills</c> — only those the host allowed, and only while
/// each folder still has the hash it was allowed at. A skill the agent writes or changes during
/// the run is therefore never offered. Nothing above the working copy and nothing under the user
/// profile is read, so a run never depends on whichever account started the host. A repository
/// skill replaces a library skill of the same name. The skills chosen for the task are not offered
/// at all, under either name: their whole text is already in the system prompt (m3-architecture.md §13).
/// </summary>
public sealed class FactorySkillDiscovery(RunSkills skills, string workingCopy) : ISkillDiscovery
{
    public RunSkills Skills => skills;

    /// <exception cref="WorkerOptionsException">The host's skills file cannot be read.</exception>
    public static FactorySkillDiscovery Load(string path, string workingCopy)
    {
        try
        {
            var skills = JsonSerializer.Deserialize<RunSkills>(File.ReadAllText(path), FactoryWire.Json)
                ?? throw new WorkerOptionsException($"The skills file '{path}' is empty.");
            return new FactorySkillDiscovery(skills, workingCopy);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new WorkerOptionsException($"The skills file '{path}' could not be read: {ex.Message}");
        }
    }

    public Task<IReadOnlyList<SkillMetadata>> DiscoverAsync(CancellationToken ct)
    {
        var byName = new Dictionary<string, SkillMetadata>(StringComparer.Ordinal);
        if (skills.LibraryRoot is { } library)
        {
            foreach (var skill in SkillDiscovery.ScanRoot(library))
                byName.TryAdd(skill.Name, skill);
        }

        if (skills.Repository.Count > 0)
        {
            var allowed = skills.Repository.Select(s => (s.Name, s.Hash)).ToHashSet();
            var fromRepository = new HashSet<string>(StringComparer.Ordinal);
            foreach (var skill in SkillDiscovery.ScanRoot(RepositorySkillFolder.In(workingCopy)).OrderBy(s => s.DirectoryPath, StringComparer.Ordinal))
            {
                if (!fromRepository.Contains(skill.Name) && allowed.Contains((skill.Name, HashOf(skill.DirectoryPath))))
                {
                    byName[skill.Name] = skill;
                    fromRepository.Add(skill.Name);
                }
            }
        }

        var chosen = skills.TaskSkills.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Task.FromResult<IReadOnlyList<SkillMetadata>>(
            [.. byName.Values.Where(s => !chosen.Contains(s.Name)).OrderBy(s => s.Name, StringComparer.Ordinal)]);
    }

    /// <summary>A folder that cannot be read just now — being written, say — matches no allowed hash.</summary>
    private static string HashOf(string directory)
    {
        try
        {
            return SkillHash.Of(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }
}
