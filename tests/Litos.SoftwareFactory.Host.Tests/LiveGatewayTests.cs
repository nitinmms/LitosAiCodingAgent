using Litos.Agent.Messages;
using Litos.Agent.Providers;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Budget;
using Litos.SoftwareFactory.Core.Settings;
using Litos.SoftwareFactory.Host.Gateway;
using Litos.SoftwareFactory.Host.Runs;
using Litos.SoftwareFactory.Host.Settings;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Litos.SoftwareFactory.Host.Tests;

/// <summary>
/// One real model call through the gateway, to the real provider. It spends a few hundred
/// tokens, so it runs only when asked for: set FACTORY_LIVE_TEST=1 with OPENROUTER_API_KEY in
/// the environment. Everything else in this project scripts the model.
/// </summary>
public sealed class LiveGatewayTests(ITestOutputHelper output)
{
    [SkippableFact]
    public async Task OneRealCall_IsAdmitted_CappedAndSettledAgainstReportedUsage()
    {
        var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        Skip.If(
            Environment.GetEnvironmentVariable("FACTORY_LIVE_TEST") != "1" || string.IsNullOrWhiteSpace(key),
            "Set FACTORY_LIVE_TEST=1 and OPENROUTER_API_KEY to make one real model call.");

        await using var host = await TestHost.StartAsync(
            options =>
            {
                options.OpenRouterApiKey = key;
                options.Budget = new BudgetPolicy { OutputAllowanceTokens = 256, Margin = 0.10 };
            },
            services => services.AddSingleton<IChatProviderFactory, FactoryProviders>(),
            startCoordinator: false);

        var threadId = await host.CreateThreadAsync(await host.RegisterProjectAsync(), budgetCap: 20_000);
        await host.DelegateAsync(threadId);
        var claimed = (await host.Store.ClaimNextRunAsync(1, DateTimeOffset.UtcNow, default))!;
        var run = new ActiveRun(claimed.Run.Id, threadId, claimed.Run.RequestedBy, FactoryOptions.OpenRouter, host.Options.Model);

        var request = new GatewayRequest(Guid.NewGuid().ToString(), new ChatRequest(
            [ChatMessage.User("Reply with exactly one word: pong")], [], "ignored-by-the-gateway",
            SystemPrompt: "You are a connectivity check. Answer in one word.", SessionId: "live-check"));
        var events = new List<GatewayEvent>();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await host.App.Services.GetRequiredService<ModelGateway>().HandleAsync(run, request, evt =>
        {
            events.Add(evt);
            return Task.CompletedTask;
        }, timeout.Token);

        var error = events.OfType<GatewayError>().FirstOrDefault();
        Assert.True(error is null, $"The provider call failed: {error?.Code}: {error?.Message}");
        var completed = Assert.IsType<GatewayMessageCompleted>(events[^1]);
        var reply = string.Concat(completed.Message.Content.OfType<TextBlock>().Select(t => t.Text)).Trim();
        var entry = Assert.Single(await host.Store.ListUsageAsync(threadId, default));
        var thread = (await host.ThreadAsync(threadId)).Thread;

        output.WriteLine($"model:            {host.Options.Model}");
        output.WriteLine($"reply:            {reply}");
        output.WriteLine($"estimated input:  {entry.EstimatedInput} (raw {entry.EstimatedInputRaw})");
        output.WriteLine($"reported input:   {entry.ActualInput} (cached {entry.ActualCachedInput})");
        output.WriteLine($"reported output:  {entry.ActualOutput} (reasoning {entry.ActualReasoning})");
        output.WriteLine($"reserved:         {entry.Reserved}");
        output.WriteLine($"charged:          {entry.Charged}");
        output.WriteLine($"thread used/cap:  {thread.TokensUsed}/{thread.BudgetCap}, reserved {thread.TokensReserved}");

        Assert.Contains("pong", reply, StringComparison.OrdinalIgnoreCase);
        Assert.True(completed.Usage.TotalInputTokens > 0, "The provider reported no input tokens.");
        Assert.True(completed.Usage.OutputTokens > 0, "The provider reported no output tokens.");
        Assert.True(completed.Usage.OutputTokens <= 256, "The output cap was not honoured.");
        Assert.Equal(UsageStatus.Settled, entry.Status);
        Assert.Equal(entry.ActualInput + entry.ActualOutput, entry.Charged);
        Assert.Equal((entry.Charged, 0L), (thread.TokensUsed, thread.TokensReserved));
    }

