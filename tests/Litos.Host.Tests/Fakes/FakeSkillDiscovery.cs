using Litos.Tools.Skills;

namespace Litos.Host.Tests.Fakes;

public sealed class FakeSkillDiscovery(IReadOnlyList<SkillMetadata> skills) : ISkillDiscovery
{
    public Task<IReadOnlyList<SkillMetadata>> DiscoverAsync(CancellationToken ct) => Task.FromResult(skills);
}
