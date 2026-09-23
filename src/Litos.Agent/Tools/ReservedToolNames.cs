namespace Litos.Agent.Tools;

/// <summary>
/// Tool names AgentLoop intercepts by name before ToolRegistry.Resolve is ever reached, routing to
/// a different execution path entirely. Lives in Litos.Agent (not Litos.Kernel) since AgentLoop is
/// the thing switching on this and must not need a Litos.Kernel reference to do so — Litos.Kernel
/// depends on Litos.Agent, never the reverse (ReadMe_PTCPersistentKernel.md §8.2).
/// </summary>
public static class ReservedToolNames
{
    public const string KernelCode = "run_kernel_code";
}
