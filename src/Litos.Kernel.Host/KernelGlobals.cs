using System.Collections;
using Microsoft.CodeAnalysis.Scripting;

namespace Litos.Kernel.Host;

/// <summary>
/// The Roslyn scripting "globals" object — its public members are what a kernel-mode script sees
/// as top-level names (SCRATCH_DIR, KernelState, ...). ToolBridge injects the per-tool wrapper
/// methods as additional top-level names via a second globals mechanism (see ScriptSession) since
/// C# scripting globals must be known at ScriptOptions build time, not appended dynamically —
/// KernelGlobals itself only carries the fixed, always-present surface (§4.1, §4.5).
/// </summary>
public sealed class KernelGlobals(string scratchDirectory, KernelStateReflector stateReflector)
{
    public string SCRATCH_DIR { get; } = scratchDirectory;

    public KernelStateApi KernelState { get; } = new(stateReflector);
}

/// <summary>
/// KernelState.List()/Describe(name) — reflects on whatever the script's own locals already are
/// via the persisted ScriptState's variable list plus the FunctionRegistry, rather than requiring
/// the model to declare variables through a special API (§4.1). Supplementary to StateDelta: useful
/// for re-orienting on everything built so far in a long session, not just what changed last round.
/// </summary>
public sealed class KernelStateApi(KernelStateReflector reflector)
{
    public IReadOnlyList<string> List() => reflector.ListAll();

    public string Describe(string name) => reflector.Describe(name);
}

/// <summary>
/// Deferred binding: KernelStateApi is constructed once (baked into KernelGlobals at ScriptOptions
/// build time), but the live ScriptState/FunctionRegistry it needs to reflect on don't exist until
/// after the first eval runs. ScriptSession sets Current before returning control to the script.
/// </summary>
public sealed class KernelStateReflector
{
    public ScriptState<object>? Current { get; set; }
    public FunctionRegistry? Functions { get; set; }

    public IReadOnlyList<string> ListAll()
    {
        var lines = new List<string>();
        if (Current is not null)
            foreach (var v in Current.Variables)
                lines.Add($"variable {v.Name} : {DescribeType(v.Type)}{DescribeSize(v.Value)}");
        if (Functions is not null)
            foreach (var f in Functions.Functions.Values)
                lines.Add($"function {f.Signature}");
        return lines;
    }

    public string Describe(string name)
    {
        if (Current is not null)
        {
            var variable = Current.Variables.FirstOrDefault(v => v.Name == name);
            if (variable.Name is not null && variable.Name == name)
                return $"variable {variable.Name} : {DescribeType(variable.Type)}{DescribeSize(variable.Value)}";
        }
        if (Functions is not null && Functions.Functions.TryGetValue(name, out var fn))
            return $"function {fn.Signature}" + (fn.DocComment is null ? "" : $"\n{fn.DocComment}");
        return $"'{name}' is not a known kernel variable or function.";
    }

    private static string DescribeType(Type type) => type.Name;

    private static string DescribeSize(object? value)
    {
        if (value is string s)
            return $" (string, {s.Length} chars)";
        if (value is ICollection c)
            return $" (~{c.Count} items)";
        if (value is IEnumerable and not string)
            return " (IEnumerable, count not cheaply computable)";
        return "";
    }
}
