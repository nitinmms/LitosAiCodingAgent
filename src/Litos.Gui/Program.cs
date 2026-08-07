using Avalonia;
using Litos.Agent.Providers;
using Litos.Agent.Session;
using Litos.Agent.Tools;
using Litos.Host;
using Litos.Tools.Attachments;
using Litos.Tools.Mcp;
using Litos.Tools.Shell;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Litos.Gui;

internal static class Program
{
    // Mirrors Litos.Api/Program.cs's own mcpHandshakeTimeout comment: measured against a real
    // npx-launched reference server, where process spawn + MCP initialize handshake + tools/list
    // took ~18s even with a warm npm cache. Unlike Api, this timeout only bounds the
    // fire-and-forget startup connect (§3.2) — it never blocks the window from appearing.
    private static readonly TimeSpan McpHandshakeTimeout = TimeSpan.FromSeconds(30);

    [STAThread]
    public static int Main(string[] args)
    {
        // Must run before any MCP server connects (InitializeMcpAsync below): job membership
        // propagates to every process spawned afterward, so this process needs to already be in
        // the job before StdioClientTransport spawns its first "cmd.exe /c <command>" child. See
        // Win32JobObject's own remarks for why this exists and why it's Windows-only.
        if (OperatingSystem.IsWindows())
            Win32JobObject.AssignCurrentProcessWithKillOnClose();

        var config = LitosConfig.Load();
        if (!LitosConfig.ChatProviderNames.Any(config.ApiKeys.ContainsKey))
        {
            Console.WriteLine("No API key found for any provider. Set ANTHROPIC_API_KEY, OPENAI_API_KEY, GEMINI_API_KEY, or OPENROUTER_API_KEY and try again.");
            return 1;
        }

        if (!config.ApiKeys.ContainsKey("tavily"))
            Console.WriteLine("Web search disabled: set TAVILY_API_KEY to enable.");

        var services = new ServiceCollection().AddLitosAgent(config);
        services.AddSingleton<IToolApprovalGate, GuiApprovalGate>();

        // Constructed directly (not via DI) since McpToolProvider needs the GuiApprovalGate
        // instance below before BuildServiceProvider() exists to resolve it, mirroring how
        // Litos.Api/Program.cs builds McpConfigStore/McpToolProvider ahead of builder.Build().
        // No wrapping McpAwareApprovalGate — Litos.Gui's approval gate auto-approves everything
        // unconditionally, so MCP tool calls flow through GuiApprovalGate unmodified (§0/§3.2).
        var mcpConfigStore = new McpConfigStore();
        var guiApprovalGate = new GuiApprovalGate();
        // No provider added — Litos.Gui has no log sink today (unlike Litos.Api's LogStore/
        // InMemoryLoggerProvider); McpServerConnection's handshake/failure logging still runs
        // through this factory, it just has nowhere to go yet.
        using var mcpLoggerFactory = LoggerFactory.Create(_ => { });
        var mcpToolProvider = new McpToolProvider(mcpConfigStore, mcpLoggerFactory, guiApprovalGate);

        // Registered as the single IToolSource instance, not per-tool AddSingleton<ITool> calls —
        // ToolRegistryFactory reads IToolSource.CurrentTools fresh every time Create() runs
        // (SubmitAsync, §3.5), so a server added/enabled/disabled/removed via /mcp is picked up
        // on the next send without a restart.
        services.AddSingleton<IToolSource>(new McpToolSource(mcpToolProvider));

        var provider = services.BuildServiceProvider();

        // Fire-and-forget, not awaited: the main window must appear immediately rather than
        // block on MCP handshakes (§3.2 point 3, a deliberate departure from Litos.Api's blocking
        // await before app.Build()). Connections populate McpToolProvider.Tools as they complete;
        // SubmitAsync's per-send ToolRegistry rebuild (§3.5) is what makes them visible, so there's
        // no "first turn must have everything" pressure the way Api's DI-container snapshot has.
        _ = InitializeMcpAsync(mcpToolProvider);

        var providerFactory = provider.GetRequiredService<IChatProviderFactory>();
        var activeProviderName = config.DefaultProvider;
        if (!config.ApiKeys.ContainsKey(activeProviderName))
            activeProviderName = LitosConfig.ChatProviderNames.First(config.ApiKeys.ContainsKey);
        var chatProvider = providerFactory.Resolve(activeProviderName);

        // Only strictly required when no default model is configured yet (to pick one), but
        // also attempted when a default IS set, purely to resolve its ContextLength for the
        // status-bar meter — a best-effort upgrade, so a failure here (offline, rate-limited)
        // falls back to the static table below rather than blocking startup.
        IReadOnlyList<ModelInfo> models;
        try
        {
            models = chatProvider.ListModelsAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch when (config.DefaultModel is not null)
        {
            models = [];
        }

        var model = config.DefaultModel;
        if (model is null)
        {
            model = models.FirstOrDefault(m => m.IsDefault)?.Id
                ?? models.FirstOrDefault()?.Id
                ?? throw new InvalidOperationException(
                    $"No models available for provider '{activeProviderName}' and no default model configured.");
        }
        var contextLength = models.FirstOrDefault(m => m.Id == model)?.ContextLength ?? ModelContextWindows.Resolve(model);

        var loopFactory = provider.GetRequiredService<AgentLoopFactory>();
        var toolRegistryFactory = provider.GetRequiredService<ToolRegistryFactory>();
        var toolRegistry = toolRegistryFactory.Create();
        var loop = loopFactory.Create(chatProvider, toolRegistry);

        var workingDirectory = ResolveStartupWorkingDirectory(
            config.LastWorkingDirectory, Directory.Exists, Directory.GetCurrentDirectory);

        // File/shell tools resolve relative paths against the process's real CWD, not
        // against Transcript.WorkingDirectory (which is otherwise only ever shown in the
        // status bar and injected into the system prompt as advisory text). Without this,
        // a restored LastWorkingDirectory from a previous session can silently disagree
        // with wherever the OS actually launched this process from.
        Environment.CurrentDirectory = workingDirectory;

        var session = new MainWindowSession(
            loopFactory,
            toolRegistryFactory,
            toolRegistry,
            providerFactory,
            provider.GetRequiredService<ITranscriptStore>(),
            new AttachHandler(provider.GetRequiredService<IAttachmentConverter>()),
            provider.GetRequiredService<Compactor>(),
            provider.GetRequiredService<Reflector>(),
            LitosConfig.ChatProviderNames.Where(config.ApiKeys.ContainsKey).ToList(),
            activeProviderName,
            chatProvider,
            loop,
            model,
            config,
            contextLength,
            mcpConfigStore,
            mcpToolProvider,
            provider.GetRequiredService<ISystemPromptProvider>());

        return BuildAvaloniaApp(session, workingDirectory)
            .StartWithClassicDesktopLifetime(args);
    }

    private static AppBuilder BuildAvaloniaApp(MainWindowSession session, string workingDirectory) =>
        AppBuilder.Configure(() => new App { Session = session, WorkingDirectory = workingDirectory })
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    // Logged, never thrown — a failed/timed-out server just contributes zero tools and an
    // Unreachable status (surfaced later in McpServersWindow), same fire-and-forget contract
    // McpToolProvider.RefreshAsync itself already guarantees internally.
    private static async Task InitializeMcpAsync(McpToolProvider mcpToolProvider)
    {
        try
        {
            await mcpToolProvider.InitializeAsync(McpHandshakeTimeout, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"MCP startup connect failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Picks the directory the GUI should actually launch (and set the process CWD) into:
    /// the last session's directory if it's still there, otherwise wherever the process
    /// happens to have been started from. Kept UI-free and side-effect-free (existence
    /// check and CWD lookup are passed in) so this decision is unit-testable without an
    /// Avalonia host or a real filesystem.
    /// </summary>
    internal static string ResolveStartupWorkingDirectory(
        string? lastWorkingDirectory, Func<string, bool> directoryExists, Func<string> getCurrentDirectory) =>
        lastWorkingDirectory is { } dir && directoryExists(dir)
            ? dir
            : getCurrentDirectory();
}
