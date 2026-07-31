namespace Litos.Agent.Tools;

/// <summary>
/// A source of tools whose contents can change over time — e.g. tools discovered from external
/// servers that connect/disconnect/reconfigure at runtime. Litos.Agent knows nothing about what
/// backs an implementation; it only needs "ask me what you currently have" to be cheap,
/// synchronous, and safe to call once per turn. Implementations must never block or do I/O in
/// CurrentTools itself — any connect/reconnect/discovery work happens out-of-band (e.g. a
/// background service), and CurrentTools just returns whatever that work has already produced.
/// </summary>
public interface IToolSource
{
    IReadOnlyList<ITool> CurrentTools { get; }
}
