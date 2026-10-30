using System.Threading.Channels;
using Litos.Agent;
using Litos.Agent.Messages;
using Litos.Agent.Providers;
using Litos.Agent.Session;
using Litos.Agent.Streaming;
using Litos.Agent.Tools;
using Litos.Host;
using Litos.Kernel;

namespace Litos.Hosting;

public enum TurnOutcome
{
    Started,
    Steered,
}

/// <summary>
/// Litos.Api's AgentWorker, trimmed for a single-user local host: no attachment queueing
/// (queueIfActive path — text-only turns always steer into an already-running turn rather than
/// queue, since there are no ImageBlocks to lose), otherwise the same per-session-key turn
/// lifecycle over the same Litos.Host/AgentLoop.
///
/// Shared by Litos.VsCodeHost and the software factory's worker. What differs between them —
/// where the provider/model comes from, which tools a turn gets, and which directory it runs
/// in — sits behind IModelSelection, IToolSetPolicy and IWorkingDirectoryResolver; the first
/// constructor below wires the defaults that reproduce Litos.VsCodeHost's behaviour.
/// </summary>
public sealed class AgentWorker : BackgroundService
{
    private readonly Lock _turnsLock = new();
    private readonly Dictionary<(SessionOwner Owner, string SessionId), ActiveTurn> _activeTurns = [];
    private readonly IChatProviderFactory _providerFactory;
    private readonly AgentLoopFactory _loopFactory;
    private readonly ITranscriptStore _transcriptStore;
    private readonly IModelSelection _modelSelection;
    private readonly IToolSetPolicy _toolSetPolicy;
    private readonly IWorkingDirectoryResolver _workingDirectoryResolver;

    /// <summary>
    /// Owns this host's chatSessionId -> KernelSession map. Null when kernel mode is unavailable
    /// (Litos.Kernel.Host could not be located) — RunTurnCoreAsync then always takes the OFF path,
    /// so a missing subprocess degrades to "PTC does nothing" rather than failing turns.
    /// </summary>
    private readonly KernelSessionManager? _kernelSessionManager;
    private readonly CancellationTokenSource _stopping = new();

    public AgentWorker(
        IChatProviderFactory providerFactory, AgentLoopFactory loopFactory, ToolRegistryFactory toolRegistryFactory,
        ITranscriptStore transcriptStore, LitosConfig config, KernelSessionManager? kernelSessionManager = null)
        : this(
            providerFactory, loopFactory, transcriptStore,
            new PersistedModelSelection(providerFactory, config), new DefaultToolSetPolicy(toolRegistryFactory),
            new TranscriptWorkingDirectoryResolver(), kernelSessionManager)
    {
    }

    public AgentWorker(
        IChatProviderFactory providerFactory, AgentLoopFactory loopFactory, ITranscriptStore transcriptStore,
        IModelSelection modelSelection, IToolSetPolicy toolSetPolicy, IWorkingDirectoryResolver workingDirectoryResolver,
        KernelSessionManager? kernelSessionManager = null)
    {
        _providerFactory = providerFactory;
        _loopFactory = loopFactory;
        _transcriptStore = transcriptStore;
        _modelSelection = modelSelection;
        _toolSetPolicy = toolSetPolicy;
        _workingDirectoryResolver = workingDirectoryResolver;
        _kernelSessionManager = kernelSessionManager;
    }

    /// <summary>The provider a *new* turn will use — same process-wide (not per-session) semantics
    /// as Litos.Api's AgentWorker/Litos.Gui's MainWindowSession. A turn already running keeps
    /// whatever provider/model it snapshotted at its own start (see RunTurnCoreAsync).</summary>
    public string ProviderName => _modelSelection.ProviderName;

    public string? Model => _modelSelection.Model;

    /// <summary>The current model's context window size — see IModelSelection.ContextLength.</summary>
    public int? ContextLength => _modelSelection.ContextLength;

    public IReadOnlyList<string> AvailableProviders => _modelSelection.AvailableProviders;

    public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(string providerName, CancellationToken ct) =>
        _providerFactory.Resolve(providerName).ListModelsAsync(ct);

    /// <summary>Switches the provider a *new* turn will use, resetting to that provider's own
    /// default model — model ids aren't portable across providers, same rule Litos.Api/Litos.Gui's
    /// own /provider-equivalent commands follow.</summary>
    public Task SwitchProviderAsync(string providerName, CancellationToken ct) =>
        _modelSelection.SwitchProviderAsync(providerName, ct);

    /// <summary>Switches the model a *new* turn will use, keeping the current provider. Looks up
    /// the new model's ContextLength from the same ListModelsAsync call /settings/models already
    /// makes for the picker, so this stays a cache lookup rather than a second network round-trip.</summary>
    public void SetModel(string modelId, int? contextLength) => _modelSelection.SetModel(modelId, contextLength);

