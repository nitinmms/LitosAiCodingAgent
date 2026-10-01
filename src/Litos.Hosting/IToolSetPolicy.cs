using Litos.Agent.Tools;

namespace Litos.Hosting;

/// <summary>
/// Decides which tools a turn may use. With PTC off the model sees this registry directly; with
/// PTC on the model sees only run_kernel_code and this registry is what kernel code can call —
/// AgentWorker applies that wrapping itself, so a policy never has to know about the toggle.
///
/// turnKind is an opaque label the host passed when starting the turn (null when it passed
/// none): Litos.VsCodeHost ignores it, while the software factory's worker uses it to give a
/// review turn read-only tools and an implement turn the full set.
/// </summary>
public interface IToolSetPolicy
{
    ToolRegistry Create(string sessionId, string? turnKind);
}

/// <summary>Every registered tool plus whatever the tool sources (MCP) currently offer, rebuilt
/// per turn — Litos.VsCodeHost's behaviour.</summary>
public sealed class DefaultToolSetPolicy(ToolRegistryFactory toolRegistryFactory) : IToolSetPolicy
{
    public ToolRegistry Create(string sessionId, string? turnKind) => toolRegistryFactory.Create();
}
