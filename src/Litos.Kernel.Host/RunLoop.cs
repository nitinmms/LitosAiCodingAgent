using Litos.Kernel;

namespace Litos.Kernel.Host;

/// <summary>
/// The subprocess entry point's main loop: handshake, then init, then a dispatch loop over
/// EvalRequest/ToolCallResponse lines from stdin until EOF. Reading EOF on stdin is this
/// process's cross-platform signal that its parent (Litos.Gui) is gone — a stdio pipe closing is
/// reliable on both Windows and macOS, unlike Win32JobObject's kill-on-close (Windows-only) — so
/// self-terminating on EOF is the load-bearing cleanup path on macOS and a belt-and-suspenders
/// backstop on Windows (ReadMe_PTCPersistentKernel.md §2 Hard requirements).
/// </summary>
public static class RunLoop
{
    public static async Task<int> RunAsync(TextReader input, TextWriter output, CancellationToken ct)
    {
        var writeLock = new SemaphoreSlim(1, 1);

        var handshakeMsg = await WireIo.ReadAsync(input, ct);
        if (handshakeMsg?.Handshake is not { } handshake)
            return 1;

        var accepted = handshake.ProtocolVersion == KernelProtocol.CurrentVersion;
        await WriteLockedAsync(output, writeLock,
            KernelWireMessage.Of(new HandshakeAck(
                KernelProtocol.CurrentVersion,
                accepted,
                accepted ? null : $"Protocol version mismatch: host={handshake.ProtocolVersion}, subprocess={KernelProtocol.CurrentVersion}")),
            ct);
        if (!accepted)
            return 1;

        var initMsg = await WireIo.ReadAsync(input, ct);
        if (initMsg?.InitRequest is not { } init)
            return 1;

        ToolBridge? bridge = null;
        ScriptSession? session = null;
        try
        {
            bridge = new ToolBridge(output, writeLock);
            session = new ScriptSession(init.ScratchDirectory, bridge, init.BridgedTools);
            await session.EnsureBootstrappedAsync();
            await WriteLockedAsync(output, writeLock, KernelWireMessage.Of(new InitAck(true, null)), ct);
        }
        catch (Exception ex)
        {
            await WriteLockedAsync(output, writeLock, KernelWireMessage.Of(new InitAck(false, ex.Message)), ct);
            return 1;
        }

        // An in-flight eval must not block this loop from reading further lines: the eval's own
        // bridged tool calls arrive as ToolCallRequest OUT and their answers arrive as
        // ToolCallResponse IN on this same stream, so awaiting the eval inline here (as an earlier
        // version of this loop did) would deadlock the moment a script called a bridged tool — the
        // ToolCallResponse the eval is blocked on would never be read. Running the eval as a
        // background task keeps this loop free to service that response while the eval is pending.
        // v1 is one eval in flight at a time by protocol contract (§8.8) — evalTask tracks it so a
        // second EvalRequest arriving before the first completes is a detectable protocol violation
        // rather than silently interleaving two evals against the same ScriptState.
        Task? evalTask = null;

        while (true)
        {
            var message = await WireIo.ReadAsync(input, ct);
            if (message is null)
                return 0; // Parent's stdin pipe closed — clean self-termination, not a crash.

            switch (message.Kind)
            {
                case KernelWireMessage.KindEvalRequest when message.EvalRequest is { } evalRequest:
                    if (evalTask is { IsCompleted: false })
                        break; // Protocol violation (overlapping EvalRequests) — ignored rather than corrupting ScriptState with a concurrent eval.
                    evalTask = RunEvalAsync(session, bridge, output, writeLock, evalRequest, ct);
                    break;

                case KernelWireMessage.KindToolCallResponse when message.ToolCallResponse is { } response:
                    bridge.Complete(response);
                    break;
            }
        }
    }

    private static async Task RunEvalAsync(
        ScriptSession session, ToolBridge bridge, TextWriter output, SemaphoreSlim writeLock, EvalRequest evalRequest, CancellationToken ct)
    {
        var result = await session.EvalAsync(evalRequest.RequestId, evalRequest.Code, bridge, ct);
        await WriteLockedAsync(output, writeLock, KernelWireMessage.Of(result), ct);
    }

    private static async Task WriteLockedAsync(TextWriter output, SemaphoreSlim writeLock, KernelWireMessage message, CancellationToken ct)
    {
        await writeLock.WaitAsync(ct);
        try
        {
            await WireIo.WriteAsync(output, message, ct);
        }
        finally
        {
            writeLock.Release();
        }
    }
}
