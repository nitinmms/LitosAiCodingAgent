using Litos.Agent.Tools;

namespace Litos.Agent.Tests.Fakes;

/// <summary>Mutable IToolSource for tests — CurrentTools is a plain settable property so a test
/// can simulate a tool appearing/disappearing between two ToolRegistryFactory.Create() calls.</summary>
public sealed class FakeToolSource : IToolSource
{
    public IReadOnlyList<ITool> CurrentTools { get; set; } = [];
}
