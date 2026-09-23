using System.Text.Json;
using Litos.Kernel;

namespace Litos.Kernel.Host;

/// <summary>
/// A convenience callable surface for bridged tools, not a gate (§4.1/§5.1) — this class only
/// performs the ToolCallRequest/ToolCallResponse round-trip over stdio; ScriptSession's caller
/// (KernelSession, in Litos.Kernel) is what actually resolves and invokes the real ITool, ungated.
/// One instance per subprocess lifetime; RequestId is a per-call GUID so concurrent-looking script
/// code (unlikely in v1's synchronous eval model, but cheap to support) can't cross-match responses.
/// </summary>
public sealed class ToolBridge(TextWriter protocolOut, SemaphoreSlim protocolWriteLock)
{
    private readonly Dictionary<string, TaskCompletionSource<ToolCallResponse>> _pending = [];
    private readonly Lock _pendingLock = new();
    private int _nestedCallsThisEval;

    /// <summary>Called by ScriptSession before each eval so the per-eval nested-call cap (§8.2) resets.</summary>
    public void ResetEvalBudget() => _nestedCallsThisEval = 0;

    /// <summary>
    /// Invoked from generated wrapper functions inside the script's own namespace — see
    /// ScriptOptions construction in ScriptSession, which emits one `Task&lt;string&gt; {name}(string
    /// argsJson)` per bridged tool that calls this with the tool's name.
    /// </summary>
    public async Task<string> CallAsync(string toolName, string argsJson)
    {
        if (Interlocked.Increment(ref _nestedCallsThisEval) > KernelLimits.MaxNestedToolCallsPerEval)
            throw new InvalidOperationException(
                $"Exceeded the maximum of {KernelLimits.MaxNestedToolCallsPerEval} tool calls in a single eval.");

        var requestId = Guid.NewGuid().ToString("n");
        var tcs = new TaskCompletionSource<ToolCallResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingLock)
            _pending[requestId] = tcs;

        var arguments = JsonSerializer.Deserialize<JsonElement>(string.IsNullOrEmpty(argsJson) ? "{}" : argsJson);
        var request = KernelWireMessage.Of(new ToolCallRequest(requestId, toolName, arguments));

        await protocolWriteLock.WaitAsync();
        try
        {
            await WireIo.WriteAsync(protocolOut, request, CancellationToken.None);
        }
        finally
        {
            protocolWriteLock.Release();
        }

        var response = await tcs.Task;
        if (response.IsError)
            throw new InvalidOperationException(response.Text);
        return response.Text;
    }

    /// <summary>Called by RunLoop's dispatch loop when a ToolCallResponse line arrives, to unblock the awaiting CallAsync.</summary>
    public void Complete(ToolCallResponse response)
    {
        TaskCompletionSource<ToolCallResponse>? tcs;
        lock (_pendingLock)
        {
            if (!_pending.Remove(response.RequestId, out tcs))
                return; // Stale/duplicate response — nothing awaiting it (already timed out, or a protocol bug); ignored rather than crashing the host.
        }
        tcs.SetResult(response);
    }
}
