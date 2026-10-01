using Litos.Agent.Tools;
using Litos.Hosting.Approvals;
using Litos.Kernel;
using Litos.Tools.Mcp;
using Litos.Tools.Shell;

namespace Litos.Hosting;

/// <summary>How a host wants MCP wired. Every default reproduces Litos.VsCodeHost.</summary>
public sealed record LitosMcpOptions
{
    /// <summary>Where server definitions and permissions live; null uses McpConfigStore's own
    /// default file under ~/.litos.</summary>
    public McpConfigStore? ConfigStore { get; init; }

    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan ReconcilePollInterval { get; init; } = TimeSpan.FromSeconds(5);
}

public static class HostingServiceCollectionExtensions
{
    /// <summary>
    /// Registers the approval plumbing a host needs when it has no MCP servers: every tool call is
    /// approved, and the pending-approval store and relay TurnsEndpoints depends on exist but
    /// never carry anything.
    /// </summary>
    public static IServiceCollection AddLitosAutoApproval(this IServiceCollection services)
    {
        var pendingApprovalStore = new PendingApprovalStore();
        services.AddSingleton(pendingApprovalStore);
        services.AddSingleton(new PendingApprovalRelay(pendingApprovalStore).Start());
        services.AddSingleton<IToolApprovalGate>(new AutoApprovalGate());
        return services;
    }

    /// <summary>
    /// Registers MCP tools and the approval gate that guards them, and starts connecting to the
    /// configured servers. Returns the tool provider so the caller can hand it to the kernel bridge.
    ///
    /// Built-in tools (read_file, shell, ...) are blanket-approved, matching Litos.Gui/Litos.Console's
    /// own auto-approve model — see ReadMe_ConsoleParityPlan.md's "Revised direction on tool approval"
    /// for why that's the shared convention across faces for internal tools specifically. MCP tools
    /// (mcp__{server}__{tool}) are gated per-server by McpConfigStore's Deny/Full/Ask instead, matching
    /// Litos.Api's model exactly — McpAwareApprovalGate (Litos.Tools.Mcp, already face-agnostic)
    /// decorates the inner blanket-approve gate and only intercepts mcp__-prefixed calls. Everything
    /// is constructed directly (new, not via DI), mirroring Litos.Api/Program.cs's own construction
    /// order — McpConfigStore/PendingApprovalStore need to exist before AddSingleton&lt;IToolApprovalGate&gt;
    /// can close over them, and before any tool that resolves IToolApprovalGate from the container runs.
    /// </summary>
    public static McpToolProvider AddLitosMcp(this IServiceCollection services, LitosMcpOptions? options = null)
    {
        options ??= new LitosMcpOptions();

        var mcpConfigStore = options.ConfigStore ?? new McpConfigStore();
        var pendingApprovalStore = new PendingApprovalStore();
        var pendingApprovalRelay = new PendingApprovalRelay(pendingApprovalStore).Start();
        var approvalGate = new McpAwareApprovalGate(new AutoApprovalGate(), mcpConfigStore, pendingApprovalStore);
        services.AddSingleton(mcpConfigStore);
        services.AddSingleton(pendingApprovalStore);
        services.AddSingleton(pendingApprovalRelay);
        services.AddSingleton<IToolApprovalGate>(approvalGate);

        // McpToolProvider/McpToolSource/McpToolRefreshService, mirroring Litos.Api/Program.cs's own MCP
        // wiring (same face-agnostic Litos.Tools.Mcp types, same approvalGate instance reused), with one
        // deliberate difference: InitializeAsync is fire-and-forget here rather than awaited before
        // builder.Build(). Unlike Litos.Api/Litos.Gui, a loopback host's own liveness signal (the stdout
        // port handshake) is itself gated on Program.cs finishing — so awaiting a slow/unreachable
        // MCP server's up-to-HandshakeTimeout connect here meant the VS Code webview sat unusable for
        // that whole time on every window open, not just the first turn seeing an incomplete tool list.
        // McpToolRefreshService's poll-with-backoff already tolerates connections still being in
        // flight — RefreshAsync only adds servers not yet in _connections — so a turn sent before
        // this finishes just proceeds with whatever tools are connected so far, same as any later turn
        // racing a mid-session Unreachable retry already does.
        var mcpLoggerFactory = LoggerFactory.Create(loggingBuilder => loggingBuilder.AddConsole());
        var mcpToolProvider = new McpToolProvider(mcpConfigStore, mcpLoggerFactory, approvalGate);
        _ = mcpToolProvider.InitializeAsync(options.HandshakeTimeout, CancellationToken.None);
        services.AddSingleton(mcpToolProvider);
        services.AddSingleton<IToolSource>(new McpToolSource(mcpToolProvider));

        services.AddSingleton(sp => new McpToolRefreshService(
            mcpToolProvider, options.ReconcilePollInterval, options.HandshakeTimeout, sp.GetRequiredService<ILogger<McpToolRefreshService>>()));
        services.AddHostedService(sp => sp.GetRequiredService<McpToolRefreshService>());

        return mcpToolProvider;
    }
}

public static class KernelHosting
{
    /// <summary>
    /// Kernel mode / PTC (ReadMe_PTCPersistentKernel.md). Returns null — meaning "PTC unavailable in
    /// this process" — when Litos.Kernel.Host cannot be located, rather than throwing: a packaging
    /// mistake that omits the subprocess binary should degrade to a toggle the UI reports as
    /// unavailable, not a host that fails every turn. KernelHostLocator.Resolve() is the same probe
    /// KernelSession would run on first use, so doing it once here surfaces the problem at startup
    /// instead of on whatever turn first tries to evaluate code.
    /// </summary>
    /// <param name="bridgedTools">The tools kernel code may call for a session. "Hidden from the
    /// model" (PTC on shows it run_kernel_code only) and "unavailable to the bridge" must not be
    /// conflated: this is the full set the session is allowed, whatever the model-facing toggle says.</param>
    public static KernelSessionManager? TryCreateSessionManager(
        Func<string, ToolRegistry> bridgedTools, McpToolProvider? mcpToolProvider = null)
    {
        try
        {
            KernelHostLocator.Resolve();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[litos] Kernel mode (PTC) unavailable: {ex.Message}");
            return null;
        }

        return new KernelSessionManager(bridgedTools, mcpToolProvider);
    }
}
