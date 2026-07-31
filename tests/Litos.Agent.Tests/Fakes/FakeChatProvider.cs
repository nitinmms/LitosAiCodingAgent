using Litos.Agent.Providers;
using Litos.Agent.Streaming;

namespace Litos.Agent.Tests.Fakes;

/// <summary>
/// Scriptable IChatProvider. AgentLoop calls StreamAsync once per round of its
/// tool-calling while(true) loop, and Compactor calls it once more for summarization —
/// so responses are served from a queue, one per call, with an empty response repeated if the
/// queue runs out (convenient for tests that don't care how many rounds happen).
/// Records every ChatRequest it received so tests can assert what AgentLoop/Compactor sent.
/// </summary>
public sealed class FakeChatProvider : IChatProvider
{
    private readonly Queue<ScriptedResponse> _responses = new();

    public string ProviderName => "fake";

    public List<ChatRequest> ReceivedRequests { get; } = [];

    public IReadOnlyList<ModelInfo> ModelsToReturn { get; set; } = [];

    /// <summary>Queues a fixed sequence of events, ignoring the request.</summary>
    public void Enqueue(params AgentEvent[] events) => _responses.Enqueue(new ScriptedResponse(events, Throw: null, Hangs: false));

    /// <summary>Queues a response that throws instead of streaming anything.</summary>
    public void EnqueueThrow(Exception exception) => _responses.Enqueue(new ScriptedResponse([], exception, Hangs: false));

    /// <summary>
    /// Queues a response that yields <paramref name="leadingEvents"/> (if any) and then never
    /// yields anything further and never completes — simulates a provider connection that
    /// accepted the request, optionally streamed a bit, and then went silent forever, for
    /// testing AgentLoop's idle-gap stream timeout without a real stalled network call.
    /// </summary>
    public void EnqueueHang(params AgentEvent[] leadingEvents) => _responses.Enqueue(new ScriptedResponse(leadingEvents, Throw: null, Hangs: true));

    public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct) =>
        Task.FromResult(ModelsToReturn);

    public async IAsyncEnumerable<AgentEvent> StreamAsync(ChatRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        ReceivedRequests.Add(request);

        var response = _responses.Count > 0 ? _responses.Dequeue() : new ScriptedResponse([], Throw: null, Hangs: false);
        if (response.Throw is not null)
            throw response.Throw;

        foreach (var evt in response.Events)
        {
            ct.ThrowIfCancellationRequested();
            yield return evt;
            await Task.Yield();
        }

        if (response.Hangs)
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
    }

    private sealed record ScriptedResponse(AgentEvent[] Events, Exception? Throw, bool Hangs);
}
