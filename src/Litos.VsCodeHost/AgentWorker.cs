using System.Threading.Channels;
using Litos.Agent;
using Litos.Agent.Messages;
using Litos.Agent.Providers;
using Litos.Agent.Session;
using Litos.Agent.Streaming;
using Litos.Agent.Tools;
using Litos.Host;

namespace Litos.VsCodeHost;

public enum TurnOutcome
{
    Started,
    Steered,
}

/// <summary>
/// Litos.Api's AgentWorker, trimmed for this single-user local host: no attachment queueing
/// (queueIfActive path — text-only turns always steer into an already-running turn rather than
/// queue, since there are no ImageBlocks to lose), otherwise the same per-session-key turn
/// lifecycle over the same Litos.Host/AgentLoop.
/// </summary>
public sealed class AgentWorker : BackgroundService
{
    private readonly Lock _turnsLock = new();
    private readonly Dictionary<(SessionOwner Owner, string SessionId), ActiveTurn> _activeTurns = [];
    private readonly IChatProviderFactory _providerFactory;
    private readonly AgentLoopFactory _loopFactory;
    private readonly ToolRegistryFactory _toolRegistryFactory;
    private readonly ITranscriptStore _transcriptStore;
    private readonly CancellationTokenSource _stopping = new();

    private readonly Lock _settingsLock = new();
    private LitosConfig _config;
    private string _providerName;
    private string _model;
    private int? _contextLength;

    public AgentWorker(
        IChatProviderFactory providerFactory, AgentLoopFactory loopFactory, ToolRegistryFactory toolRegistryFactory,
        ITranscriptStore transcriptStore, LitosConfig config)
    {
        _providerFactory = providerFactory;
        _loopFactory = loopFactory;
        _toolRegistryFactory = toolRegistryFactory;
        _transcriptStore = transcriptStore;
        _config = config;

        _providerName = config.IsProviderConfigured(config.DefaultProvider)
            ? config.DefaultProvider
            : config.AvailableChatProviders.FirstOrDefault()
                ?? throw new InvalidOperationException(
                    "No API key found for any chat provider. Set ANTHROPIC_API_KEY, OPENAI_API_KEY, GEMINI_API_KEY, OPENROUTER_API_KEY, or LOCAL_BASE_URL.");
        _model = config.DefaultModel ?? "";
    }

    /// <summary>The provider a *new* turn will use — same process-wide (not per-session) semantics
    /// as Litos.Api's AgentWorker/Litos.Gui's MainWindowSession. A turn already running keeps
    /// whatever provider/model it snapshotted at its own start (see RunTurnCoreAsync).</summary>
    public string ProviderName
    {
        get { lock (_settingsLock) return _providerName; }
    }

    public string? Model
    {
        get { lock (_settingsLock) return string.IsNullOrEmpty(_model) ? null : _model; }
    }

    /// <summary>The current model's context window size, resolved alongside the model itself
    /// (constructor's default, SwitchProviderAsync, SetModel, EnsureModelResolvedAsync's own
    /// fallback) — same "resolved once per provider/model switch, not per turn" caching Gui's
    /// MainWindowSession.ContextLength uses. Null until a model carrying ModelInfo.ContextLength
    /// has been resolved at least once (e.g. before the first turn's EnsureModelResolvedAsync).</summary>
    public int? ContextLength
    {
        get { lock (_settingsLock) return _contextLength; }
    }

    public IReadOnlyList<string> AvailableProviders => _config.AvailableChatProviders;

    public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(string providerName, CancellationToken ct) =>
        _providerFactory.Resolve(providerName).ListModelsAsync(ct);

    /// <summary>Switches the provider a *new* turn will use, resetting to that provider's own
    /// default model — model ids aren't portable across providers, same rule Litos.Api/Litos.Gui's
    /// own /provider-equivalent commands follow.</summary>
    public async Task SwitchProviderAsync(string providerName, CancellationToken ct)
    {
        var models = await _providerFactory.Resolve(providerName).ListModelsAsync(ct);
        var defaultModelInfo = models.FirstOrDefault(m => m.IsDefault) ?? models.FirstOrDefault()
            ?? throw new InvalidOperationException($"Provider '{providerName}' returned no models.");

        lock (_settingsLock)
        {
            _providerName = providerName;
            _model = defaultModelInfo.Id;
            _contextLength = defaultModelInfo.ContextLength;
            SaveLastUsedProviderAndModel();
        }
    }

    /// <summary>Switches the model a *new* turn will use, keeping the current provider. Looks up
    /// the new model's ContextLength from the same ListModelsAsync call /settings/models already
    /// makes for the picker, so this stays a cache lookup rather than a second network round-trip.</summary>
    public void SetModel(string modelId, int? contextLength)
    {
        lock (_settingsLock)
        {
            _model = modelId;
            _contextLength = contextLength;
            SaveLastUsedProviderAndModel();
        }
    }

    /// <summary>Persists the just-changed provider/model to ~/.litos/config.json so the next
    /// Litos.VsCodeHost process (spawned on the next VS Code launch, or after a saveKeys respawn)
    /// starts with the same selection instead of falling back to DefaultModel: null — the same
    /// write-on-select approach Litos.Gui's own SaveLastUsedProviderAndModel uses. Must be called
    /// under _settingsLock, since it reads _providerName/_model.</summary>
    private void SaveLastUsedProviderAndModel()
    {
        _config = _config with { DefaultProvider = _providerName, DefaultModel = _model };
        _config.Save();
    }

