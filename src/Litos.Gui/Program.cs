using Avalonia;
using Litos.Agent.Providers;
using Litos.Agent.Session;
using Litos.Agent.Tools;
using Litos.Artifacts;
using Litos.Host;
using Litos.Hosting;
using Litos.Hosting.Files;
using Litos.Tools.Attachments;
using Litos.Tools.Mcp;
using Litos.Tools.Shell;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Litos.Gui;

internal static class Program
{
    // Mirrors Litos.Api/Program.cs's own mcpHandshakeTimeout comment: measured against a real
    // npx-launched reference server, where process spawn + MCP initialize handshake + tools/list
    // took ~18s even with a warm npm cache. Unlike Api, this timeout only bounds the
    // fire-and-forget startup connect (§3.2) — it never blocks the window from appearing.
    private static readonly TimeSpan McpHandshakeTimeout = TimeSpan.FromSeconds(30);

    // Same reconcile cadence Litos.Hosting's LitosMcpOptions defaults Litos.VsCodeHost to.
    private static readonly TimeSpan McpReconcilePollInterval = TimeSpan.FromSeconds(5);

    [STAThread]
    public static int Main(string[] args)
    {
        // Must run before any MCP server connects (InitializeMcpAsync below): job membership
        // propagates to every process spawned afterward, so this process needs to already be in
        // the job before StdioClientTransport spawns its first "cmd.exe /c <command>" child. See
        // Win32JobObject's own remarks for why this exists and why it's Windows-only.
        if (OperatingSystem.IsWindows())
            Win32JobObject.AssignCurrentProcessWithKillOnClose();

        var resume = SessionResumeArgs.Parse(args);

        var config = LitosConfig.Load();
        if (config.AvailableChatProviders.Count == 0)
        {
            // No console window exists (OutputType=WinExe), so a Console.WriteLine here would be
            // invisible to a user launching the published exe directly — show a window instead.
            // ApiKeysApp runs its own AppBuilder.Configure/Start to completion (one-shot per
            // process) before the real App below is ever built, so the two never coexist.
            // Saving starts the real app in a fresh process (it can't start here: Avalonia's
            // AppBuilder is one-shot per process, and providers are registered only once). If that
            // relaunch fails, exiting is what this flow always did — the user starts Litos again.
            var keysSaved = ApiKeysApp.Run(args);
            if (keysSaved)
                AppRelauncher.TryRelaunch(resume);
            return keysSaved ? 0 : 1;
        }

        if (!config.ApiKeys.ContainsKey("tavily"))
            Console.WriteLine("Web search disabled: set TAVILY_API_KEY to enable.");

        var services = new ServiceCollection().AddLitosAgent(config);

        // Built-in tools stay blanket-approved (GuiApprovalGate), the shared convention across
        // faces — see ReadMe_ConsoleParityPlan.md's "Revised direction on tool approval". MCP tools
        // (mcp__{server}__{tool}) are gated per server by McpConfigStore's Deny/Ask/Full, the same
        // decorator Litos.Hosting's AddLitosMcp wires for Litos.VsCodeHost. An Ask lands in
        // pendingApprovals, which MainWindow renders as an in-transcript Approve/Deny card.
        // Constructed directly (not via DI): McpToolProvider needs the gate instance before
        // BuildServiceProvider() exists to resolve it, mirroring AddLitosMcp's own construction order.
        var mcpConfigStore = new McpConfigStore();
        var pendingApprovals = new PendingApprovalStore();
        var approvalGate = new McpAwareApprovalGate(new GuiApprovalGate(), mcpConfigStore, pendingApprovals);
        services.AddSingleton<IToolApprovalGate>(approvalGate);
        // No provider added — Litos.Gui has no log sink today (unlike Litos.Api's LogStore/
        // InMemoryLoggerProvider); McpServerConnection's handshake/failure logging still runs
        // through this factory, it just has nowhere to go yet.
        using var mcpLoggerFactory = LoggerFactory.Create(_ => { });
        var mcpToolProvider = new McpToolProvider(mcpConfigStore, mcpLoggerFactory, approvalGate);

        // Registered as the single IToolSource instance, not per-tool AddSingleton<ITool> calls —
        // ToolRegistryFactory reads IToolSource.CurrentTools fresh every time Create() runs
        // (SubmitAsync, §3.5), so a server added/enabled/disabled/removed via /mcp is picked up
        // on the next send without a restart.
        services.AddSingleton<IToolSource>(new McpToolSource(mcpToolProvider));

        // Serves agent-published artifacts and share_file downloads over loopback, so those tools'
        // links open in the user's browser exactly as they do from the VS Code extension. Both tools
        // read the base URL lazily per call; the server is started below, before any turn can run.
        var loopbackServer = new GuiLoopbackServer();
        services.AddLitosSharedFiles(() => loopbackServer.BaseUrl);
        // Must follow AddLitosAgent: it replaces that ISkillDiscovery with one that also searches
        // the built-in skill root (artifact-design).
        services.AddLitosArtifacts(() => loopbackServer.BaseUrl);

        var provider = services.BuildServiceProvider();
        loopbackServer.Start(provider.GetRequiredService<ArtifactStore>(), provider.GetRequiredService<SharedFileStore>());

        // Fire-and-forget, not awaited: the main window must appear immediately rather than
        // block on MCP handshakes (§3.2 point 3, a deliberate departure from Litos.Api's blocking
        // await before app.Build()). Connections populate McpToolProvider.Tools as they complete;
        // SubmitAsync's per-send ToolRegistry rebuild (§3.5) is what makes them visible, so there's
        // no "first turn must have everything" pressure the way Api's DI-container snapshot has.
        _ = InitializeMcpAsync(mcpToolProvider);

        // Background reconcile, as Litos.VsCodeHost runs it: retries Unreachable servers on their
        // backoff and picks up config changes made elsewhere (e.g. the VS Code MCP panel, which
        // shares ~/.litos/mcp.json). Stopped from MainWindow's Closing handler.
        var mcpRefreshService = new McpToolRefreshService(
            mcpToolProvider, McpReconcilePollInterval, McpHandshakeTimeout, NullLogger<McpToolRefreshService>.Instance);
        _ = mcpRefreshService.StartAsync(CancellationToken.None);

        var providerFactory = provider.GetRequiredService<IChatProviderFactory>();
        var activeProviderName = ResolveStartupProvider(config);
        // A saved DefaultModel is only meaningful for the provider it was saved alongside — if the
        // resolved provider differs from config.DefaultProvider (ResolveStartupProvider fell back),
        // the saved model almost certainly doesn't belong to it. Discarding it here is what makes
        // the "pick this provider's own default model" branch below actually run instead of handing
        // a wrong-provider model id to ListModelsAsync/ModelContextWindows.Resolve.
        var savedModel = activeProviderName == config.DefaultProvider ? config.DefaultModel : null;
        var chatProvider = providerFactory.Resolve(activeProviderName);

        // Only strictly required when no default model is configured yet (to pick one), but
        // also attempted when a default IS set, purely to resolve its ContextLength for the
        // status-bar meter — a best-effort upgrade, so a failure here (offline, rate-limited)
        // falls back to the static table below rather than blocking startup.
        // With no saved model a failure here used to escape Main — a silent crash in a WinExe. It
        // now opens the window with no model, and the error is shown with the "pick a model" notice.
        IReadOnlyList<ModelInfo> models;
        string? modelListError = null;
        try
        {
            models = chatProvider.ListModelsAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            models = [];
            if (savedModel is null)
                modelListError = ex.Message;
        }

        // Empty when the provider reported no models and none is saved (typically ListModelsAsync
        // failing offline on a first run). The window still opens — MainWindow tells the user to
        // pick one with /model or /provider and refuses to send until they do — rather than this
        // throwing out of Main, which in a WinExe is a silent crash with nothing on screen.
        var model = ResolveStartupModel(savedModel, models) ?? "";
        var contextLength = model.Length == 0
            ? 0
            : models.FirstOrDefault(m => m.Id == model)?.ContextLength ?? ModelContextWindows.Resolve(model);

        var loopFactory = provider.GetRequiredService<AgentLoopFactory>();
        var toolRegistryFactory = provider.GetRequiredService<ToolRegistryFactory>();
        var toolRegistry = toolRegistryFactory.Create();
        var loop = loopFactory.Create(chatProvider, toolRegistry);

        // The bridge's tool source is always the FULL (OFF-equivalent) registry, regardless of the
        // model-facing toggle state — "hidden from the model" (ON's tools.Schemas = [run_kernel_code]
        // only, per §6/§8.2) and "unavailable to the bridge" must not be conflated into the same
        // registry instance. The sessionId parameter is unused today (one registry for the whole
        // process) but keeps KernelSessionManager's factory shape session-scoped per §4.4, in case a
        // future revision ever needs per-session tool visibility.
        // Null means PTC is unavailable in this build (Litos.Kernel.Host not found beside the exe):
        // the status-bar toggle and /ptc then say so instead of a turn failing later — the same
        // probe Litos.VsCodeHost runs.
        var kernelSessionManager = KernelHosting.TryCreateSessionManager(
            _ => toolRegistryFactory.Create(),
            mcpToolProvider);

        var workingDirectory = resume is not null && Directory.Exists(resume.WorkingDirectory)
            ? resume.WorkingDirectory
            : ResolveStartupWorkingDirectory(config.LastWorkingDirectory, Directory.Exists, Directory.GetCurrentDirectory);

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
            config.AvailableChatProviders.ToList(),
            activeProviderName,
            chatProvider,
            loop,
            model,
            config,
            contextLength,
            mcpConfigStore,
            mcpToolProvider,
            provider.GetRequiredService<ISystemPromptProvider>(),
            kernelSessionManager,
            pendingApprovals,
            mcpRefreshService,
            loopbackServer);

