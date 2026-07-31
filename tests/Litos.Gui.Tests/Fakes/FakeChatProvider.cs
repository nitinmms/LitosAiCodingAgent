using Litos.Agent.Messages;
using Litos.Agent.Providers;
using Litos.Agent.Streaming;
using Litos.Agent.Tools;

namespace Litos.Gui.Tests.Fakes;

/// <summary>Minimal scriptable IChatProvider — local copy of the same-shaped fake other test
/// projects keep (e.g. Litos.Api.Tests/Fakes/FakeChatProvider.cs); no cross-test-project
/// reference exists in this repo.</summary>
public sealed class FakeChatProvider : IChatProvider
{
    public string ProviderName => "fake";

    public List<IReadOnlyList<ToolSchema>> ReceivedToolLists { get; } = [];

    public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ModelInfo>>([new ModelInfo("fake-model", "Fake Model", IsDefault: true)]);

    public async IAsyncEnumerable<AgentEvent> StreamAsync(
        ChatRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        ReceivedToolLists.Add(request.Tools);
        yield return new TextDelta("ok");
        yield return new MessageCompleted(ChatMessage.Assistant([new TextBlock("ok")]), new UsageInfo(1, 1));
        await Task.CompletedTask;
    }
}
