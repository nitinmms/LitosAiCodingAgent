using System.Collections.Concurrent;
using System.Threading.Channels;
using Litos.Agent;
using Litos.Agent.Messages;
using Litos.Agent.Providers;
using Litos.Agent.Session;
using Litos.Agent.Streaming;
using Litos.Agent.Tools;
using Litos.Host;

namespace Litos.Api;

public enum TurnOutcome
{
    Started,
    Steered,
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
    private readonly ConcurrentDictionary<(SessionOwner Owner, string SessionId), ActiveTurn> _activeTurns = new();
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

        _providerName = config.ApiKeys.ContainsKey(config.DefaultProvider)
            ? config.DefaultProvider
            : config.ApiKeys.Keys.FirstOrDefault(LitosConfig.ChatProviderNames.Contains)
                ?? throw new InvalidOperationException(
                    "No API key found for any chat provider. Set ANTHROPIC_API_KEY, OPENAI_API_KEY, GEMINI_API_KEY, or OPENROUTER_API_KEY.");
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

    public bool IsTurnActive => !_activeTurns.IsEmpty;

    public bool IsTurnActiveFor(SessionOwner owner, string sessionId) => _activeTurns.ContainsKey((owner, sessionId));

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
    /// </summary>
    public ChannelReader<AgentEvent>? StartOrSteerTurn(
        SessionOwner owner, string sessionId, IReadOnlyList<ContentBlock> content, CancellationToken requestAborted, out TurnOutcome outcome)
    {
        var key = (owner, sessionId);
        if (_activeTurns.TryGetValue(key, out var existing))
        {
            existing.Steering.Writer.TryWrite(new SteeringMessage(RenderForSteering(content), SteeringMode.Steer));
            outcome = TurnOutcome.Steered;
            return null;
        }

        var events = Channel.CreateUnbounded<AgentEvent>();
        var steering = Channel.CreateUnbounded<SteeringMessage>();
        var turn = new ActiveTurn(steering, Task.CompletedTask);
        if (!_activeTurns.TryAdd(key, turn))
        {
            // Lost a race with another caller starting the same key between the check above and
            // here — treat it the same as "already running": steer instead of double-starting.
            _activeTurns[key].Steering.Writer.TryWrite(new SteeringMessage(RenderForSteering(content), SteeringMode.Steer));
            outcome = TurnOutcome.Steered;
            return null;
        }

        var runTask = RunTurnAsync(owner, sessionId, content, events.Writer, steering, requestAborted);
        _activeTurns[key] = turn with { Run = runTask };
        outcome = TurnOutcome.Started;
        return events.Reader;
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

    private async Task RunTurnAsync(
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
            _activeTurns.TryRemove((owner, sessionId), out _);
        }
    }

    private sealed record ActiveTurn(Channel<SteeringMessage> Steering, Task Run);
}
