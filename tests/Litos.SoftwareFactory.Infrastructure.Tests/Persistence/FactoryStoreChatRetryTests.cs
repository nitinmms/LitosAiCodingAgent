using System.Text.Json;
using Litos.Agent.Streaming;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Orchestration;
using Litos.SoftwareFactory.Core.Store;

namespace Litos.SoftwareFactory.Infrastructure.Tests.Persistence;

/// <summary>
/// A chat answer its own allowance stopped: its note says so, and anyone on the project can ask the
/// same question again with a larger allowance, once.
/// </summary>
public abstract partial class FactoryStoreContract
{
    private const string Question = "Draw the release pipeline as a page.";

    private static ChatAnswerPayload? AnswerPayloadOf(ThreadMessage message) =>
        message.PayloadJson is { } json ? JsonSerializer.Deserialize<ChatAnswerPayload>(json, FactoryWire.Json) : null;

    /// <summary>A thread whose one chat answer was stopped by its 100,000-token allowance after spending 96,000.</summary>
    private async Task<(TaskThread Thread, Guid Stopped)> StoppedChatAsync()
    {
        var thread = await AddThreadAsync(await AddProjectAsync());
        var runId = (await ChatAsync(thread.Id, Question)).RunId!.Value;
        await Store.ReserveAsync(new ReserveCommand("stop-1", thread.Id, runId, Ben, "openrouter", "m", 10_000, 10_000), Real, T0, default);
        await Store.SettleAsync("stop-1", new UsageInfo(90_000, 6_000), 96_000, T0, default);
        await Store.FinishChatRunAsync(runId, null, "Litos stopped before answering: this answer's allowance ran out.", T0, default, stoppedByAllowance: true);
        return (thread, runId);
    }

    private async Task<ThreadMessage> LastFactoryNoteAsync(Guid threadId) =>
        (await Store.GetThreadAsync(threadId, default))!.Messages.Last(m => m.Author == MessageAuthor.Factory);

    [SkippableFact]
    public async Task AChatStoppedByItsAllowance_IsRecordedSo_AndItsNoteSaysWhatTheAllowanceWas()
    {
        var (thread, stopped) = await StoppedChatAsync();

        Assert.Equal(StopReason.BudgetExhausted, (await Store.GetRunAsync(stopped, default))!.StopReason);
        var note = await LastFactoryNoteAsync(thread.Id);
        Assert.Equal(MessageKind.Status, note.Kind);
        var payload = AnswerPayloadOf(note)!;
        Assert.Equal(new ChatStop(stopped, 100_000, 96_000), payload.ChatStop);
        Assert.Empty(payload.Artifacts);
    }

    /// <summary>A page published before the stop is still shown, next to the stop.</summary>
    [SkippableFact]
    public async Task AChatStoppedAfterPublishing_KeepsThePageOnTheNote_BesideTheStop()
    {
        var thread = await AddThreadAsync(await AddProjectAsync());
        var runId = (await ChatAsync(thread.Id, Question)).RunId!.Value;
        var (page, _) = await Store.PublishArtifactAsync(runId, null, "Pipeline", "<p>v1</p>", 1_000_000, T0, default);

        await Store.FinishChatRunAsync(runId, null, "Stopped.", T0, default, stoppedByAllowance: true);

        var payload = AnswerPayloadOf(await LastFactoryNoteAsync(thread.Id))!;
        Assert.Equal([new PublishedArtifact(page.Id, 1)], payload.Artifacts);
        Assert.Equal(runId, payload.ChatStop!.RunId);
        // The page links still read from the same payload.
        Assert.Equal([new PublishedArtifact(page.Id, 1)], ArtifactsOn(await LastFactoryNoteAsync(thread.Id))!.Artifacts);
    }

    /// <summary>A failure that was not the allowance (the quota, a timeout) offers no retry.</summary>
    [SkippableFact]
    public async Task AChatThatFailedOtherwise_HasNoStop_AndCannotBeRetried()
    {
        var thread = await AddThreadAsync(await AddProjectAsync());
        var runId = (await ChatAsync(thread.Id, Question)).RunId!.Value;
        await Store.FinishChatRunAsync(runId, null, "Litos took longer than 10 minutes.", T0, default);

        Assert.Null((await LastFactoryNoteAsync(thread.Id)).PayloadJson);
        Assert.Null((await Store.GetRunAsync(runId, default))!.StopReason);
        await Assert.ThrowsAsync<StoreConflictException>(() => Store.RetryChatAsync(thread.Id, runId, Ben, 200_000, T0, default));
    }

    /// <summary>A reply is an answer, whatever the flag says: nothing to retry.</summary>
    [SkippableFact]
    public async Task AChatThatReplied_IsNeverMarkedStopped()
    {
        var thread = await AddThreadAsync(await AddProjectAsync());
        var runId = (await ChatAsync(thread.Id, Question)).RunId!.Value;

        await Store.FinishChatRunAsync(runId, "Here it is.", null, T0, default, stoppedByAllowance: true);

        Assert.Null((await Store.GetRunAsync(runId, default))!.StopReason);
        Assert.Null((await LastFactoryNoteAsync(thread.Id)).PayloadJson);
    }

