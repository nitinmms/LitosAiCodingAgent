using global::Anthropic.SDK;
using global::GenerativeAI;
using global::MarkItDown;
using global::OpenAI;
using Litos.Agent;
using Litos.Agent.Providers;
using Litos.Agent.Session;
using Litos.Agent.Tools;
using Litos.Persistence;
using Litos.Providers.Anthropic;
using Litos.Providers.Gemini;
using Litos.Providers.Local;
using Litos.Providers.MeshApi;
using Litos.Providers.OpenAI;
using Litos.Providers.OpenRouter;
using Litos.Tools.Attachments;
using Litos.Tools.FileSystem;
using Litos.Tools.ProjectInstructions;
using Litos.Tools.Shell;
using Litos.Tools.Skills;
using Litos.Tools.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Litos.Host;

public static class LitosHostBuilder
{
    public static IServiceCollection AddLitosAgent(this IServiceCollection services, LitosConfig config)
    {
        services.AddSingleton(config);
        // ToolRegistry itself is no longer a DI singleton — AgentLoopFactory builds a fresh one
        // per Create() call, via ToolRegistryFactory, from the static IEnumerable<ITool> below
        // plus whatever any registered IToolSource currently has (e.g. MCP-discovered tools).
        // This is what lets a turn started after an MCP server is added/reconnected see its
        // tools without a container restart, while a turn already in flight keeps the
        // ToolRegistry snapshot it was constructed with.
        services.AddSingleton<ToolRegistryFactory>();
        services.AddSingleton<ITranscriptStore>(_ => new JsonlTranscriptStore());
        services.AddSingleton<ContextAccountant>();
        services.AddSingleton(new CompactionSettings());
        services.AddSingleton<Compactor>();
        services.AddSingleton<Reflector>();

        services.AddSingleton<ITool, ReadFileTool>();
        services.AddSingleton<ITool, WriteFileTool>();
        services.AddSingleton<ITool, EditFileTool>();
        services.AddSingleton<ITool, ListDirectoryTool>();
        services.AddSingleton<ITool, GrepTool>();
        // Factory (not AddSingleton<ITool, ShellTool>()) so config.ShellCommandTimeout can be
        // passed through — IToolApprovalGate is still resolved from the container like any other
        // constructor dependency; it isn't registered here (see comment below) but will be by
        // the time this factory actually runs, since each face registers its gate before
        // building the container.
        services.AddSingleton<ITool>(sp => new ShellTool(
            sp.GetRequiredService<IToolApprovalGate>(),
            hardTimeout: config.ShellCommandTimeout));
        // Registered unconditionally (like Anthropic's key below) even without a Tavily key so
        // the tool still appears in the model's tool list; InvokeAsync reports the missing-key
        // error itself rather than the tool silently disappearing when unconfigured.
        services.AddSingleton<ITool>(_ => new WebSearchTool(
            new HttpClient { BaseAddress = new Uri("https://api.tavily.com/") }, config.GetApiKey("tavily")));
        // Deliberately NOT registered here: IToolApprovalGate.
        // It is UI-shaped (a console prompt vs. a browser dialog), so each face
        // registers its own implementation after calling AddLitosAgent(...).

        services.AddSingleton<ISkillDiscovery>(_ => new SkillDiscovery());
        services.AddSingleton<ITool, SkillTool>();
        services.AddSingleton<IProjectInstructionsDiscovery>(_ => new ProjectInstructionsDiscovery());
        services.AddSingleton<ISystemPromptProvider, LitosSystemPromptProvider>();

        services.AddSingleton(new MarkItDownClient());
        services.AddSingleton<IAttachmentConverter, MarkItDownAttachmentConverter>();

        // Unauthenticated (OpenRouter's /models catalog needs no API key) and registered
        // regardless of whether the user has an OpenRouter key — Anthropic/OpenAI/Gemini's
        // ListModelsAsync all consult this to resolve context length for the status-bar meter,
        // independent of whether OpenRouter itself is configured as an active provider below.
        services.AddSingleton(_ => new OpenRouterModelCatalog(new HttpClient { BaseAddress = new Uri("https://openrouter.ai/api/v1/") }));

        var anthropicKey = config.GetApiKey("anthropic");
        services.AddKeyedSingleton<IChatProvider>("anthropic", (sp, _) =>
            new AnthropicChatProvider(
                anthropicKey is null ? new AnthropicClient() : new AnthropicClient(new APIAuthentication(anthropicKey)),
                sp.GetRequiredService<OpenRouterModelCatalog>()));

        var openAiKey = config.GetApiKey("openai");
        if (openAiKey is not null)
            services.AddKeyedSingleton<IChatProvider>("openai", (sp, _) =>
                new OpenAiChatProvider(new OpenAIClient(openAiKey), sp.GetRequiredService<OpenRouterModelCatalog>()));

        var geminiKey = config.GetApiKey("gemini");
        if (geminiKey is not null)
            services.AddKeyedSingleton<IChatProvider>("gemini", (sp, _) =>
                new GeminiChatProvider(new GoogleAi(geminiKey), sp.GetRequiredService<OpenRouterModelCatalog>()));

        var meshApiKey = config.GetApiKey("mesh_api");
        if (meshApiKey is not null)
            services.AddKeyedSingleton<IChatProvider>("mesh_api", (_, _) =>
                new MeshApiChatProvider(CreateMeshApiHttpClient(meshApiKey)));

        var openRouterKey = config.GetApiKey("openrouter");
        if (openRouterKey is not null)
            services.AddKeyedSingleton<IChatProvider>("openrouter", (_, _) =>
                new OpenRouterChatProvider(CreateOpenRouterHttpClient(openRouterKey)));

        // Gated on LocalBaseUrl, not on a key — unlike every other provider here, "local"
        // (LM Studio, Ollama, vLLM, ...) is meant to work with no API key at all, so requiring
        // one the way the other three do would make a key-less local server unreachable.
        if (config.LocalBaseUrl is { } localBaseUrl)
            services.AddKeyedSingleton<IChatProvider>("local", (_, _) =>
                new LocalChatProvider(CreateLocalHttpClient(localBaseUrl, config.GetApiKey("local"))));

        services.AddSingleton<IChatProviderFactory, ChatProviderFactory>();
        services.AddSingleton<AgentLoopFactory>();

        return services;
    }

