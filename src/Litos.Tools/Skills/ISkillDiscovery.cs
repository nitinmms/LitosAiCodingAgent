namespace Litos.Tools.Skills;

public interface ISkillDiscovery
{
    Task<IReadOnlyList<SkillMetadata>> DiscoverAsync(CancellationToken ct);
}