    [SkippableFact]
    public async Task RetryChat_AsksTheSameQuestion_WithTheLargerAllowance_AndSaysSo()
    {
        var (thread, stopped) = await StoppedChatAsync();

        var result = await Store.RetryChatAsync(thread.Id, stopped, Erin, 150_000, T0.AddMinutes(1), default);

        Assert.Equal(DispatchOutcome.Chat, result.Outcome);
        var retry = (await Store.GetRunAsync(result.RunId!.Value, default))!;
        Assert.Equal((RunKind.Chat, Question, 150_000L, Erin), (retry.Kind, retry.Request, retry.ChatBudgetCap, retry.RequestedBy));
        Assert.Equal((0L, 0L), (retry.ChatTokensUsed, retry.ChatTokensReserved));

        var details = (await Store.GetThreadAsync(thread.Id, default))!;
        Assert.Equal(retry.Id, details.ChatRun!.Id);
        var note = details.Messages.Last();
        Assert.Equal((MessageAuthor.User, Erin, MessageKind.Status, "Tried again with an allowance of 150,000 tokens."), (note.Author, note.AuthorUserId, note.Kind, note.Text));
        Assert.Equal(new ChatRetry(stopped, 150_000), JsonSerializer.Deserialize<ChatRetry>(note.PayloadJson!, FactoryWire.Json));
    }

    /// <summary>A retry starts empty: the brief reads the thread up to the question, as the first attempt did.</summary>
    [SkippableFact]
    public async Task RetryChat_LeavesTheTasksBudgetAlone()
    {
        var (thread, stopped) = await StoppedChatAsync();
        var before = await ThreadAsync(thread.Id);

        await Store.RetryChatAsync(thread.Id, stopped, Ben, 150_000, T0, default);

        var after = await ThreadAsync(thread.Id);
        Assert.Equal((before.BudgetCap, before.TokensUsed, before.TokensReserved, before.State), (after.BudgetCap, after.TokensUsed, after.TokensReserved, after.State));
    }

    /// <summary>Two people pressing Try again, or one pressing twice: one retry.</summary>
    [SkippableFact]
    public async Task RetryChat_Twice_IsOneRetry()
    {
        var (thread, stopped) = await StoppedChatAsync();
        await Store.RetryChatAsync(thread.Id, stopped, Ben, 150_000, T0, default);

        var again = await Store.RetryChatAsync(thread.Id, stopped, Erin, 200_000, T0, default);

        Assert.Equal(DispatchOutcome.Duplicate, again.Outcome);
        Assert.Null(again.RunId);
        Assert.Single((await Store.GetThreadAsync(thread.Id, default))!.Messages, m => m.Text.StartsWith("Tried again", StringComparison.Ordinal));
    }

    /// <summary>No ceiling, but it must be larger: the same allowance would stop it again.</summary>
    [SkippableTheory]
    [InlineData(100_000)]
    [InlineData(50_000)]
    public async Task RetryChat_WithAnAllowanceNotAboveTheOld_IsRefused(long allowance)
    {
        var (thread, stopped) = await StoppedChatAsync();

        var refused = await Assert.ThrowsAsync<StoreConflictException>(() => Store.RetryChatAsync(thread.Id, stopped, Ben, allowance, T0, default));

        Assert.Contains("above 100,000", refused.Message);
        Assert.Null((await Store.GetThreadAsync(thread.Id, default))!.ChatRun);
    }

    [SkippableFact]
    public async Task RetryChat_WithAVeryLargeAllowance_IsAllowed()
    {
        var (thread, stopped) = await StoppedChatAsync();

        var result = await Store.RetryChatAsync(thread.Id, stopped, Ben, 50_000_000, T0, default);

        Assert.Equal(50_000_000L, (await Store.GetRunAsync(result.RunId!.Value, default))!.ChatBudgetCap);
    }

    [SkippableFact]
    public async Task RetryChat_WhileAnotherAnswerIsRunning_IsRejected()
    {
        var (thread, stopped) = await StoppedChatAsync();
        await ChatAsync(thread.Id, "Meanwhile, where is the CSV export?");

        var result = await Store.RetryChatAsync(thread.Id, stopped, Ben, 150_000, T0, default);

        Assert.Equal(DispatchOutcome.Rejected, result.Outcome);
        Assert.Contains("still answering", result.Reason);
        // Rejected, not used up: it can be retried once the other answer is done.
        Assert.DoesNotContain((await Store.GetThreadAsync(thread.Id, default))!.Messages, m => m.Text.StartsWith("Tried again", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task RetryChat_OfAnotherThreadsRun_OrAnUnknownOne_IsNotFound()
    {
        var (_, theirs) = await StoppedChatAsync();
        var mine = await AddThreadAsync(await AddProjectAsync("billing"));

        await Assert.ThrowsAsync<StoreNotFoundException>(() => Store.RetryChatAsync(mine.Id, theirs, Ben, 150_000, T0, default));
        await Assert.ThrowsAsync<StoreNotFoundException>(() => Store.RetryChatAsync(mine.Id, Guid.NewGuid(), Ben, 150_000, T0, default));
    }

    /// <summary>A retry its own allowance stops again can be retried again, larger still.</summary>
    [SkippableFact]
    public async Task ARetryStoppedAgain_CanItselfBeRetried()
    {
        var (thread, stopped) = await StoppedChatAsync();
        var retry = (await Store.RetryChatAsync(thread.Id, stopped, Ben, 150_000, T0, default)).RunId!.Value;
        await Store.FinishChatRunAsync(retry, null, "Stopped again.", T0, default, stoppedByAllowance: true);

        var second = await Store.RetryChatAsync(thread.Id, retry, Ben, 250_000, T0, default);

        Assert.Equal(DispatchOutcome.Chat, second.Outcome);
        Assert.Equal((Question, 250_000L), ((await Store.GetRunAsync(second.RunId!.Value, default))!.Request, (await Store.GetRunAsync(second.RunId!.Value, default))!.ChatBudgetCap));
    }
}
