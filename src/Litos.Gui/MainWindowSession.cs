using Litos.Agent;
using Litos.Agent.Providers;
using Litos.Agent.Session;
using Litos.Agent.Tools;
using Litos.Host;
using Litos.Tools.Mcp;

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
    ToolRegistryFactory toolRegistryFactory,
    ToolRegistry toolRegistry,
    IChatProviderFactory providerFactory,
    ITranscriptStore transcriptStore,
    AttachHandler attachHandler,
    Compactor compactor,
    Reflector reflector,
    IReadOnlyList<string> availableProviders,
    string providerName,
    IChatProvider chatProvider,
    AgentLoop loop,
    string model,
    LitosConfig config,
    int contextLength,
    McpConfigStore mcpConfigStore,
    McpToolProvider mcpToolProvider,
    ISystemPromptProvider systemPromptProvider)
{
    public AgentLoopFactory LoopFactory { get; } = loopFactory;

    /// <summary>
    /// Rebuilds a fresh ToolRegistry snapshot (static tools + every IToolSource's current tools,
    /// including MCP) — called by MainWindow.SubmitAsync immediately before every turn so a server
    /// added/enabled/disabled/removed via /mcp is picked up on the next send without a restart.
    /// </summary>
    public ToolRegistryFactory ToolRegistryFactory { get; } = toolRegistryFactory;

    /// <summary>
    /// Rebuilt via ToolRegistryFactory.Create() immediately before every RunTurnAsync call (see
    /// MainWindow.SubmitAsync) so newly discovered MCP tools are visible on the next turn — a turn
    /// already in flight keeps whatever AgentLoop/ToolRegistry it captured at the moment it started.
    /// </summary>
    public ToolRegistry ToolRegistry { get; set; } = toolRegistry;

    public IChatProviderFactory ProviderFactory { get; } = providerFactory;
    public ITranscriptStore TranscriptStore { get; } = transcriptStore;
    public AttachHandler AttachHandler { get; } = attachHandler;
    public Compactor Compactor { get; } = compactor;
    public Reflector Reflector { get; } = reflector;
    public IReadOnlyList<string> AvailableProviders { get; } = availableProviders;

    /// <summary>Live MCP server config, mutated by McpServersWindow's add/edit/enable/disable/remove actions.</summary>
    public McpConfigStore McpConfigStore { get; } = mcpConfigStore;

    /// <summary>Orchestrates MCP server connections; McpServersWindow reads Connections for live status and calls RefreshAsync after each mutation.</summary>
    public McpToolProvider McpToolProvider { get; } = mcpToolProvider;

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

    /// <summary>Builds the system prompt for the "View Context" breakdown modal — the same provider AgentLoop uses per-turn, called on demand rather than cached since it can change between turns (new AGENTS.md content, newly discovered MCP tools).</summary>
    public ISystemPromptProvider SystemPromptProvider { get; } = systemPromptProvider;
}
