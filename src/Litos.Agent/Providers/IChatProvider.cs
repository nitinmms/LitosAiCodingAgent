using Litos.Agent.Streaming;

namespace Litos.Agent.Providers;

public interface IChatProvider
{
    string ProviderName { get; }

    Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct);

    IAsyncEnumerable<AgentEvent> StreamAsync(ChatRequest request, CancellationToken ct);
}
