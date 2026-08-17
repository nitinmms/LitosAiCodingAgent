using System.Threading.Channels;
using Litos.Agent;
using Litos.Agent.Messages;
using Litos.Agent.Providers;
using Litos.Agent.Session;
using Litos.Agent.Streaming;
using Litos.Agent.Tools;
using Litos.Api.Channels;
using Litos.Host;

namespace Litos.Api;

public enum TurnOutcome
{
    Started,
    Steered,
    Queued,
}

/// <summary>
/// Drives every turn in this process — the HTTP API's own session and, per
/// ReadMe_TelegramIntegrationTool.md §6.3, one independent turn per linked Telegram chat — as a
/// BackgroundService whose lifetime is the container's own (Kestrel/SIGTERM share this via the
/// generic host, HeadlessServiceTool.md §5.2).
///
/// Turns are keyed by (SessionOwner, sessionId) and run concurrently, one Task per key, mirroring
/// OpenClaw's own concurrency model (docs.openclaw.ai/concepts/queue: per-session lanes run in
/// parallel; a message for a session already running steers that session's in-flight turn rather
/// than queueing behind unrelated sessions). This is a deliberate departure from this class's
/// earlier single-global-turn design — that shape made a busy Telegram chat block the HTTP API's
/// own session (and vice versa) for no reason, since the two share no state once each has its own
/// Transcript. Same-session concurrency still isn't offered: a second message for a session
/// already running steers it (SteeringMode.Steer), it never starts a second concurrent turn for
/// the same key.
/// </summary>
public sealed class AgentWorker : BackgroundService
{
    // _activeTurns and _pendingContent are both guarded by _turnsLock, not left as independent
    // ConcurrentDictionarys — the two must change together atomically (an "is this key active"
    // check plus an enqueue-into/dequeue-from _pendingContent, or a turn's own removal from
    // _activeTurns plus whatever a concurrent StartOrSteerTurn call decides to do about pending
    // content for that same key). Two separately-locked structures left a real window where
    // content enqueued right as a turn finished could be stranded forever: a dequeue could
    // observe "not active yet" and complete before the enqueue that was meant to reach it ran.
    // A plain Dictionary is fine here — every access already goes through the lock, so there's no
    // benefit to ConcurrentDictionary's own lock-free reads.
    private readonly Lock _turnsLock = new();
    private readonly Dictionary<(SessionOwner Owner, string SessionId), ActiveTurn> _activeTurns = [];
    private readonly Dictionary<(SessionOwner Owner, string SessionId), List<ContentBlock>> _pendingContent = [];
    private readonly Lock _settingsLock = new();
    private readonly IChatProviderFactory _providerFactory;
    private readonly AgentLoopFactory _loopFactory;
    private readonly ToolRegistryFactory _toolRegistryFactory;
    private readonly ITranscriptStore _transcriptStore;
    private readonly CancellationTokenSource _stopping = new();

    // The provider/model a *new* turn will pick up. Settings can change these at any time
    // (Litos.Api Settings page) — a turn already running snapshots its own provider/loop at
    // start (see RunTurnAsync) and keeps using it even if these fields change mid-turn, so an
    // admin switching providers never disturbs a turn already in flight.
    private string _providerName;
    private string _model;

    public AgentWorker(
        IChatProviderFactory providerFactory, AgentLoopFactory loopFactory, ToolRegistryFactory toolRegistryFactory,
        ITranscriptStore transcriptStore, LitosConfig config)
    {
        _providerFactory = providerFactory;
        _loopFactory = loopFactory;
        _toolRegistryFactory = toolRegistryFactory;
        _transcriptStore = transcriptStore;

        _providerName = config.IsProviderConfigured(config.DefaultProvider)
            ? config.DefaultProvider
            : config.AvailableChatProviders.FirstOrDefault()
                ?? throw new InvalidOperationException(
                    "No API key found for any chat provider. Set ANTHROPIC_API_KEY, OPENAI_API_KEY, GEMINI_API_KEY, OPENROUTER_API_KEY, or LOCAL_BASE_URL.");
        _model = config.DefaultModel ?? "";
        StartedAt = DateTimeOffset.UtcNow;
    }

    public string ProviderName
    {
        get { lock (_settingsLock) return _providerName; }
    }

    public string? Model
    {
        get { lock (_settingsLock) return string.IsNullOrEmpty(_model) ? null : _model; }
    }

    public DateTimeOffset StartedAt { get; }

    public bool IsTurnActive
    {
        get { lock (_turnsLock) return _activeTurns.Count > 0; }
    }

    public bool IsTurnActiveFor(SessionOwner owner, string sessionId)
    {
        lock (_turnsLock) return _activeTurns.ContainsKey((owner, sessionId));
    }

    /// <summary>
    /// Providers with a configured API key — the choices a Settings page can offer, mirroring
    /// Litos.Console's `/provider` usage-list (Program.cs `availableProviders`).
    /// </summary>
    public IReadOnlyList<string> AvailableProviders { get; init; } = [];

