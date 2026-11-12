using Litos.Agent.Streaming;
using Litos.SoftwareFactory.Core.Store;
using Microsoft.EntityFrameworkCore;

namespace Litos.SoftwareFactory.Infrastructure.Tests.Persistence;

/// <summary>The rows the chat report is read from: chat answers in the period, and task work beside them.</summary>
public abstract partial class FactoryStoreContract
{
    private async Task<Guid> ChargedChatAsync(Guid threadId, string key, long charge, string question = "Where is it?")
    {
        var runId = (await ChatAsync(threadId, question)).RunId!.Value;
        await Store.ReserveAsync(new ReserveCommand(key, threadId, runId, Ben, "openrouter", "m", 1_000, 1_000), Real, T0, default);
        await Store.SettleAsync(key, new UsageInfo((int)charge - 100, 100), charge, T0, default);
        await Store.FinishChatRunAsync(runId, "Answered.", null, T0, default);
        return runId;
    }

    [SkippableFact]
    public async Task ChatMetricsData_IsEveryAnswerInThePeriod_WithItsChargeAndWhetherItMadeAnArtifact_AndTaskWorkBeside()
    {
        var running = await RunningAsync();
        await Store.ReserveAsync(new ReserveCommand("task-1", running.Thread.Id, running.Run.Id, Admin, "openrouter", "m", 1_000, 1_000), Policy, T0, default);
        await Store.SettleAsync("task-1", new UsageInfo(1_000, 100), 1_100, T0, default);

        var plain = await ChargedChatAsync(running.Thread.Id, "chat-plain", 3_000);
        // An older task's thread: chat is counted by when it was asked, not when the task began.
        var other = await AddThreadAsync(running.Project, "Older task");
        var withPage = (await ChatAsync(other.Id, "Draw it.")).RunId!.Value;
        await Store.PublishArtifactAsync(withPage, null, "Pipeline", "<p>x</p>", 1_000_000, T0, default);
        await Store.ReserveAsync(new ReserveCommand("chat-page", other.Id, withPage, Ben, "openrouter", "m", 1_000, 1_000), Real, T0, default);
        await Store.SettleAsync("chat-page", new UsageInfo(40_000, 2_000), 42_000, T0, default);
        await Store.FinishChatRunAsync(withPage, "Here it is.", null, T0, default);

        var old = await ChargedChatAsync(other.Id, "chat-old", 9_000, "Long ago?");
        var elsewhere = await RunningAsync("billing");
        await ChargedChatAsync(elsewhere.Thread.Id, "chat-billing", 5_000);
        await using (var db = Contexts.CreateDbContext())
        {
            (await db.Runs.FirstAsync(r => r.Id == old)).CreatedAt = T0.AddDays(-60);
            foreach (var entry in await db.Usage.Where(u => u.RunId == old).ToListAsync())
                entry.CreatedAt = T0.AddDays(-60);
            await db.SaveChangesAsync();
        }

        var data = await Store.GetChatMetricsDataAsync([running.Project.Id], T0.AddDays(-30), default);

        Assert.Equal(
            [(plain, running.Thread.Id, 3_000L, false), (withPage, other.Id, 42_000L, true)],
            data.Answers.OrderBy(a => a.Tokens).Select(a => (a.RunId, a.ThreadId, a.Tokens, a.Artifact)));
        Assert.All(data.Answers, a => Assert.Equal(running.Project.Id, a.ProjectId));
        Assert.Equal("Older task", data.Answers.Single(a => a.RunId == withPage).TaskTitle);
        Assert.Equal(1_100, data.TaskTokens);

        var everywhere = await Store.GetChatMetricsDataAsync(null, T0.AddDays(-30), default);
        Assert.Equal(3, everywhere.Answers.Count);
        Assert.Equal(1_100, everywhere.TaskTokens); // billing's task has spent nothing; its chat is chat, not task work
    }
}
