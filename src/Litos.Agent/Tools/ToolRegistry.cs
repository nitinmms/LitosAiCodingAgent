namespace Litos.Agent.Tools;

public sealed class ToolRegistry(IEnumerable<ITool> tools)
{
    private readonly Dictionary<string, ITool> _byName = tools.ToDictionary(t => t.Name);

    public IReadOnlyList<ToolSchema> Schemas { get; } =
        [.. tools.Select(t => new ToolSchema(t.Name, t.Description, t.ParameterSchema))];

    public ITool Resolve(string name) =>
        _byName.TryGetValue(name, out var tool)
            ? tool
            : throw new ToolInvocationException($"Unknown tool '{name}'.");
}
