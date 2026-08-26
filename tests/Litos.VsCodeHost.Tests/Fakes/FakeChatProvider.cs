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

        // response.Gate is an arbitrary caller-supplied Task (typically a TaskCompletionSource
        // that's deliberately never released, to simulate a stalled provider/tool call) — it has
        // no cancellation wiring of its own, so awaiting it bare would ignore ct entirely and hang
        // forever even once the caller cancels. A real HTTP-backed provider's in-flight call would
        // actually observe cancellation; WaitAsync keeps this fake honest about that instead of
        // outliving the very token meant to stop it.
        if (response.Gate is not null)
            await response.Gate.WaitAsync(ct);

        foreach (var evt in response.Events)
        {
            ct.ThrowIfCancellationRequested();
            yield return evt;
            await Task.Yield();
        }
    }

    private sealed record ScriptedResponse(AgentEvent[] Events, Task? Gate);
}