    /// <summary>
    /// An image through the gateway to each provider (attachments-architecture.md §8, step 1):
    /// the same question with and without the repository's agent-loop diagram, so the difference
    /// in reported input is what the image cost. Set FACTORY_LIVE_TEST=1 and the provider's key
    /// variable; the key is set as the factory's own secret, as an Admin would.
    /// </summary>
    [SkippableTheory]
    [InlineData("anthropic", "claude-haiku-5-5", "ANTHROPIC_API_KEY", true)]
    [InlineData("openai", "gpt-6-luna", "OPENAI_API_KEY", true)]
    [InlineData("gemini", "gemini-3.1-flash-lite", "GEMINI_API_KEY", true)]
    [InlineData("openrouter", "google/gemini-2.5-flash", "OPENROUTER_API_KEY", true)]
    [InlineData("openrouter", "deepseek/deepseek-v4.1-flash", "OPENROUTER_API_KEY", false)]
    public async Task AnImage_ReachesTheProvider_AndIsChargedAsItReports(string provider, string model, string keyVariable, bool seesImages)
    {
        var key = Environment.GetEnvironmentVariable(keyVariable);
        Skip.If(
            Environment.GetEnvironmentVariable("FACTORY_LIVE_TEST") != "1" || string.IsNullOrWhiteSpace(key),
            $"Set FACTORY_LIVE_TEST=1 and {keyVariable} to send a real image.");
        var png = await File.ReadAllBytesAsync(Path.Combine(RepositoryRoot(), "Litos-Agent-Loop.png"));

        await using var host = await TestHost.StartAsync(
            options => options.Budget = new BudgetPolicy { OutputAllowanceTokens = 2_048, Margin = 0.10 },
            services => services.AddSingleton<IChatProviderFactory, FactoryProviders>(),
            startCoordinator: false);
        await host.App.Services.GetRequiredService<FactorySettings>().SetSecretAsync(SecretNames.Provider(provider), key!, null, default);

        var threadId = await host.CreateThreadAsync(await host.RegisterProjectAsync(), budgetCap: 100_000);
        await host.DelegateAsync(threadId);
        var claimed = (await host.Store.ClaimNextRunAsync(1, DateTimeOffset.UtcNow, default))!;
        var run = new ActiveRun(claimed.Run.Id, threadId, claimed.Run.RequestedBy, provider, model);
        var gateway = host.App.Services.GetRequiredService<ModelGateway>();

        async Task<(GatewayMessageCompleted? Completed, GatewayError? Error)> AskAsync(string session, params ContentBlock[] content)
        {
            var events = new List<GatewayEvent>();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            await gateway.HandleAsync(run, new GatewayRequest(Guid.NewGuid().ToString(), new ChatRequest(
                [new ChatMessage(Role.User, content)], [], "ignored-by-the-gateway",
                SystemPrompt: "Answer in one short sentence.", SessionId: session)), evt =>
            {
                events.Add(evt);
                return Task.CompletedTask;
            }, timeout.Token);
            return (events.OfType<GatewayMessageCompleted>().LastOrDefault(), events.OfType<GatewayError>().FirstOrDefault());
        }

        const string Question = "What is the title at the top of the attached diagram? If nothing is attached, say 'no image'.";
        var (plain, plainError) = await AskAsync("text-only", new TextBlock(Question));
        var (withImage, imageError) = await AskAsync("with-image", new TextBlock(Question), new ImageBlock("image/png", png));
        var entries = await host.Store.ListUsageAsync(threadId, default);

        output.WriteLine($"provider/model:   {provider} / {model}");
        output.WriteLine($"text only:        {Describe(plain, plainError)}");
        output.WriteLine($"with the image:   {Describe(withImage, imageError)}");
        foreach (var entry in entries)
            output.WriteLine($"  call: estimated {entry.EstimatedInput}, reported input {entry.ActualInput}, output {entry.ActualOutput}, served by {entry.ServedBy}, charged {entry.Charged}");
        if (plain is not null && withImage is not null)
            output.WriteLine($"the image cost:   {withImage.Usage.TotalInputTokens - plain.Usage.TotalInputTokens} input tokens");

        Assert.True(plainError is null, $"The text-only call failed: {plainError?.Message}");
        if (!seesImages)
            return; // recorded, not asserted: what a text-only model does with an image is the finding

        Assert.True(imageError is null, $"The image call failed: {imageError?.Message}");
        Assert.True(withImage!.Usage.TotalInputTokens > plain!.Usage.TotalInputTokens + 200, "The image was not charged: did it reach the model?");
        Assert.DoesNotContain("no image", Text(withImage), StringComparison.OrdinalIgnoreCase);
        Assert.All(entries, e => Assert.Equal(UsageStatus.Settled, e.Status));
    }

    private static string Text(GatewayMessageCompleted completed) =>
        string.Concat(completed.Message.Content.OfType<TextBlock>().Select(t => t.Text)).Trim();

    private static string Describe(GatewayMessageCompleted? completed, GatewayError? error) =>
        error is not null ? $"ERROR {error.Code}: {error.Message}"
        : completed is null ? "no reply"
        : $"input {completed.Usage.TotalInputTokens}, output {completed.Usage.OutputTokens}: {Text(completed)}";

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "LitosAiAgent.slnx")))
                return dir.FullName;
        throw new InvalidOperationException("The repository root was not found.");
    }
}