    /// <summary>Resolves the IChatProvider for the current ProviderName — used by /compact and
    /// /reflect endpoints, which run a one-off provider call outside the normal turn lifecycle
    /// (RunTurnCoreAsync itself resolves this independently, under its own snapshot).</summary>
    public IChatProvider ResolveActiveProvider() => _providerFactory.Resolve(ProviderName);

    public ChannelReader<AgentEvent>? StartOrSteerTurn(
        SessionOwner owner, string sessionId, IReadOnlyList<ContentBlock> content, CancellationToken requestAborted, out TurnOutcome outcome)
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

        var runTask = RunTurnAsync(owner, sessionId, content, events.Writer, turn.Steering, requestAborted, turn.Cancel);
        lock (_turnsLock)
            _activeTurns[key] = turn with { Run = runTask };
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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var registration = stoppingToken.Register(_stopping.Cancel);
        await Task.Delay(Timeout.Infinite, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
    }

    private async Task EnsureModelResolvedAsync(CancellationToken ct)
    {
        // _contextLength is checked too, not just _model: LitosConfig.DefaultModel can already
        // populate _model at construction (see ctor) without ever resolving its ContextLength via
        // ListModelsAsync, which only this method and SwitchProviderAsync/SetModel actually call.
        if (!string.IsNullOrEmpty(_model) && _contextLength is not null)
            return;

        var provider = _providerFactory.Resolve(_providerName);
        var models = await provider.ListModelsAsync(ct);
        if (models.Count == 0)
            throw new InvalidOperationException("No default model configured and the provider returned no models to fall back to.");

        lock (_settingsLock)
        {
            if (string.IsNullOrEmpty(_model))
                _model = (models.FirstOrDefault(m => m.IsDefault) ?? models[0]).Id;
            _contextLength ??= models.FirstOrDefault(m => m.Id == _model)?.ContextLength;
        }
    }

    private Task RunTurnAsync(
        SessionOwner owner, string sessionId, IReadOnlyList<ContentBlock> content,
        ChannelWriter<AgentEvent> events, Channel<SteeringMessage> steering, CancellationToken requestAborted,
        CancellationTokenSource explicitCancel) =>
        // Sets ChannelContext.Owner/SessionId for the whole turn — PendingApprovalRelay reads
        // SessionId synchronously from inside McpAwareApprovalGate's call stack (itself inside
        // AgentLoop.RunTurnAsync, itself inside this scope) to route an Ask-mode MCP approval back
        // to the SSE stream that started the turn which triggered it. See ChannelContext.cs and
        // PendingApprovalRelay.cs for the full mechanism.
        ChannelContext.RunAsAsync(owner, sessionId, () => RunTurnCoreAsync(owner, sessionId, content, events, steering, requestAborted, explicitCancel));

    private async Task RunTurnCoreAsync(
        SessionOwner owner, string sessionId, IReadOnlyList<ContentBlock> content,
        ChannelWriter<AgentEvent> events, Channel<SteeringMessage> steering, CancellationToken requestAborted,
        CancellationTokenSource explicitCancel)
    {
        try
        {
            await EnsureModelResolvedAsync(requestAborted);

            // Snapshot under the lock at turn start — a /provider or /model switch made mid-turn
            // only affects the *next* turn, matching Litos.Api's AgentWorker.RunTurnCoreAsync.
            string providerName, model;
            lock (_settingsLock)
            {
                providerName = _providerName;
                model = _model;
            }

            var toolRegistry = _toolRegistryFactory.Create();
            var loop = _loopFactory.Create(_providerFactory.Resolve(providerName), toolRegistry);

            // explicitCancel (CancelTurn/the Stop button) is linked in alongside requestAborted (the
            // SSE connection dying) and _stopping (host shutdown) — any of the three ends the turn.
            // This is what actually lets a stuck tool call be aborted from the UI instead of only
            // ever timing out on its own (or, before this existed, never being recoverable at all).
            using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token, requestAborted, explicitCancel.Token);

            var transcript = await Transcript.LoadAsync(_transcriptStore, owner, sessionId, turnCts.Token);
            if (transcript.WorkingDirectory is null)
                transcript = Transcript.CreateNew(Directory.GetCurrentDirectory());

            await foreach (var evt in loop.RunTurnAsync(owner, sessionId, transcript, model, content, turnCts.Token, steering.Reader))
                await events.WriteAsync(evt, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // Host shutdown, the request disconnecting, or an explicit CancelTurn — nothing
            // further to report to a writer nobody is (necessarily) reading from anymore.
        }
        finally
        {
            events.TryComplete();
            steering.Writer.TryComplete();
            // Remove-then-dispose under the same lock CancelTurn takes, so a concurrent CancelTurn
            // either observes the turn (and calls .Cancel() before this Dispose() can run) or
            // doesn't find it at all — never a window where it's found but already disposed.
            lock (_turnsLock)
            {
                _activeTurns.Remove((owner, sessionId));
                explicitCancel.Dispose();
            }
        }
    }

    private sealed record ActiveTurn(Channel<SteeringMessage> Steering, CancellationTokenSource Cancel, Task Run);
}
