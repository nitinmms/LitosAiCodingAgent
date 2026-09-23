namespace Litos.Kernel;

/// <summary>
/// Output-size ceilings enforced in code (Litos.Kernel.Host), not left to prompt guidance alone
/// — ReadMe_PTCPersistentKernel.md §8.2 "Output size is enforced in code, not left to prompt
/// guidance". Starting values, revisited once Milestone 1's benchmark data (§1.1) exists.
/// </summary>
public static class KernelLimits
{
    public const int MaxOutputBytes = 64 * 1024;
    public const int MaxReturnValueBytes = 32 * 1024;
    public const int MaxStateDeltaBytes = 4 * 1024;
    public const int MaxStateDeltaNames = 20;
    public const int MaxCombinedEvalResultBytes = 96 * 1024;
    public const int MaxToolCallResponseBytes = 32 * 1024;
    public const int MaxNestedToolCallsPerEval = 100;

    /// <summary>Element cap when serializing an IEnumerable return value — never auto-enumerated to completion (§8.2).</summary>
    public const int MaxEnumerableElements = 100;
}
