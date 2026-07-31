namespace Litos.Tools.ProjectInstructions;

public interface IProjectInstructionsDiscovery
{
    Task<IReadOnlyList<ProjectInstructionsFile>> DiscoverAsync(CancellationToken ct);
}
