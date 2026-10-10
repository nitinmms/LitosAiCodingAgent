using System.Diagnostics;
using Litos.Agent.Messages;
using Litos.Agent.Providers;
using Litos.Agent.Session;
using Litos.Agent.Tools;
using Litos.Host;
using Litos.Hosting;
using Litos.Hosting.Turns;
using Litos.Persistence;
using Litos.SoftwareFactory.Contracts;
using Litos.Tools.Skills;

namespace Litos.SoftwareFactory.Worker;

/// <summary>
/// Composes the worker: Litos.Hosting's turn machinery with the factory's seams in place of
/// Litos.VsCodeHost's defaults (ReadMe_LitosSoftwareFactory_V1.md §13.2).
/// </summary>
public static class WorkerApp
{
    /// <param name="hostHttp">The HttpClient used to reach the factory host; supplied by tests.</param>
    public static WebApplication Build(WorkerOptions options, string[] args, HttpClient? hostHttp = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        LoopbackHost.ConfigureLoopback(builder);

        // Stdout carries the port handshake as its first line, so nothing else may write there
        // before it: logs go to stderr, which the host captures into the run's worker.log.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(console => console.LogToStandardErrorThreshold = LogLevel.Trace);

        // A config built here, never loaded: the worker has no provider keys and must not read
        // or write the ~/.litos/config.json other Litos faces share.
        var config = new LitosConfig(
            DefaultProvider: options.Provider, DefaultModel: options.Model, LastWorkingDirectory: null,
            ApiKeys: new Dictionary<string, string>(), ShellCommandTimeoutSeconds: options.ShellTimeoutSeconds);
        builder.Services.AddLitosAgent(config);
        builder.Services.AddLitosAutoApproval();

        // Registered after AddLitosAgent, so these replace its user-profile defaults.
        builder.Services.AddSingleton<ITranscriptStore>(_ => new JsonlTranscriptStore(options.SessionsDirectory));
        builder.Services.AddSingleton<ISkillDiscovery, NoSkills>();

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(_ => new FactoryHostClient(hostHttp ?? new HttpClient(), options));
        builder.Services.AddSingleton(sp => new GatewayChatProvider(
            sp.GetRequiredService<FactoryHostClient>(), options.Provider, options.Model, options.ContextLength));
        builder.Services.AddSingleton<IChatProviderFactory, GatewayChatProviderFactory>();

        builder.Services.AddSingleton<IModelSelection>(new FixedModelSelection(options.Provider, options.Model, options.ContextLength));
        if (options.McpConfigPath is { } mcpConfig)
            builder.Services.AddSingleton(sp => new WorkerMcp(mcpConfig, sp.GetRequiredService<ILoggerFactory>()));
        // read_file with the factory's per-file limit in place of the profile's (WorkerOptions.ReadFileDefaultLines).
        builder.Services.AddSingleton(sp => new FactoryToolSetPolicy(
            sp.GetServices<ITool>().Select(tool => tool.Name == "read_file"
                ? new Litos.Tools.FileSystem.ReadFileTool(WorkerOptions.ReadFileMaxBytes, WorkerOptions.ReadFileDefaultLines)
                : tool),
            sp.GetRequiredService<FactoryHostClient>(),
            testOutputDirectory: options.TestOutputDirectory,
            webSearch: options.WebSearch,
            mcpTools: sp.GetService<WorkerMcp>() is { } mcp ? mcp.PermittedTools : null));
        builder.Services.AddSingleton<IToolSetPolicy>(sp => sp.GetRequiredService<FactoryToolSetPolicy>());
        builder.Services.AddSingleton<IWorkingDirectoryResolver, ProcessWorkingDirectoryResolver>();

        builder.Services.AddSingleton(sp =>
        {
            // Kernel code can call exactly what the session's current turn may call — never the
            // whole registry, or a read-only review turn could edit files through PTC.
            var policy = sp.GetRequiredService<FactoryToolSetPolicy>();
            var kernel = options.PtcEnabled
                ? KernelHosting.TryCreateSessionManager(policy.CreateForBridge, outputCapChars: WorkerOptions.KernelOutputCapChars)
                : null;
            return new AgentWorker(
                sp.GetRequiredService<IChatProviderFactory>(),
                sp.GetRequiredService<AgentLoopFactory>(),
                sp.GetRequiredService<ITranscriptStore>(),
                sp.GetRequiredService<IModelSelection>(),
                policy,
                sp.GetRequiredService<IWorkingDirectoryResolver>(),
                kernel);
        });
        builder.Services.AddHostedService(sp => sp.GetRequiredService<AgentWorker>());

        if (options.ParentProcessId is { } parentProcessId)
            builder.Services.AddHostedService(sp => new ParentProcessWatcher(parentProcessId, sp.GetRequiredService<IHostApplicationLifetime>()));

        var app = builder.Build();
        app.UseFactoryWorkerSecurity(options.Secret);
        app.MapLitosTurnEndpoints(ReadTurnRequestAsync);
        app.MapWorkerEndpoints();
        return app;
    }

