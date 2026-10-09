using Litos.Agent.Messages;
using Litos.Agent.Providers;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Budget;
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
}
