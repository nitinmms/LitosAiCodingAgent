using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Litos.Agent.Providers;
using Litos.Agent.Streaming;
using Litos.SoftwareFactory.Contracts;

namespace Litos.SoftwareFactory.Worker;

/// <summary>The host refused the call before sending it: the task's allowance or the user's
/// quota does not cover it. Nothing was spent, and the run is already paused.</summary>
public sealed class GatewayRefusedException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>The call reached the gateway but did not produce a response: the provider failed,
/// or the stream broke before the message was complete.</summary>
public sealed class GatewayCallFailedException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The worker's only chat provider (ReadMe_LitosSoftwareFactory_V1.md §9.3): every model call —
/// turns and compaction alike — is posted to the factory host, which holds the provider keys,
/// admits and reserves the call against the task's budget, and streams the provider's events back
/// as NDJSON. The agent loop already sends every call through IChatProvider.StreamAsync, so
/// nothing in the loop knows the difference.
///
/// Failures are thrown, not yielded as ErrorOccurred events. The agent loop turns a thrown
/// exception into that event itself, while compaction — which ignores events it does not
/// recognise — would otherwise replace a session's history with an empty summary.
/// </summary>
public sealed class GatewayChatProvider(FactoryHostClient host, string providerName, string model, int? contextLength) : IChatProvider
{
    public string ProviderName => providerName;

    /// <summary>The one model fixed at launch; a worker has no catalog to choose from.</summary>
    public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ModelInfo>>([new ModelInfo(model, model, IsDefault: true, ContextLength: contextLength)]);

    public async IAsyncEnumerable<AgentEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        // A fresh key per call, so a request the host sees twice is charged once.
        var gatewayRequest = new GatewayRequest(Guid.NewGuid().ToString(), request);

        using var message = new HttpRequestMessage(HttpMethod.Post, FactoryWire.GatewayPath(host.RunId))
        {
            Content = JsonContent.Create(gatewayRequest, options: FactoryWire.Json),
        };

        HttpResponseMessage response;
        try
        {
            response = await host.Http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new GatewayCallFailedException($"The model gateway could not be reached: {ex.Message}", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new GatewayCallFailedException($"The model gateway refused the request (HTTP {(int)response.StatusCode}).");

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);
            var completed = false;

            while (await ReadLineAsync(reader, ct) is { } line)
            {
                if (line.Length == 0)
                    continue;

                switch (Parse(line))
                {
                    case GatewayTextDelta e:
                        yield return new TextDelta(e.Text);
                        break;
                    case GatewayReasoningDelta e:
                        yield return new ReasoningDelta(e.Text);
                        break;
                    case GatewayToolCallStarted e:
                        yield return new ToolCallStarted(e.CallId, e.ToolName);
                        break;
                    case GatewayToolCallArgsDelta e:
                        yield return new ToolCallArgsDelta(e.CallId, e.JsonFragment);
                        break;
                    case GatewayToolCallCompleted e:
                        yield return new ToolCallCompleted(e.CallId, e.ToolName, e.Arguments);
                        break;
                    case GatewayHeartbeat:
                        yield return new StreamHeartbeat();
                        break;
                    case GatewayMessageCompleted e:
                        completed = true;
                        yield return new MessageCompleted(e.Message, e.Usage);
                        break;
                    case GatewayError e when GatewayErrorCodes.IsRefusal(e.Code):
                        throw new GatewayRefusedException(e.Code, e.Message);
                    case GatewayError e:
                        throw new GatewayCallFailedException(e.Message);
                }
            }

            // A stream that simply stops is a broken connection, not an empty reply.
            if (!completed)
                throw new GatewayCallFailedException("The model gateway's response ended before the message was complete.");
        }
    }

    private static async Task<string?> ReadLineAsync(StreamReader reader, CancellationToken ct)
    {
        try
        {
            return await reader.ReadLineAsync(ct);
        }
        catch (IOException ex)
        {
            throw new GatewayCallFailedException($"The model gateway's response was cut off: {ex.Message}", ex);
        }
    }

    private static GatewayEvent Parse(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<GatewayEvent>(line, FactoryWire.Json)
                ?? throw new GatewayCallFailedException("The model gateway sent an empty event.");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new GatewayCallFailedException($"The model gateway sent an event that could not be read: {ex.Message}", ex);
        }
    }
}

/// <summary>Whatever provider name the agent asks for, the answer is the gateway.</summary>
public sealed class GatewayChatProviderFactory(GatewayChatProvider provider) : IChatProviderFactory
{
    public IChatProvider Resolve(string providerName) => provider;
}