    /// <summary>Starts the worker, announces its port on stdout, and tells the host it is ready.</summary>
    public static async Task<int> StartAsync(WebApplication app)
    {
        var port = await LoopbackHost.StartAndAnnounceAsync(app);

        // Ready means ready (blueprint §8.1): the run's MCP servers have each connected or failed.
        var host = app.Services.GetRequiredService<FactoryHostClient>();
        try
        {
            IReadOnlyList<McpServerReport> mcp = app.Services.GetService<WorkerMcp>() is { } servers
                ? await servers.ConnectAsync(app.Lifetime.ApplicationStopping)
                : [];
            await host.ReadyAsync(new WorkerReady(port, McpReady: true, mcp), app.Lifetime.ApplicationStopping);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            // The host also has the port from the handshake; it decides what a missing "ready" means.
            Console.Error.WriteLine($"[worker] Could not report ready to the factory host: {ex.Message}");
        }

        return port;
    }

    /// <summary>
    /// Reads the host's turn request and, for a session's first turn, switches PTC on. The toggle
    /// is stored per session in its transcript, so it is written once and a later choice to turn
    /// it off is not overridden.
    /// </summary>
    private static async ValueTask<TurnInput> ReadTurnRequestAsync(HttpRequest request, CancellationToken ct)
    {
        var turnRequest = await request.ReadFromJsonAsync<TurnRequest>(ct)
            ?? throw new BadHttpRequestException("Request body is required.");

        var services = request.HttpContext.RequestServices;
        if (services.GetRequiredService<WorkerOptions>().PtcEnabled && request.RouteValues["id"] is string sessionId)
            await EnablePtcForNewSessionAsync(services.GetRequiredService<AgentWorker>(), services.GetRequiredService<ITranscriptStore>(), sessionId, ct);

        return new TurnInput([new TextBlock(turnRequest.Input)], turnRequest.TurnKind);
    }

    internal static async Task EnablePtcForNewSessionAsync(AgentWorker worker, ITranscriptStore store, string sessionId, CancellationToken ct)
    {
        if (!worker.IsKernelModeAvailable)
            return;

        await foreach (var _ in store.ReadAsync(SessionOwner.Local, sessionId, ct))
            return; // the session already has history, so its PTC choice has been made

        await worker.SetKernelModeEnabledAsync(SessionOwner.Local, sessionId, enabled: true, ct);
    }
}

public static class WorkerEndpoints
{
    public static IEndpointRouteBuilder MapWorkerEndpoints(this IEndpointRouteBuilder app)
    {
        // Compaction before a large turn (§8.6). It goes through the gateway like any other model
        // call, so it is charged to the task's budget; a refusal or failure is reported as an
        // error rather than leaving the host to start a large turn on an uncompacted session.
        app.MapPost("/sessions/{id}/compact", async (
            string id, CompactRequest request, AgentWorker worker, ITranscriptStore store, Compactor compactor, CancellationToken ct) =>
        {
            var transcript = await Transcript.LoadAsync(store, SessionOwner.Local, id, ct);
            bool compacted;
            try
            {
                compacted = await compactor.ForceCompactAsync(
                    transcript, worker.ResolveActiveProvider(), worker.Model ?? "", worker.ContextLength, ct, request.Instruction);
            }
            catch (Exception ex) when (ex is GatewayRefusedException or GatewayCallFailedException)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway);
            }

            // JSONL is append-only: the summary is appended and treated as a checkpoint on replay.
            if (compacted)
                await store.AppendAsync(SessionOwner.Local, id, TranscriptEntry.FromMessage(transcript.Messages[0]), ct);
            return Results.Json(new CompactResponse(compacted), FactoryWire.Json);
        });

        app.MapPost(FactoryWire.ShutdownPath, (IHostApplicationLifetime lifetime) =>
        {
            lifetime.StopApplication();
            return Results.Ok();
        });

        return app;
    }
}

/// <summary>
/// Stops the worker when the factory host that started it is gone (§13.2), so a crashed or
/// killed host never leaves a worker running commands with nobody supervising it.
/// </summary>
public sealed class ParentProcessWatcher(int parentProcessId, IHostApplicationLifetime lifetime, TimeSpan? pollInterval = null)
    : BackgroundService
{
    private readonly TimeSpan _pollInterval = pollInterval ?? TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Not before the worker has finished starting: stopping it mid-startup would abort the
        // listener while it binds, and the worker would die with a startup error instead of
        // shutting down in order.
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var onStarted = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        using var onStopping = stoppingToken.Register(() => started.TrySetCanceled(stoppingToken));
        try
        {
            await started.Task;
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            if (!IsRunning(parentProcessId))
            {
                Console.Error.WriteLine($"[worker] Parent process {parentProcessId} has exited; stopping.");
                lifetime.StopApplication();
                return;
            }

            try
            {
                await Task.Delay(_pollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    internal static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
