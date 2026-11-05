using Litos.Agent.Tools;
using Litos.Tools.Skills;

namespace Litos.Artifacts;

public static class ArtifactsServiceCollectionExtensions
{
    /// <summary>
    /// Registers the artifact store and tool, installs the built-in artifact-design skill, and
    /// replaces the ISkillDiscovery AddLitosAgent registered with one that also searches the
    /// built-in skill root — so the system prompt's skill catalog and the model's skill tool both
    /// see the skill. Call after AddLitosAgent: the last ISkillDiscovery registration is the one
    /// resolved. The host must also map <see cref="ArtifactEndpoints.MapArtifactEndpoints"/>.
    /// </summary>
    /// <param name="baseUrl">The host's own base URL, read lazily per tool call — see ArtifactTool.</param>
    /// <param name="artifactsRoot">Null for ~/.litos/artifacts; tests pass a temp directory.</param>
    /// <param name="builtInSkillsRoot">Null for ~/.litos/builtin-skills; tests pass a temp directory.</param>
    public static IServiceCollection AddLitosArtifacts(
        this IServiceCollection services, Func<string> baseUrl, string? artifactsRoot = null, string? builtInSkillsRoot = null)
    {
        ArtifactSkills.EnsureInstalled(builtInSkillsRoot);

        services.AddSingleton(_ => new ArtifactStore(artifactsRoot));
        services.AddSingleton<ITool>(sp => new ArtifactTool(sp.GetRequiredService<ArtifactStore>(), baseUrl));
        services.AddSingleton<ISkillDiscovery>(_ => new SkillDiscovery(userRoots: ArtifactSkills.UserRoots(builtInSkillsRoot)));
        return services;
    }
}