    public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(string providerName, CancellationToken ct) =>
        _providerFactory.Resolve(providerName).ListModelsAsync(ct);

    /// <summary>
    /// Switches the provider a *new* turn will use, resetting to that provider's own default
    /// model — model ids aren't portable across providers, same rule as Litos.Console's
    /// `/provider` command (Program.cs:579-582). Does not affect a turn already in progress.
    /// </summary>
    public async Task SwitchProviderAsync(string providerName, CancellationToken ct)
    {
        var models = await _providerFactory.Resolve(providerName).ListModelsAsync(ct);
        var defaultModel = models.FirstOrDefault(m => m.IsDefault)?.Id ?? models.FirstOrDefault()?.Id
            ?? throw new InvalidOperationException($"Provider '{providerName}' returned no models.");

        lock (_settingsLock)
        {
            _providerName = providerName;
            _model = defaultModel;
        }
    }

    /// <summary>Switches the model a *new* turn will use, keeping the current provider.</summary>
    public void SetModel(string modelId)
    {
        lock (_settingsLock)
            _model = modelId;
    }

    /// <summary>
    /// Starts a new turn for (<paramref name="owner"/>, <paramref name="sessionId"/>), or — if a
    /// turn is already running for that exact key — writes <paramref name="content"/> into its
    /// live steering channel (SteeringMode.Steer) instead. Different keys never conflict: each
    /// runs as its own concurrently-executing Task (see class remarks), so a Telegram chat's turn
    /// and the HTTP API's own session — or two different Telegram chats — make progress
    /// independently. The returned channel reader (only non-null for TurnOutcome.Started) is fed
    /// by that turn's own background Task and completes when the turn ends.
    ///
    /// <paramref name="queueIfActive"/> changes what happens on the "already running" path: when
    /// true (the HTTP attachment endpoint's own case — see TurnsEndpoints), content is held in
    /// _pendingContent instead of being steered in, because RenderForSteering only carries text
    /// into a running turn and would silently drop any ImageBlock. The queued content is prepended
    /// to whatever starts the *next* fresh turn for this key, once the current one finishes.
    /// Callers that never set this (Telegram, plain-text HTTP) keep today's immediate-steer
    /// behavior unchanged.
    /// </summary>
    public ChannelReader<AgentEvent>? StartOrSteerTurn(
        SessionOwner owner, string sessionId, IReadOnlyList<ContentBlock> content, CancellationToken requestAborted,
        out TurnOutcome outcome, bool queueIfActive = false)
    {
        var key = (owner, sessionId);
        Channel<AgentEvent>? events = null;
        IReadOnlyList<ContentBlock>? effectiveContent = null;
        ActiveTurn? turn = null;

        // The full decision — is a turn active for this key, and if so steer/queue, otherwise
        // start one (draining any pending content queued for it) — happens under one lock so it's
        // atomic with RunTurnAsync's own removal of the key on completion (see that method's
        // finally, which takes the same lock). Two independently-locked structures here would
        // leave a window where content queued right as a turn finishes is never dequeued by
        // anyone: a dequeue could observe "not active yet" and complete before a concurrent
        // enqueue for the same key runs. Only the decision and the dictionary mutations happen
        // inside the lock — RunTurnAsync itself (the actual long-running turn) is kicked off
        // after releasing it, so the lock is held only briefly.
        lock (_turnsLock)
        {
            if (_activeTurns.TryGetValue(key, out var existing))
            {
                if (queueIfActive)
                {
                    EnqueuePendingContentLocked(key, content);
                    outcome = TurnOutcome.Queued;
                    return null;
                }

                existing.Steering.Writer.TryWrite(new SteeringMessage(RenderForSteering(content), SteeringMode.Steer));
                outcome = TurnOutcome.Steered;
                return null;
            }

            // Anything queued by an earlier attachment-bearing request while the *previous* turn
            // for this key was running belongs to this fresh turn — prepended so it reads before
            // whatever prompted this turn to start. Dequeued under the same lock that will add
            // this turn to _activeTurns below, so no request arriving after this point can queue
            // content that gets missed by this turn (it'll see _activeTurns already populated and
            // queue for the *next* one instead).
            effectiveContent = _pendingContent.Remove(key, out var pending) && pending.Count > 0
                ? [.. pending, .. content]
                : content;

            events = Channel.CreateUnbounded<AgentEvent>();
            var steering = Channel.CreateUnbounded<SteeringMessage>();
            turn = new ActiveTurn(steering, Task.CompletedTask);
            _activeTurns[key] = turn;
        }

        var runTask = RunTurnAsync(owner, sessionId, effectiveContent, events.Writer, turn.Steering, requestAborted);
        lock (_turnsLock)
            _activeTurns[key] = turn with { Run = runTask };
        outcome = TurnOutcome.Started;
        return events.Reader;
    }

