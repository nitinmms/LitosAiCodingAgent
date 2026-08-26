using System.Diagnostics;
using System.Text.Json;
using Litos.Agent.Tools;
using Litos.Tools.Mcp;

namespace Litos.Kernel;

/// <summary>
/// One instance per chat session (ReadMe_PTCPersistentKernel.md §4.4) — owns a lazily-spawned
/// Litos.Kernel.Host subprocess for the lifetime of the chat session, not one round or one turn.
/// The subprocess is a genuinely separate OS process (§4.2) running Roslyn/C# scripting
/// out-of-process (§4.3); this class is the .NET-side half of the stdio protocol plus the tool
/// bridge's servicing loop (§8.2).
/// </summary>
public sealed class KernelSession : IAsyncDisposable
{
    private readonly string _sessionId;
    private readonly string _workingDirectory;
    private readonly string _scratchDirectory;
    private readonly string _auditLogPath;
    private readonly Func<ToolRegistry> _bridgedToolsSource;
    private readonly McpToolProvider? _mcpToolProvider;
    private readonly TimeSpan _hardTimeout;
    private readonly Lock _processLock = new();

    private Process? _process;
    private SemaphoreSlim? _writeLock;
    private Task? _readerLoop;
    private readonly Dictionary<string, TaskCompletionSource<EvalResult>> _pendingEvals = [];
    private readonly Lock _pendingLock = new();

    public KernelSession(
        string sessionId,
        string workingDirectory,
        string scratchDirectory,
        Func<ToolRegistry> bridgedToolsSource,
        McpToolProvider? mcpToolProvider = null,
        TimeSpan? hardTimeout = null)
    {
        _sessionId = sessionId;
        _workingDirectory = workingDirectory;
        _scratchDirectory = scratchDirectory;
        _auditLogPath = Path.Combine(Path.GetDirectoryName(scratchDirectory.TrimEnd('/', '\\'))!, "audit.jsonl");
        _bridgedToolsSource = bridgedToolsSource;
        _mcpToolProvider = mcpToolProvider;
        _hardTimeout = hardTimeout ?? TimeSpan.FromMinutes(5);
    }

    /// <summary>mcp__{server}__{tool} — McpToolProxy's own naming convention (Litos.Tools.Mcp.McpToolProxy.cs).</summary>
    private const string McpToolNamePrefix = "mcp__";

    /// <summary>
    /// Sends an EvalRequest, services any ToolCallRequest messages the subprocess emits by
    /// resolving and invoking the real ITool — ungated, per §5.1, no IToolApprovalGate call
    /// anywhere in this path — until the matching EvalResult arrives. Lazily spawns the subprocess
    /// on the first call. Wrapped in a hard timeout mirroring ShellTool's; on timeout or
    /// cancellation the process tree is killed and the session marked dead so the next call
    /// transparently respawns.
    /// </summary>
    public async Task<ToolResult> RunAsync(string code, CancellationToken ct)
    {
        try
        {
            await EnsureStartedAsync(ct);
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"Failed to start kernel: {ex.Message}");
        }