    private static HttpClient CreateOpenRouterHttpClient(string apiKey)
    {
        var client = new HttpClient { BaseAddress = new Uri("https://openrouter.ai/api/v1/") };
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        return client;
    }

    private static HttpClient CreateMeshApiHttpClient(string apiKey)
    {
        var client = new HttpClient { BaseAddress = new Uri("https://api.meshapi.ai/v1/") };
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        return client;
    }

    private static HttpClient CreateLocalHttpClient(string baseUrl, string? apiKey)
    {
        // Trailing slash required for relative request URIs ("models", "chat/completions") to
        // resolve under BaseAddress rather than replacing its last path segment — same reason
        // CreateOpenRouterHttpClient's literal already ends in "/". A user-typed base URL can't
        // be relied on to already end in one.
        var normalizedBaseUrl = baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/";
        var client = new HttpClient { BaseAddress = new Uri(normalizedBaseUrl) };
        if (apiKey is not null)
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        return client;
    }
}

/// <summary>
/// AgentLoop is bound to one IChatProvider, but the active provider can change at
/// runtime (/provider), so it's created on demand per provider rather than resolved
/// as a fixed DI singleton. The ToolRegistry is likewise supplied per Create() call rather than
/// captured once — the caller (e.g. AgentWorker.RunTurnAsync) builds a fresh snapshot via
/// ToolRegistryFactory.Create() at the moment each turn starts, so a turn sees whatever tools
/// (including any live-discovered MCP tools) are current at that instant.
/// </summary>
public sealed class AgentLoopFactory(
    ITranscriptStore store,
    ContextAccountant accountant,
    ISystemPromptProvider systemPromptProvider,
    Compactor compactor)
{
    public AgentLoop Create(IChatProvider provider, ToolRegistry tools) => new(provider, tools, store, accountant, systemPromptProvider, compactor);
}