    /// <summary>Resolves the IChatProvider for the current ProviderName — used by /compact and
    /// /reflect endpoints, which run a one-off provider call outside the normal turn lifecycle
    /// (RunTurnCoreAsync itself resolves this independently, under its own snapshot).</summary>
    public IChatProvider ResolveActiveProvider() => _providerFactory.Resolve(ProviderName);

    public ChannelReader<AgentEvent>? StartOrSteerTurn(
        SessionOwner owner, string sessionId, IReadOnlyList<ContentBlock> content, CancellationToken requestAborted, out TurnOutcome outcome,
        string? turnKind = null)
    {
        var key = (owner, sessionId);
        Channel<AgentEvent>? events = null;
        ActiveTurn? turn = null;

        lock (_turnsLock)
        {
            if (_activeTurns.TryGetValue(key, out var existing))
            {
                existing.Steering.Writer.TryWrite(new SteeringMessage(RenderForSteering(content), SteeringMode.Steer));
                outcome = TurnOutcome.Steered;
                return null;
            }

            events = Channel.CreateUnbounded<AgentEvent>();
            var steering = Channel.CreateUnbounded<SteeringMessage>();
            // Owned independently of requestAborted (the SSE connection's own token) so a turn can
            // be cancelled explicitly via CancelTurn — e.g. a stuck MCP tool call with no other
            // recovery path — without requiring the client to tear down its HTTP connection, which
            // the VS Code webview never did (no cancel UI existed before this).
            var cancel = new CancellationTokenSource();
            turn = new ActiveTurn(steering, cancel, Task.CompletedTask);
            _activeTurns[key] = turn;
        }

        var runTask = RunTurnAsync(owner, sessionId, content, turnKind, events.Writer, turn.Steering, requestAborted, turn.Cancel);
        lock (_turnsLock)
        {
            // Only while the turn is still registered: one that ran to completion synchronously
            // (it failed before its first real await) has already removed itself, and putting it
            // back would leave the session steering a dead turn for the life of the process.
            if (_activeTurns.TryGetValue(key, out var registered) && ReferenceEquals(registered, turn))
                _activeTurns[key] = turn with { Run = runTask };
        }
        outcome = TurnOutcome.Started;
        return events.Reader;
    }

    /// <summary>
    /// Explicitly aborts the session's in-progress turn, if any — backs the extension's Stop/Cancel
    /// affordance. Returns false when there's nothing to cancel (already finished, or never
    /// started), which the endpoint surfaces as 404 rather than treating as an error.
    /// </summary>
    public bool CancelTurn(SessionOwner owner, string sessionId)
    {
        lock (_turnsLock)
        {
            if (!_activeTurns.TryGetValue((owner, sessionId), out var turn))
                return false;

            turn.Cancel.Cancel();
            return true;
        }
    }

    private static string RenderForSteering(IReadOnlyList<ContentBlock> content) =>
        string.Join("\n\n", content.OfType<TextBlock>().Select(t => t.Text));

    /// <summary>
    /// Steers the session's turn in progress, and never starts one: false when no turn is running
    /// to take the message. Unlike <see cref="StartOrSteerTurn"/>, a caller that only means to
    /// steer cannot start a stray turn from its message.
    /// </summary>
    public bool TrySteer(SessionOwner owner, string sessionId, string text)
    {
        lock (_turnsLock)
            return _activeTurns.TryGetValue((owner, sessionId), out var turn)
                && turn.Steering.Writer.TryWrite(new SteeringMessage(text, SteeringMode.Steer));
    }

    /// <summary>The most unread steering messages kept per session, oldest dropped first.</summary>
    internal const int MaxUndelivered = 20;

    private readonly Dictionary<(SessionOwner Owner, string SessionId), List<string>> _undelivered = [];

