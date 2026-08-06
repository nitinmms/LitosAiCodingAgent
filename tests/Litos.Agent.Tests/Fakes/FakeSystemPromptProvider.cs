using Litos.Agent.Session;
using Litos.Agent.Tools;

namespace Litos.Agent.Tests.Fakes;

/// <summary>Returns a fixed (possibly null) prompt and records how it was called.</summary>
public sealed class FakeSystemPromptProvider(string? promptToReturn) : ISystemPromptProvider
{
    public List<string?> ReceivedWorkingDirectories { get; } = [];

    public Task<SystemPromptSections?> BuildAsync(ToolRegistry tools, string? workingDirectory, CancellationToken ct)
    {
        ReceivedWorkingDirectories.Add(workingDirectory);
        var sections = promptToReturn is null ? null : new SystemPromptSections(promptToReturn, "", "", SkillsCatalog: null, [], "");
        return Task.FromResult(sections);
    }
}
