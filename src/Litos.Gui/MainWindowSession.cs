using Litos.Agent;
using Litos.Agent.Providers;
using Litos.Agent.Session;
using Litos.Agent.Tools;
using Litos.Host;

namespace Litos.Gui;

/// <summary>
/// Bundles the DI-resolved services and mutable provider/model/loop state that MainWindow needs
/// for /resume, /attach, /provider, /model. AgentLoop is bound to one IChatProvider, so switching
/// providers means rebuilding it via AgentLoopFactory — that's why Loop/Model/ProviderName/
/// ChatProvider live here as mutable properties rather than MainWindow's old readonly fields.
/// ISkillDiscovery is deliberately NOT carried here — /skills constructs one fresh, scoped to the
/// live session working directory, rather than reusing the DI singleton (see HandleSkillsAsync).
/// Config is carried so MainWindow can persist the last-used provider/model/working directory
/// back to ~/.litos/config.json (LitosConfig with-updates + Save()) after each change.
/// </summary>
public sealed class MainWindowSession(
    AgentLoopFactory loopFactory,
    ToolRegistry toolRegistry,
    IChatProviderFactory providerFactory,
    ITranscriptStore transcriptStore,
    AttachHandler attachHandler,
    Compactor compactor,
    IReadOnlyList<string> availableProviders,
    string providerName,
    IChatProvider chatProvider,
    AgentLoop loop,
    string model,
    LitosConfig config,
    int contextLength)
{
    public AgentLoopFactory LoopFactory { get; } = loopFactory;

    /// <summary>
    /// Built once at startup via ToolRegistryFactory (see Program.cs) and reused for every
    /// AgentLoop rebuilt by a provider switch — Litos.Gui doesn't run a per-turn loop the way
    /// Litos.Api's AgentWorker does, and dynamic MCP tool discovery is out of scope for this face
    /// (ReadMe_LitosApi_Mcp.md), so one static snapshot for the process's lifetime matches today's
    /// existing behavior exactly.
    /// </summary>
    public ToolRegistry ToolRegistry { get; } = toolRegistry;

    public IChatProviderFactory ProviderFactory { get; } = providerFactory;
    public ITranscriptStore TranscriptStore { get; } = transcriptStore;
    public AttachHandler AttachHandler { get; } = attachHandler;
    public Compactor Compactor { get; } = compactor;
    public IReadOnlyList<string> AvailableProviders { get; } = availableProviders;

    public string ProviderName { get; set; } = providerName;
    public IChatProvider ChatProvider { get; set; } = chatProvider;
    public AgentLoop Loop { get; set; } = loop;
    public string Model { get; set; } = model;
    public LitosConfig Config { get; set; } = config;

    /// <summary>
    /// The current Model's context window, resolved from ModelInfo.ContextLength whenever the
    /// provider/model changes (see MainWindow's HandleProviderAsync/HandleModelAsync) rather than
    /// re-fetched per turn — ListModelsAsync is a network call for most providers, too costly to
    /// repeat after every message just to refresh the status-bar context meter.
    /// </summary>
    public int ContextLength { get; set; } = contextLength;
}
