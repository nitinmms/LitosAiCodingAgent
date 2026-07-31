using Avalonia;
using Litos.Agent.Providers;
using Litos.Agent.Session;
using Litos.Agent.Tools;
using Litos.Host;
using Litos.Tools.Attachments;
using Litos.Tools.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace Litos.Gui;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
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
        var provider = services.BuildServiceProvider();

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
        var toolRegistry = provider.GetRequiredService<ToolRegistryFactory>().Create();
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
            toolRegistry,
            providerFactory,
            provider.GetRequiredService<ITranscriptStore>(),
            new AttachHandler(provider.GetRequiredService<IAttachmentConverter>()),
            provider.GetRequiredService<Compactor>(),
            LitosConfig.ChatProviderNames.Where(config.ApiKeys.ContainsKey).ToList(),
            activeProviderName,
            chatProvider,
            loop,
            model,
            config,
            contextLength);

        return BuildAvaloniaApp(session, workingDirectory)
            .StartWithClassicDesktopLifetime(args);
    }

    private static AppBuilder BuildAvaloniaApp(MainWindowSession session, string workingDirectory) =>
        AppBuilder.Configure(() => new App { Session = session, WorkingDirectory = workingDirectory })
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

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
