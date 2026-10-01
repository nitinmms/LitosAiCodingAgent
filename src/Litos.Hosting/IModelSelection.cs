using Litos.Agent.Providers;
using Litos.Host;

namespace Litos.Hosting;

/// <summary>The provider/model a turn runs with, read together so a switch made between two
/// reads can't hand a turn one provider's name with another provider's model.</summary>
public readonly record struct ModelSelectionSnapshot(string ProviderName, string Model, int? ContextLength);

/// <summary>
/// Which provider and model a *new* turn uses, and how that choice changes. Split out of
/// AgentWorker so a host decides where the selection comes from and whether it is remembered:
/// Litos.VsCodeHost persists it to ~/.litos/config.json (<see cref="PersistedModelSelection"/>),
/// while the software factory's worker fixes it at launch and never writes it anywhere
/// (<see cref="FixedModelSelection"/>).
/// </summary>
public interface IModelSelection
{
    string ProviderName { get; }

    /// <summary>Null until a model has been chosen or resolved.</summary>
    string? Model { get; }

    /// <summary>The current model's context window size; null until it has been resolved.</summary>
    int? ContextLength { get; }

    IReadOnlyList<string> AvailableProviders { get; }

    ModelSelectionSnapshot Snapshot();

    /// <summary>Switches provider, resetting to that provider's own default model — model ids
    /// aren't portable across providers.</summary>
    Task SwitchProviderAsync(string providerName, CancellationToken ct);

    void SetModel(string modelId, int? contextLength);

    /// <summary>Resolves the model and its context length if either is still unknown.</summary>
    Task EnsureResolvedAsync(CancellationToken ct);
}

/// <summary>
/// Litos.VsCodeHost's selection: starts from LitosConfig's default provider/model and saves
/// every change back to ~/.litos/config.json, so the next host process starts with the same
/// choice. This is AgentWorker's original provider/model logic, moved here unchanged.
/// </summary>
public sealed class PersistedModelSelection : IModelSelection
{
    private readonly Lock _settingsLock = new();
    private readonly IChatProviderFactory _providerFactory;
    private LitosConfig _config;
    private string _providerName;
    private string _model;
    private int? _contextLength;

    public PersistedModelSelection(IChatProviderFactory providerFactory, LitosConfig config)
    {
        _providerFactory = providerFactory;
        _config = config;

        _providerName = config.IsProviderConfigured(config.DefaultProvider)
            ? config.DefaultProvider
            : config.AvailableChatProviders.FirstOrDefault()
                ?? throw new InvalidOperationException(
                    "No API key found for any chat provider. Set ANTHROPIC_API_KEY, OPENAI_API_KEY, GEMINI_API_KEY, OPENROUTER_API_KEY, or LOCAL_BASE_URL.");
        _model = config.DefaultModel ?? "";
    }

    public string ProviderName
    {
        get { lock (_settingsLock) return _providerName; }
    }

    public string? Model
    {
        get { lock (_settingsLock) return string.IsNullOrEmpty(_model) ? null : _model; }
    }

    /// <summary>Resolved alongside the model itself (SwitchProviderAsync, SetModel,
    /// EnsureResolvedAsync's own fallback) — "resolved once per provider/model switch, not per
    /// turn", the same caching Gui's MainWindowSession.ContextLength uses. Null until a model
    /// carrying ModelInfo.ContextLength has been resolved at least once.</summary>
    public int? ContextLength
    {
        get { lock (_settingsLock) return _contextLength; }
    }

    public IReadOnlyList<string> AvailableProviders => _config.AvailableChatProviders;

    public ModelSelectionSnapshot Snapshot()
    {
        lock (_settingsLock)
            return new ModelSelectionSnapshot(_providerName, _model, _contextLength);
    }

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

    public async Task EnsureResolvedAsync(CancellationToken ct)
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
}

/// <summary>
/// A selection fixed for the life of the process — the software factory's worker gets its
/// provider and model as launch arguments (ReadMe_LitosSoftwareFactory_V1.md §13.2) and must
/// never write them to the ~/.litos/config.json it shares with every other Litos face.
/// Switching is refused rather than ignored, so a caller that tries learns it had no effect.
/// </summary>
public sealed class FixedModelSelection(string providerName, string model, int? contextLength = null) : IModelSelection
{
    public string ProviderName => providerName;

    public string? Model => model;

    public int? ContextLength => contextLength;

    public IReadOnlyList<string> AvailableProviders => [providerName];

    public ModelSelectionSnapshot Snapshot() => new(providerName, model, contextLength);

    public Task SwitchProviderAsync(string providerName, CancellationToken ct) =>
        throw new NotSupportedException("This host's provider is fixed at launch and cannot be switched.");

    public void SetModel(string modelId, int? contextLength) =>
        throw new NotSupportedException("This host's model is fixed at launch and cannot be changed.");

    public Task EnsureResolvedAsync(CancellationToken ct) => Task.CompletedTask;
}
