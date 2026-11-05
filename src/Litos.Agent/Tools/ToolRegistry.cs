namespace Litos.Agent.Tools;

public sealed class ToolRegistry(IEnumerable<ITool> tools)
{
    private readonly Dictionary<string, ITool> _byName = tools.ToDictionary(t => t.Name);

    public IReadOnlyList<ToolSchema> Schemas { get; } =
        [.. tools.Select(t => new ToolSchema(t.Name, t.Description, t.ParameterSchema))];

    public ITool Resolve(string name) =>
        _byName.TryGetValue(name, out var tool)
            ? tool
            : throw new ToolInvocationException(UnknownToolMessage(name));

    /// <summary>
    /// In kernel mode the registry holds run_kernel_code alone, and every other tool is reached from
    /// inside a script — but a model that reads a tool's name in its brief or in run_kernel_code's
    /// own description often calls it directly, gets a bare "unknown tool", and gives up on it. Say
    /// how to call it instead, in the name-and-value form the kernel's wrappers take.
    /// </summary>
    private string UnknownToolMessage(string name) =>
        _byName.Count == 1 && _byName.ContainsKey(ReservedToolNames.KernelCode)
            ? $"Unknown tool '{name}': this session's only tool is {ReservedToolNames.KernelCode}. "
              + $"If '{name}' is listed in its description, call it from inside a script, with its arguments as name and value pairs, "
              + $"for example: await {name}(\"argument\", value);"
            : $"Unknown tool '{name}'.";
}
