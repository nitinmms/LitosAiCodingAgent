using Litos.Tools.ProjectInstructions;

namespace Litos.Host.Tests.Fakes;

public sealed class FakeProjectInstructionsDiscovery(IReadOnlyList<ProjectInstructionsFile> files) : IProjectInstructionsDiscovery
{
    public Task<IReadOnlyList<ProjectInstructionsFile>> DiscoverAsync(CancellationToken ct) => Task.FromResult(files);
}