    /// <summary>Caller must already hold _turnsLock.</summary>
    private void EnqueuePendingContentLocked((SessionOwner, string) key, IReadOnlyList<ContentBlock> content)
    {
        if (_pendingContent.TryGetValue(key, out var existing))
            existing.AddRange(content);
        else
            _pendingContent[key] = [.. content];
    }

    private static string RenderForSteering(IReadOnlyList<ContentBlock> content) =>
        string.Join("\n\n", content.OfType<TextBlock>().Select(t => t.Text));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Turns run as independent Tasks kicked off by StartOrSteerTurn (see class remarks), not
        // drained from a queue here. This method's only job is linking the host's own shutdown
        // token so every in-flight turn observes it — deliberately not gated behind this method
        // having run at all: a turn can be started (via StartOrSteerTurn, e.g. from an HTTP
        // request) before BackgroundService.StartAsync gets around to calling ExecuteAsync, so
        // default-model resolution happens lazily in RunTurnAsync itself (EnsureModelResolvedAsync)
        // rather than being awaited here.
        using var registration = stoppingToken.Register(_stopping.Cancel);
        await Task.Delay(Timeout.Infinite, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
    }

    /// <summary>
    /// Resolves the initial default model on first use, if the config didn't already pin one —
    /// idempotent and safe to call from multiple concurrently-starting turns (only the first
    /// actually hits the provider; the rest see _model already set once the lock is released).
    /// </summary>
    private async Task EnsureModelResolvedAsync(CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(_model))
            return;

        var provider = _providerFactory.Resolve(_providerName);
        var models = await provider.ListModelsAsync(ct);
        if (models.Count == 0)
            throw new InvalidOperationException("No default model configured and the provider returned no models to fall back to.");
        var resolved = (models.FirstOrDefault(m => m.IsDefault) ?? models[0]).Id;

        lock (_settingsLock)
        {
            if (string.IsNullOrEmpty(_model))
                _model = resolved;
        }
    }

    private Task RunTurnAsync(
        SessionOwner owner, string sessionId, IReadOnlyList<ContentBlock> content,
        ChannelWriter<AgentEvent> events, Channel<SteeringMessage> steering, CancellationToken requestAborted) =>
        // Sets ChannelContext.Owner/SessionId for every turn (HTTP and Telegram alike) — this is
        // the one place both paths already funnel through, so a tool like ShareFileTool can read
        // "which session is this" without AgentLoop/ITool needing a new parameter. Telegram's own
        // ChannelContext.RunAsAsync("telegram", chatId, ...) wrap (TelegramSessionDriver.cs) sets
        // Channel/ChannelId *before* this runs (it wraps the synchronous call that creates this
        // method's Task, and AsyncLocal is captured at Task-creation time even though that Task is
        // never awaited under the outer wrap) — this call's own RunAsAsync(owner, sessionId, ...)
        // overload preserves whatever Channel/ChannelId is already ambient rather than clobbering it.
        ChannelContext.RunAsAsync(owner, sessionId, () => RunTurnCoreAsync(owner, sessionId, content, events, steering, requestAborted));

    private async Task RunTurnCoreAsync(
        SessionOwner owner, string sessionId, IReadOnlyList<ContentBlock> content,
        ChannelWriter<AgentEvent> events, Channel<SteeringMessage> steering, CancellationToken requestAborted)
    {
        await EnsureModelResolvedAsync(requestAborted);

        // Snapshot the provider/model this turn will run on — settled at turn start and never
        // revisited, so a Settings change made mid-turn only affects the *next* turn.
        string providerName, model;
        lock (_settingsLock)
        {
            providerName = _providerName;
            model = _model;
        }

        // Built fresh here, alongside the provider/model snapshot above — a turn started after
        // an MCP server was added/reconnected sees its tools; a turn already in flight keeps
        // whatever ToolRegistry snapshot it was constructed with (next-turn-only visibility).
        var toolRegistry = _toolRegistryFactory.Create();
        var loop = _loopFactory.Create(_providerFactory.Resolve(providerName), toolRegistry);

        // Linked so either a host shutdown or the originating request going away ends the turn —
        // an abandoned SSE connection no longer leaves a turn (and its tool calls) running
        // indefinitely just because nobody is reading the event stream anymore.
        using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token, requestAborted);

        try
        {
            var transcript = await Transcript.LoadAsync(_transcriptStore, owner, sessionId, turnCts.Token);
            if (transcript.WorkingDirectory is null)
                transcript = Transcript.CreateNew(Directory.GetCurrentDirectory());

            await foreach (var evt in loop.RunTurnAsync(owner, sessionId, transcript, model, content, turnCts.Token, steering.Reader))
                await events.WriteAsync(evt, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // Either a host shutdown or the request disconnecting — nothing further to report to
            // a writer that (per the disconnect case) nobody is reading from anymore.
        }
        finally
        {
            events.TryComplete();
            steering.Writer.TryComplete();
            lock (_turnsLock)
                _activeTurns.Remove((owner, sessionId));
        }
    }

    private sealed record ActiveTurn(Channel<SteeringMessage> Steering, Task Run);
}