        var requestId = Guid.NewGuid().ToString("n");
        var tcs = new TaskCompletionSource<EvalResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingLock)
            _pendingEvals[requestId] = tcs;

        AppendAudit(new { evt = "eval_start", requestId, codeLength = code.Length });

        using var timeoutCts = new CancellationTokenSource(_hardTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            await WriteLockedAsync(KernelWireMessage.Of(new EvalRequest(requestId, code)), linkedCts.Token);
            var result = await tcs.Task.WaitAsync(linkedCts.Token);
            AppendAudit(new { evt = "eval_end", requestId, result.IsError, result.Truncated });
            return result.IsError
                ? ToolResult.Error(Combine(result))
                : ToolResult.Ok(Combine(result));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            AppendAudit(new { evt = "eval_timeout", requestId });
            await KillAndResetAsync();
            return ToolResult.Error($"Kernel eval timed out after {_hardTimeout.TotalMinutes:0}m and was killed. The kernel will restart on the next call.");
        }
        catch (OperationCanceledException)
        {
            AppendAudit(new { evt = "eval_cancelled", requestId });
            await KillAndResetAsync();
            throw;
        }
        finally
        {
            lock (_pendingLock)
                _pendingEvals.Remove(requestId);
        }
    }

    private static string Combine(EvalResult result)
    {
        var text = string.IsNullOrEmpty(result.Output) ? (result.ReturnValueText ?? "") : result.Output;
        if (!string.IsNullOrEmpty(result.ReturnValueText) && !string.IsNullOrEmpty(result.Output))
            text += "\n" + result.ReturnValueText;
        if (result.Truncated && result.ArtifactPath is not null)
            text += $"\n[output truncated, full content at {result.ArtifactPath}]";
        if (result.StateDelta is not null)
            text += "\n" + result.StateDelta;
        return text;
    }

    /// <summary>Backs /kernel-reset and crash/hang recovery (§4.4 table) — kills the subprocess and clears lazy-start state so the next RunAsync respawns fresh.</summary>
    public async Task ResetAsync(CancellationToken ct)
    {
        AppendAudit(new { evt = "reset" });
        await KillAndResetAsync();
    }

    private async Task KillAndResetAsync()
    {
        Process? toKill;
        lock (_processLock)
        {
            toKill = _process;
            _process = null;
            _writeLock = null;
        }
        if (toKill is not null)
            await KillTreeAsync(toKill);

        lock (_pendingLock)
        {
            foreach (var tcs in _pendingEvals.Values)
                tcs.TrySetException(new InvalidOperationException("Kernel session was reset."));
            _pendingEvals.Clear();
        }
    }

    private async Task EnsureStartedAsync(CancellationToken ct)
    {
        lock (_processLock)
        {
            if (_process is { HasExited: false })
                return;
        }

        Directory.CreateDirectory(_scratchDirectory);

        var hostPath = KernelHostLocator.Resolve();
        var startInfo = new ProcessStartInfo
        {
            FileName = hostPath.FileName,
            WorkingDirectory = _workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in hostPath.Arguments)
            startInfo.ArgumentList.Add(arg);

        // Minimized environment, not inherited wholesale — this subprocess runs model-generated
        // code (§5, ungated), so the least-surprise default flips from ShellTool's "inherit
        // everything" (§8.2). Only what the .NET/Roslyn host itself needs to run is kept; provider
        // API keys and other secrets are never copied in.
        startInfo.EnvironmentVariables.Clear();
        CopyIfPresent(startInfo, "PATH");
        CopyIfPresent(startInfo, "DOTNET_ROOT");
        CopyIfPresent(startInfo, "TEMP");
        CopyIfPresent(startInfo, "TMP");
        CopyIfPresent(startInfo, "HOME"); // macOS/Linux TEMP-equivalent lookups often fall back to HOME.

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.Start();

        var writeLock = new SemaphoreSlim(1, 1);
        lock (_processLock)
        {
            _process = process;
            _writeLock = writeLock;
        }

        _ = DrainStderrAsync(process);

        await WriteLockedInternalAsync(process, writeLock, KernelWireMessage.Of(new Handshake(KernelProtocol.CurrentVersion)), ct);
        var ackMsg = await WireIo.ReadAsync(process.StandardOutput, ct);
        if (ackMsg?.HandshakeAck is not { Accepted: true })
        {
            await KillTreeAsync(process);
            throw new InvalidOperationException($"Kernel subprocess handshake failed: {ackMsg?.HandshakeAck?.Reason ?? "no response"}");
        }

        var bridgedTools = _bridgedToolsSource().Schemas
            .Select(s => new BridgedToolSchema(s.Name, s.Description, s.ParameterSchema))
            .ToList();
        await WriteLockedInternalAsync(process, writeLock, KernelWireMessage.Of(new InitRequest(_scratchDirectory, bridgedTools)), ct);
        var initAckMsg = await WireIo.ReadAsync(process.StandardOutput, ct);
        if (initAckMsg?.InitAck is not { Success: true })
        {
            await KillTreeAsync(process);
            throw new InvalidOperationException($"Kernel subprocess init failed: {initAckMsg?.InitAck?.Error ?? "no response"}");
        }

        AppendAudit(new { evt = "kernel_started", pid = process.Id });

        _readerLoop = ReadLoopAsync(process);
    }

    private async Task ReadLoopAsync(Process process)
    {
        try
        {
            while (true)
            {
                var message = await WireIo.ReadAsync(process.StandardOutput, CancellationToken.None);
                if (message is null)
                    return; // Subprocess closed its stdout — it exited or crashed.

                switch (message.Kind)
                {
                    case KernelWireMessage.KindEvalResult when message.EvalResult is { } result:
                        TaskCompletionSource<EvalResult>? tcs;
                        lock (_pendingLock)
                            _pendingEvals.TryGetValue(result.RequestId, out tcs);
                        tcs?.TrySetResult(result);
                        break;

                    case KernelWireMessage.KindToolCallRequest when message.ToolCallRequest is { } request:
                        _ = ServiceToolCallAsync(process, request);
                        break;
                }
            }
        }
        catch
        {
            // Reader loop ended abnormally (process died mid-read) — pending evals are left to
            // time out via RunAsync's own hard timeout rather than being force-failed here, since
            // a race between "process just exited" and "result already in flight" is otherwise
            // hard to adjudicate safely.
        }
    }

    /// <summary>
    /// Resolves the real ITool and invokes it directly — ungated (§5.1), no IToolApprovalGate call
    /// for a built-in tool. An MCP-named tool (mcp__{server}__{tool}) is routed through
    /// McpToolProvider.InvokeDirectAsync instead of ToolRegistry.Resolve(...).InvokeAsync — the
    /// latter would resolve to McpToolProxy, whose InvokeAsync calls IToolApprovalGate internally,
    /// silently re-gating MCP tools from inside a supposedly ungated kernel (§7/§8.2's flagged bug).
    /// </summary>
    private async Task<ToolResult> InvokeBridgedToolAsync(string toolName, JsonElement arguments)
    {
        if (_mcpToolProvider is not null && toolName.StartsWith(McpToolNamePrefix, StringComparison.Ordinal))
        {
            var rest = toolName[McpToolNamePrefix.Length..];
            var separatorIndex = rest.IndexOf("__", StringComparison.Ordinal);
            if (separatorIndex > 0)
            {
                var serverName = rest[..separatorIndex];
                var mcpToolName = rest[(separatorIndex + 2)..];
                return await _mcpToolProvider.InvokeDirectAsync(serverName, mcpToolName, arguments, CancellationToken.None);
            }
        }

        var tool = _bridgedToolsSource().Resolve(toolName);
        return await tool.InvokeAsync(arguments, CancellationToken.None);
    }

    private async Task ServiceToolCallAsync(Process process, ToolCallRequest request)
    {
        string text;
        bool isError;
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await InvokeBridgedToolAsync(request.ToolName, request.Arguments);
            text = result.Text;
            isError = result.IsError;
        }
        catch (Exception ex)
        {
            text = $"Bridged tool '{request.ToolName}' failed: {ex.Message}";
            isError = true;
        }

        var capped = text.Length > KernelLimits.MaxToolCallResponseBytes
            ? text[..KernelLimits.MaxToolCallResponseBytes] + "...[truncated]"
            : text;

        AppendAudit(new { evt = "tool_call", request.ToolName, durationMs = sw.ElapsedMilliseconds, isError, resultSize = text.Length });

        SemaphoreSlim? writeLock;
        lock (_processLock)
            writeLock = _writeLock;
        if (writeLock is null)
            return;

        await WriteLockedInternalAsync(process, writeLock, KernelWireMessage.Of(new ToolCallResponse(request.RequestId, capped, isError)), CancellationToken.None);
    }

    private async Task WriteLockedAsync(KernelWireMessage message, CancellationToken ct)
    {
        Process? process;
        SemaphoreSlim? writeLock;
        lock (_processLock)
        {
            process = _process;
            writeLock = _writeLock;
        }
        if (process is null || writeLock is null)
            throw new InvalidOperationException("Kernel subprocess is not running.");
        await WriteLockedInternalAsync(process, writeLock, message, ct);
    }

    private static async Task WriteLockedInternalAsync(Process process, SemaphoreSlim writeLock, KernelWireMessage message, CancellationToken ct)
    {
        await writeLock.WaitAsync(ct);
        try
        {
            await WireIo.WriteAsync(process.StandardInput, message, ct);
        }
        finally
        {
            writeLock.Release();
        }
    }

    private static async Task DrainStderrAsync(Process process)
    {
        try
        {
            await process.StandardError.ReadToEndAsync();
        }
        catch
        {
            // Best-effort only — stderr is drained to prevent the pipe from filling and blocking
            // the child, not surfaced anywhere today.
        }
    }

    private static void CopyIfPresent(ProcessStartInfo startInfo, string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (value is not null)
            startInfo.EnvironmentVariables[name] = value;
    }

    /// <summary>Cross-platform tree-kill (ReadMe_PTCPersistentKernel.md §2 Hard requirements) — Process.Kill(entireProcessTree: true) works identically on Windows and macOS/Linux.</summary>
    private static Task KillTreeAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Process may have already exited in the gap between the check and the kill call.
        }
        return Task.CompletedTask;
    }

    private void AppendAudit(object record)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_auditLogPath)!);
            var line = JsonSerializer.Serialize(record, record.GetType());
            File.AppendAllText(_auditLogPath, DateTimeOffset.UtcNow.ToString("O") + " " + line + Environment.NewLine);
        }
        catch
        {
            // Audit logging is best-effort debugging/benchmark data (§8.2) — never allowed to fail an eval.
        }
    }

    /// <summary>Kills the subprocess if running. Called by /new, never by /compact (§4.4 table).</summary>
    public async ValueTask DisposeAsync()
    {
        await KillAndResetAsync();
    }
}
