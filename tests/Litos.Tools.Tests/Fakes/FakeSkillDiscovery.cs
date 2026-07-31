using Litos.Tools.Skills;

namespace Litos.Tools.Tests.Fakes;

public sealed class FakeSkillDiscovery(IReadOnlyList<SkillMetadata> skills) : ISkillDiscovery
{
    public int CallCount { get; private set; }

    public Task<IReadOnlyList<SkillMetadata>> DiscoverAsync(CancellationToken ct)
    {
        CallCount++;
        return Task.FromResult(skills);
    }
}
