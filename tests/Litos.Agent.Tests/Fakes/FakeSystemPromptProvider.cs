using Litos.Agent.Session;
using Litos.Agent.Tools;

namespace Litos.Agent.Tests.Fakes;

/// <summary>Returns a fixed (possibly null) prompt and records how it was called.</summary>
public sealed class FakeSystemPromptProvider(string? promptToReturn) : ISystemPromptProvider
{
    public List<string?> ReceivedWorkingDirectories { get; } = [];

    public Task<string?> BuildAsync(ToolRegistry tools, string? workingDirectory, CancellationToken ct)
    {
        ReceivedWorkingDirectories.Add(workingDirectory);
        return Task.FromResult(promptToReturn);
    }
}
