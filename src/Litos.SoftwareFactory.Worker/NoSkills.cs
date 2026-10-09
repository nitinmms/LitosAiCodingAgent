using Litos.Tools.Skills;

namespace Litos.SoftwareFactory.Worker;

/// <summary>
/// The worker's skills: none, until the factory's skill library and its approval of repository
/// skills exist (m3-architecture.md §7.3). The engine's discovery walked up from the working copy,
/// so every <c>.litos/skills</c> folder in the repository, or in any folder above the factory's
/// data directory, was advertised in the system prompt of every turn, unapproved, while the
/// <c>skill</c> tool was in no tool set.
/// </summary>
public sealed class NoSkills : ISkillDiscovery
{
    public Task<IReadOnlyList<SkillMetadata>> DiscoverAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<SkillMetadata>>([]);
}
