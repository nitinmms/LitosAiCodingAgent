using System.IO.Pipelines;
using Litos.Kernel;
using Litos.Kernel.Host;

namespace Litos.Kernel.Tests;

/// <summary>
/// Drives Litos.Kernel.Host's real RunLoop in-process over an in-memory duplex pipe pair, instead
/// of spawning the actual subprocess — exercises the real protocol/eval/StateDelta/stdout-capture
/// logic without process-spawn flakiness or a build/publish step in the test run. KernelSession's
/// own process-spawn/tree-kill/handshake plumbing is a separate, thinner layer covered by its own
/// tests; this fixture is about ScriptSession/RunLoop correctness.
/// </summary>
public sealed class InProcessKernelHostFixture : IAsyncDisposable
{
    private readonly Pipe _toHost = new();
    private readonly Pipe _fromHost = new();
    private readonly StreamWriter _hostInputWriter;
    private readonly StreamReader _hostOutputReader;
    private readonly StreamReader _testReader;
    private readonly Task<int> _runLoopTask;

    public string ScratchDirectory { get; }

    public InProcessKernelHostFixture(IReadOnlyList<BridgedToolSchema>? bridgedTools = null)
    {
        ScratchDirectory = Path.Combine(Path.GetTempPath(), "litos-kernel-tests-" + Guid.NewGuid().ToString("n"));

        _hostInputWriter = new StreamWriter(_toHost.Writer.AsStream()) { AutoFlush = true };
        _hostOutputReader = new StreamReader(_toHost.Reader.AsStream());
        var hostOutputWriter = new StreamWriter(_fromHost.Writer.AsStream()) { AutoFlush = true };
        _testReader = new StreamReader(_fromHost.Reader.AsStream());

        _runLoopTask = RunLoop.RunAsync(_hostOutputReader, hostOutputWriter, CancellationToken.None);

        BridgedToolsForInit = bridgedTools ?? [];
    }

    private IReadOnlyList<BridgedToolSchema> BridgedToolsForInit { get; }

    public async Task InitializeAsync()
    {
        await WireIo.WriteAsync(_hostInputWriter, KernelWireMessage.Of(new Handshake(KernelProtocol.CurrentVersion)), CancellationToken.None);
        var ack = await WireIo.ReadAsync(_testReader, CancellationToken.None);
        if (ack?.HandshakeAck is not { Accepted: true })
            throw new InvalidOperationException($"Handshake rejected: {ack?.HandshakeAck?.Reason}");

        await WireIo.WriteAsync(_hostInputWriter, KernelWireMessage.Of(new InitRequest(ScratchDirectory, BridgedToolsForInit)), CancellationToken.None);
        var initAck = await WireIo.ReadAsync(_testReader, CancellationToken.None);
        if (initAck?.InitAck is not { Success: true })
            throw new InvalidOperationException($"Init rejected: {initAck?.InitAck?.Error}");
    }

    /// <param name="toolResponse">If set, answers every ToolCallRequest this eval triggers with this canned (text, isError) response instead of the default auto-reject.</param>
    public async Task<EvalResult> EvalAsync(string code, (string Text, bool IsError)? toolResponse = null)
    {
        var requestId = Guid.NewGuid().ToString("n");
        await WireIo.WriteAsync(_hostInputWriter, KernelWireMessage.Of(new EvalRequest(requestId, code)), CancellationToken.None);

        while (true)
        {
            var message = await WireIo.ReadAsync(_testReader, CancellationToken.None);
            if (message is null)
                throw new InvalidOperationException("Host closed its output before returning an EvalResult.");

            if (message.Kind == KernelWireMessage.KindEvalResult && message.EvalResult is { } result && result.RequestId == requestId)
                return result;

            if (message.Kind == KernelWireMessage.KindToolCallRequest && message.ToolCallRequest is { } toolCall)
            {
                var (text, isError) = toolResponse ?? ("no bridged tool configured in this test", true);
                await WireIo.WriteAsync(_hostInputWriter, KernelWireMessage.Of(new ToolCallResponse(toolCall.RequestId, text, isError)), CancellationToken.None);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _toHost.Writer.CompleteAsync();
        try
        {
            await _runLoopTask;
        }
        catch
        {
            // Best-effort shutdown — a test failure shouldn't be masked by a cleanup exception.
        }
        if (Directory.Exists(ScratchDirectory))
            Directory.Delete(ScratchDirectory, recursive: true);
    }
}
