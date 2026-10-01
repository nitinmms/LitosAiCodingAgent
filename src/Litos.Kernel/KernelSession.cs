using System.Diagnostics;
using System.Text.Json;
using Litos.Agent.Tools;
using Litos.Tools.Mcp;
using Litos.Tools.Shell; // ToolPermission — see DenyMcpToolReason.

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
    private readonly Lock _evalTokenLock = new();
    private CancellationToken _evalToken = CancellationToken.None;

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

        // Bridged tool calls made by this eval run under its token, so cancelling the turn (or
        // the eval timing out) stops a long tool call — a shell command, say — as it would on
        // the direct path, instead of leaving it to the process-tree kill alone.
        var evalToken = linkedCts.Token;
        lock (_evalTokenLock)
            _evalToken = evalToken;

        try
        {
            await WriteLockedAsync(KernelWireMessage.Of(new EvalRequest(requestId, code)), linkedCts.Token);
            var result = await tcs.Task.WaitAsync(linkedCts.Token);
            AppendAudit(new { evt = "eval_end", requestId, result.IsError, result.Truncated });
            return result.IsError
                ? ToolResult.Error(Combine(result))
                : ToolResult.Ok(Combine(result));
        }
        catch (KernelProcessDiedException ex)
        {
            // The interpreter died under this eval (see FailPendingEvalsOnProcessDeathAsync).
            // Reported as a normal tool error, not rethrown: the model can read this, understand
            // that its own script killed the kernel, and try something else on the next round.
            // KillAndResetAsync clears the dead Process handle so the next RunAsync respawns.
            AppendAudit(new { evt = "eval_kernel_died", requestId });
            await KillAndResetAsync();
            return ToolResult.Error(ex.Message);
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

            // Only if no later eval has replaced it: this eval must not clear another's token.
            lock (_evalTokenLock)
            {
                if (_evalToken == evalToken)
                    _evalToken = CancellationToken.None;
            }
        }
    }

    /// <summary>The token bridged tool calls run under: the eval in progress, or none.</summary>
    private CancellationToken CurrentEvalToken()
    {
        lock (_evalTokenLock)
            return _evalToken;
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
        Exception? readFailure = null;
        try
        {
            while (true)
            {
                var message = await WireIo.ReadAsync(process.StandardOutput, CancellationToken.None);
                if (message is null)
                    break; // Subprocess closed its stdout — it exited or crashed.

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
        catch (Exception ex)
        {
            readFailure = ex;
        }

        await FailPendingEvalsOnProcessDeathAsync(process, readFailure);
    }

    /// <summary>
    /// The reader loop has ended, which can only mean the subprocess's stdout reached EOF or the
    /// read itself threw — both of which mean no further EvalResult can ever arrive. Anything still
    /// pending is therefore unanswerable and is failed here, immediately.
    ///
    /// This deliberately replaces the earlier "leave them to RunAsync's hard timeout" behavior. That
    /// was survivable in Litos.Gui, where the timeout is long and a human can reach for
    /// /kernel-reset, but it means an uncatchable script failure (a StackOverflowException from
    /// runaway recursion, or a bare Environment.Exit) stalls the caller for the FULL hard timeout
    /// before reporting anything — measured at ~21s against a 20s timeout, and it would be a
    /// five-minute hang at the 5m default. The model gets no error it could react to in the
    /// meantime; the user just watches a spinner.
    ///
    /// The race the previous comment worried about ("process just exited" vs. "result already in
    /// flight") does not actually arise on the EOF path: stdout reaching EOF means every byte the
    /// subprocess ever wrote has already been read and dispatched by the loop above, so a result
    /// still pending at this point was genuinely never sent. Awaiting exit before failing also lets
    /// the error name the exit code, and gives a process killed by KillAndResetAsync time to be
    /// reaped so this reports the real cause rather than racing the kill.
    /// </summary>
    private async Task FailPendingEvalsOnProcessDeathAsync(Process process, Exception? readFailure)
    {
        try
        {
            await process.WaitForExitAsync();
        }
        catch
        {
            // Already reaped, or never started cleanly — the exit code below is best-effort only.
        }

        string reason;
        try
        {
            reason = process.HasExited
                ? $"Kernel process exited unexpectedly with code {process.ExitCode}."
                : "Kernel process connection was lost.";
        }
        catch
        {
            reason = "Kernel process exited unexpectedly.";
        }

        if (readFailure is not null)
            reason += $" ({readFailure.Message})";

        reason += " This usually means the script crashed the interpreter — for example unbounded"
            + " recursion causing a StackOverflowException, or a direct call to Environment.Exit."
            + " The kernel will restart on the next call; variables and functions from earlier"
            + " rounds are gone.";

        List<TaskCompletionSource<EvalResult>> orphaned;
        lock (_pendingLock)
        {
            orphaned = [.. _pendingEvals.Values];
            _pendingEvals.Clear();
        }

        if (orphaned.Count == 0)
            return;

        AppendAudit(new { evt = "kernel_died", orphanedEvals = orphaned.Count, reason });
        foreach (var tcs in orphaned)
            tcs.TrySetException(new KernelProcessDiedException(reason));
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

                if (DenyMcpToolReason(serverName, toolName) is { } denial)
                    return ToolResult.Error(denial);

                return await _mcpToolProvider.InvokeDirectAsync(serverName, mcpToolName, arguments, CurrentEvalToken());
            }
        }

        var tool = _bridgedToolsSource().Resolve(toolName);
        return await tool.InvokeAsync(arguments, CurrentEvalToken());
    }

    /// <summary>
    /// Kernel-mode's MCP permission rule: honor Deny, treat Ask as approved, allow Full. Returns
    /// the refusal text for a denied call, or null to proceed.
    ///
    /// Built-in tools stay ungated inside the kernel (§5.1) — that is the consent the user gives by
    /// enabling the toggle at all, and gating them would be theater, since a script that can call
    /// read_file can equally open a FileStream. MCP is the case where that argument does NOT carry:
    /// an MCP server is frequently REMOTE and holds credentials the kernel has no other route to,
    /// so a user's explicit Deny on a production database or a paid API is not made redundant by
    /// local code execution. Honoring Deny keeps the one permission that expresses a hard "no"
    /// meaningful whether or not the toggle is on.
    ///
    /// Ask is deliberately NOT prompted here. An approval prompt mid-eval would block the script
    /// against KernelSession's hard timeout (5 minutes by default) waiting on a panel the user is
    /// very likely not watching — an eval that appears hung for reasons the model cannot see or
    /// report. Ask therefore proceeds, and the toggle's own UI is what must say so plainly.
    ///
    /// A server missing from config resolves to Deny via PermissionFor's own safe-by-default
    /// fallback, matching McpAwareApprovalGate.
    /// </summary>
    private string? DenyMcpToolReason(string serverName, string fullToolName)
    {
        var configStore = _mcpToolProvider?.ConfigStore;
        if (configStore is null)
            return null;

        var server = configStore.Current.Servers.FirstOrDefault(s => s.Name == serverName);
        var permission = server?.PermissionFor(fullToolName) ?? ToolPermission.Deny;

        return permission == ToolPermission.Deny
            ? $"MCP tool '{fullToolName}' is denied by this server's configured permission and was not called. "
                + "Kernel mode does not override an explicit Deny — change the server's permission in /mcp if this was unintended."
            : null;
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
