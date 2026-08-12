using Litos.Agent.Messages;
using Litos.Agent.Providers;
using Litos.Agent.Streaming;
using Litos.Agent.Tools;

namespace Litos.Api.Tests.Fakes;

/// <summary>
/// Minimal scriptable IChatProvider for AgentWorker tests — local copy of
/// Litos.Agent.Tests/Fakes/FakeChatProvider.cs's shape (no cross-test-project reference exists
/// in this repo; each test project keeps its own fakes, e.g. FakeApprovalGate is duplicated
/// between Litos.Tools.Tests and Litos.Host.Tests). Trimmed to only what AgentWorker's own tests
/// need: a queued sequence of events per StreamAsync call, defaulting to a trivial
/// TextDelta+MessageCompleted reply so a turn can complete without every test scripting one.
/// </summary>
public sealed class FakeChatProvider : IChatProvider
{
    private readonly Queue<ScriptedResponse> _responses = new();

    public string ProviderName => "fake";

    /// <summary>Every ChatRequest.Tools list this provider has actually been asked to stream
    /// against, in call order — lets a test assert exactly which tools a given turn saw, e.g. to
    /// prove a source's newly-connected tool reached the NEXT turn but not one already running.</summary>
    public List<IReadOnlyList<ToolSchema>> ReceivedToolLists { get; } = [];

    /// <summary>Every ChatRequest.Messages list this provider has actually been asked to stream
    /// against, in call order — lets a test assert exactly what content a given turn saw, e.g. to
    /// prove content queued while a turn was busy reached the NEXT turn's request.</summary>
    public List<IReadOnlyList<ChatMessage>> ReceivedMessageLists { get; } = [];

    public IReadOnlyList<ModelInfo> ModelsToReturn { get; set; } =
        [new ModelInfo("fake-model", "Fake Model", IsDefault: true)];

    public void Enqueue(params AgentEvent[] events) => _responses.Enqueue(new ScriptedResponse(events, null));

    /// <summary>
    /// Queues a response that doesn't yield <paramref name="events"/> until
    /// <paramref name="gate"/> completes — lets a test deterministically observe "the turn has
    /// started but not yet finished" (e.g. AgentWorker.IsTurnActiveFor) before releasing it,
    /// instead of racing a fast in-memory fake to completion.
    /// </summary>
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