    /// <summary>
    /// The steering messages that reached a turn after it had stopped reading them, oldest first,
    /// and forgets them. A turn checks for steering only between its tool calls, so a message can
    /// arrive after its last check and before it ends; its caller can then deliver it another way.
    /// </summary>
    /// <remarks>A turn still ending (a cancel only asks it to stop) is waited for, up to
    /// <paramref name="wait"/>, so what it leaves unread is collected first.</remarks>
    public async Task<IReadOnlyList<string>> TakeUndeliveredSteeringAsync(SessionOwner owner, string sessionId, TimeSpan wait, CancellationToken ct)
    {
        Task? ending;
        lock (_turnsLock)
            ending = _activeTurns.TryGetValue((owner, sessionId), out var turn) ? turn.Run : null;
        if (ending is not null)
            await Task.WhenAny(ending, Task.Delay(wait, ct));

        lock (_turnsLock)
            return _undelivered.Remove((owner, sessionId), out var texts) ? texts : [];
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var registration = stoppingToken.Register(_stopping.Cancel);
        await Task.Delay(Timeout.Infinite, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
    }

    /// <summary>
    /// Public so ContextEndpoints' GET /sessions/{id}/context/usage can resolve ContextLength
    /// on demand rather than only reporting whatever RunTurnCoreAsync happened to have already
    /// resolved — without this, the status row read "Context usage unavailable" until the first
    /// turn ran, even though ListModelsAsync (and, for "local", the LM Studio vendor-catalog
    /// lookup) already had everything needed to answer as soon as the panel opened.
    /// </summary>
    public Task EnsureModelResolvedAsync(CancellationToken ct) => _modelSelection.EnsureResolvedAsync(ct);

    private Task RunTurnAsync(
        SessionOwner owner, string sessionId, IReadOnlyList<ContentBlock> content, string? turnKind,
        ChannelWriter<AgentEvent> events, Channel<SteeringMessage> steering, CancellationToken requestAborted,
        CancellationTokenSource explicitCancel) =>
        // Sets ChannelContext.Owner/SessionId for the whole turn — PendingApprovalRelay reads
        // SessionId synchronously from inside McpAwareApprovalGate's call stack (itself inside
        // AgentLoop.RunTurnAsync, itself inside this scope) to route an Ask-mode MCP approval back
        // to the SSE stream that started the turn which triggered it. See ChannelContext.cs and
        // PendingApprovalRelay.cs for the full mechanism.
        ChannelContext.RunAsAsync(owner, sessionId, () => RunTurnCoreAsync(owner, sessionId, content, turnKind, events, steering, requestAborted, explicitCancel));

    private async Task RunTurnCoreAsync(
        SessionOwner owner, string sessionId, IReadOnlyList<ContentBlock> content, string? turnKind,
        ChannelWriter<AgentEvent> events, Channel<SteeringMessage> steering, CancellationToken requestAborted,
        CancellationTokenSource explicitCancel)
    {
        try
        {
            await EnsureModelResolvedAsync(requestAborted);

            // Snapshot under the lock at turn start — a /provider or /model switch made mid-turn
            // only affects the *next* turn, matching Litos.Api's AgentWorker.RunTurnCoreAsync.
            var (providerName, model, contextLength) = _modelSelection.Snapshot();

            // explicitCancel (CancelTurn/the Stop button) is linked in alongside requestAborted (the
            // SSE connection dying) and _stopping (host shutdown) — any of the three ends the turn.
            // This is what actually lets a stuck tool call be aborted from the UI instead of only
            // ever timing out on its own (or, before this existed, never being recoverable at all).
            using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token, requestAborted, explicitCancel.Token);

            // Loaded before the registry is built, not after: the kernel-mode toggle is persisted
            // per-session in the transcript (Transcript.KernelModeEnabled, §5.3), so it has to be
            // read before deciding which registry this turn gets.
            var transcript = await Transcript.LoadAsync(_transcriptStore, owner, sessionId, turnCts.Token);
            var workingDirectory = _workingDirectoryResolver.Resolve(transcript.WorkingDirectory);
            if (transcript.WorkingDirectory != workingDirectory)
                transcript.SetWorkingDirectory(workingDirectory);

            // Toggle-gated exactly as Litos.Gui does it (§1/§6/§8.2): OFF builds today's full
            // registry with no kernel awareness at all; ON builds a registry containing ONLY
            // KernelCodeTool, so tools.Schemas genuinely has one entry rather than one entry plus
            // everything else filtered model-side. "Hidden from the model" happens here, at
            // registry construction. Either way the turn's tools come from the tool-set policy.
            ToolRegistry toolRegistry;
            AgentLoop loop;
            if (_kernelSessionManager is not null && transcript.KernelModeEnabled)
            {
                var bridgedTools = _toolSetPolicy.Create(sessionId, turnKind);
                toolRegistry = new ToolRegistry([new KernelCodeTool(bridgedTools.Schemas)]);
                loop = _loopFactory.Create(
                    _providerFactory.Resolve(providerName),
                    toolRegistry,
                    BuildKernelRunner(owner, sessionId, transcript.WorkingDirectory));
            }
            else
            {
                toolRegistry = _toolSetPolicy.Create(sessionId, turnKind);
                loop = _loopFactory.Create(_providerFactory.Resolve(providerName), toolRegistry);
            }

            await foreach (var evt in loop.RunTurnAsync(owner, sessionId, transcript, model, content, turnCts.Token, steering.Reader, contextLength))
                await events.WriteAsync(evt, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // Host shutdown, the request disconnecting, or an explicit CancelTurn — nothing
            // further to report to a writer nobody is (necessarily) reading from anymore.
        }
        catch (Exception ex)
        {
            // Anything else that escapes the loop — a compaction that failed, a model that could
            // not be resolved, a tool-set policy that threw. AgentLoop reports a failed model call
            // as an ErrorOccurred event itself, but these happen outside its stream handling, and
            // this task is awaited by nobody: without this the turn's event stream would simply
            // end, indistinguishable from a turn that finished with nothing to say.
            events.TryWrite(new ErrorOccurred(ex));
        }
        finally
        {
            // Remove-then-dispose under the same lock CancelTurn takes, so a concurrent CancelTurn
            // either observes the turn (and calls .Cancel() before this Dispose() can run) or
            // doesn't find it at all — never a window where it's found but already disposed.
            // Closing the steering channel under that lock too means no steer is written after
            // the unread ones are collected; collecting them before the event stream ends means a
            // caller that has seen the stream end will find them.
            lock (_turnsLock)
            {
                _activeTurns.Remove((owner, sessionId));
                steering.Writer.TryComplete();
                CollectUndelivered((owner, sessionId), steering.Reader);
                explicitCancel.Dispose();
            }

            events.TryComplete();
        }
    }

    /// <summary>Keeps what a finished turn left unread in its steering channel. Called under the turns lock.</summary>
    private void CollectUndelivered((SessionOwner Owner, string SessionId) key, ChannelReader<SteeringMessage> reader)
    {
        while (reader.TryRead(out var message))
        {
            if (!_undelivered.TryGetValue(key, out var texts))
                _undelivered[key] = texts = [];
            texts.Add(message.Text);
            if (texts.Count > MaxUndelivered)
                texts.RemoveAt(0);
        }
    }

    /// <summary>
    /// Closure AgentLoop calls when the model emits run_kernel_code (§8.3) — resolves (lazily
    /// creating, if needed) this chat session's KernelSession and delegates to RunAsync. The
    /// scratch directory comes from ITranscriptStore.GetScratchDirectory (§4.5, §8.5) so it lives
    /// under the session's own storage root, never inside the user's project.
    /// </summary>
    private Func<string, CancellationToken, Task<ToolResult>> BuildKernelRunner(
        SessionOwner owner, string sessionId, string? workingDirectory)
    {
        var resolvedWorkingDirectory = workingDirectory ?? Directory.GetCurrentDirectory();
        return (code, ct) =>
        {
            var kernelSession = _kernelSessionManager!.GetOrCreate(
                sessionId,
                resolvedWorkingDirectory,
                sid => _transcriptStore.GetScratchDirectory(owner, sid));
            return kernelSession.RunAsync(code, ct);
        };
    }

    /// <summary>
    /// Whether kernel mode (PTC) is available in this process at all — false when
    /// Litos.Kernel.Host could not be located, in which case the toggle must not be presented as
    /// switchable in the UI.
    /// </summary>
    public bool IsKernelModeAvailable => _kernelSessionManager is not null;

    /// <summary>Reads the session's persisted kernel-mode toggle (§5.3). A session with no toggle entry yet reads false — the stated OFF default.</summary>
    public async Task<bool> GetKernelModeEnabledAsync(SessionOwner owner, string sessionId, CancellationToken ct)
    {
        var transcript = await Transcript.LoadAsync(_transcriptStore, owner, sessionId, ct);
        return transcript.KernelModeEnabled;
    }

    /// <summary>
    /// Flips the session's persisted kernel-mode toggle by appending a "kernel_toggle" entry
    /// (§5.3) — per-chat-session and durable across resume, not a global app preference.
    /// Turning it OFF also destroys any live kernel for the session: leaving an interpreter running
    /// for a session that can no longer reach it would hold a subprocess (and its memory) for the
    /// rest of the host's life with nothing able to use or reset it.
    /// </summary>
    public async Task SetKernelModeEnabledAsync(SessionOwner owner, string sessionId, bool enabled, CancellationToken ct)
    {
        await _transcriptStore.AppendAsync(owner, sessionId, TranscriptEntry.KernelToggle(enabled), ct);

        if (!enabled && _kernelSessionManager is not null)
            await _kernelSessionManager.DestroyAsync(sessionId);
    }

    /// <summary>Backs /kernel-reset — a deliberate escape hatch for "the interpreter is in a bad state", independent of session lifecycle and compaction (§4.4).</summary>
    public async Task ResetKernelAsync(string sessionId, CancellationToken ct)
    {
        if (_kernelSessionManager is not null)
            await _kernelSessionManager.ResetAsync(sessionId, ct);
    }

    private sealed record ActiveTurn(Channel<SteeringMessage> Steering, CancellationTokenSource Cancel, Task Run);
}