        // Fire-and-forget, same shape as InitializeMcpAsync above: must not block the window from
        // appearing. MainWindow reads session.UpdateCheckTask once constructed and reflects the
        // result in the status bar (see MainWindow's constructor and ObserveUpdateCheckAsync).
        session.UpdateCheckTask = SelfUpdater.CheckForUpdateAsync(session.UpdateHttpClient, CancellationToken.None);
        session.StartupModelListError = modelListError;

        return BuildAvaloniaApp(session, workingDirectory, resume?.SessionId)
            .StartWithClassicDesktopLifetime(args);
    }

    private static AppBuilder BuildAvaloniaApp(MainWindowSession session, string workingDirectory, string? resumeSessionId) =>
        AppBuilder.Configure(() => new App { Session = session, WorkingDirectory = workingDirectory, ResumeSessionId = resumeSessionId })
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

    /// <summary>
    /// The saved DefaultProvider if it's still configured (has an API key / base URL, per
    /// LitosConfig.IsProviderConfigured), otherwise the first available configured provider — the
    /// same fallback the inline code always had, just extracted so it's unit-testable and so
    /// callers (Main) can tell whether a fallback happened, which is what determines whether the
    /// saved DefaultModel is still trustworthy (see ResolveStartupModel's caller in Main).
    /// </summary>
    internal static string ResolveStartupProvider(LitosConfig config) =>
        config.IsProviderConfigured(config.DefaultProvider)
            ? config.DefaultProvider
            : config.AvailableChatProviders[0];

    /// <summary>
    /// Picks the model to start with: the saved model id, but only if it's still real —
    /// validated against the resolved provider's own current model list, not trusted verbatim.
    /// A saved id that doesn't appear in that list (stale after the provider's catalog changed, or
    /// carried over from a provider ResolveStartupProvider fell away from) falls through to the
    /// provider's own reported default, then its first model. An empty <paramref name="models"/>
    /// (offline/rate-limited ListModelsAsync — Main already treats that as non-fatal when a saved
    /// model exists) means there's nothing to validate against, so the saved model is trusted as
    /// given rather than treated as invalid by omission. Returns null only when there is truly no
    /// model to start with at all (no saved model, and the provider's own list is empty too) — Main
    /// then opens the window with no model, and MainWindow asks the user to pick one.
    /// </summary>
    internal static string? ResolveStartupModel(string? savedModel, IReadOnlyList<ModelInfo> models)
    {
        if (models.Count == 0 || (savedModel is not null && models.Any(m => m.Id == savedModel)))
            return savedModel;

        return models.FirstOrDefault(m => m.IsDefault)?.Id ?? models.FirstOrDefault()?.Id;
    }
}
