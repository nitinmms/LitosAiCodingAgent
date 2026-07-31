namespace Litos.Agent.Tools;

/// <summary>
/// Builds a fresh ToolRegistry snapshot on demand — the static tool set (built-in tools, fixed
/// for the process lifetime) plus every currently-available tool from each registered
/// IToolSource, evaluated at the instant Create() runs. Callers that need "next-turn-only"
/// visibility of a changing tool set (e.g. AgentWorker, once per turn) call Create() at the point
/// their own turn/request begins and hold onto the result for that turn's whole duration —
/// mirroring AgentLoopFactory.Create(provider), which the same call sites already call at the
/// same moment for the same reason. A turn already in progress keeps whatever ToolRegistry
/// instance it was handed; only a turn that calls Create() after a source's contents changed
/// observes the change.
/// </summary>
public sealed class ToolRegistryFactory(IEnumerable<ITool> staticTools, IEnumerable<IToolSource> sources)
{
    private readonly IReadOnlyList<ITool> _staticTools = [.. staticTools];
    private readonly IReadOnlyList<IToolSource> _sources = [.. sources];

    public ToolRegistry Create() =>
        new(_staticTools.Concat(_sources.SelectMany(s => s.CurrentTools)));
}
