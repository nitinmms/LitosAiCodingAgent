using Litos.Agent.Messages;
using Litos.Agent.Providers;
using Litos.Agent.Streaming;
using Litos.Agent.Tools;

namespace Litos.VsCodeHost.Tests.Fakes;

/// <summary>
/// Local copy of Litos.Api.Tests/Fakes/FakeChatProvider.cs — see that file's own note on why each
/// test project keeps its own fakes rather than sharing a cross-project reference.
/// </summary>
public sealed class FakeChatProvider : IChatProvider
{
    private readonly Queue<ScriptedResponse> _responses = new();

    public string ProviderName => "fake";

    public List<IReadOnlyList<ToolSchema>> ReceivedToolLists { get; } = [];

    public List<IReadOnlyList<ChatMessage>> ReceivedMessageLists { get; } = [];

    public IReadOnlyList<ModelInfo> ModelsToReturn { get; set; } =
        [new ModelInfo("fake-model", "Fake Model", IsDefault: true)];

    public void Enqueue(params AgentEvent[] events) => _responses.Enqueue(new ScriptedResponse(events, null));

    public void EnqueueAwaiting(Task gate, params AgentEvent[] events) => _responses.Enqueue(new ScriptedResponse(events, gate));

    public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct) =>
        Task.FromResult(ModelsToReturn);

    public async IAsyncEnumerable<AgentEvent> StreamAsync(
        ChatRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        ReceivedToolLists.Add(request.Tools);
        ReceivedMessageLists.Add(request.Messages);

        var response = _responses.Count > 0
            ? _responses.Dequeue()
            : new ScriptedResponse([new TextDelta("ok"), new MessageCompleted(ChatMessage.Assistant([new TextBlock("ok")]), new UsageInfo(1, 1))], null);

        if (response.Gate is not null)
            await response.Gate;

        foreach (var evt in response.Events)
        {
            ct.ThrowIfCancellationRequested();
            yield return evt;
            await Task.Yield();
        }
    }

    private sealed record ScriptedResponse(AgentEvent[] Events, Task? Gate);
}
