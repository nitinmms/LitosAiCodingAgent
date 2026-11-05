using System.Text.Json;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Store;

namespace Litos.SoftwareFactory.Infrastructure.Tests.Persistence;

/// <summary>Pages a chat answer publishes on its thread (ReadMe_ArtifactsFeature.md §11.5).</summary>
public abstract partial class FactoryStoreContract
{
    private const long ArtifactThreadLimit = 1_000_000;

    private async Task<(TaskThread Thread, Guid RunId)> ChattingAsync(string repository = "salesapp")
    {
        var thread = await AddThreadAsync(await AddProjectAsync(repository));
        return (thread, (await ChatAsync(thread.Id, "Draw me the pipeline.")).RunId!.Value);
    }

    private static MessageArtifacts? ArtifactsOn(ThreadMessage message) =>
        message.PayloadJson is { } json ? JsonSerializer.Deserialize<MessageArtifacts>(json, FactoryWire.Json) : null;

    [SkippableFact]
    public async Task PublishArtifact_New_IsVersionOne_OnTheRunsThreadAndProject_ByTheAsker()
    {
        var (thread, runId) = await ChattingAsync();

        var (artifact, created) = await Store.PublishArtifactAsync(runId, null, "Pipeline", "<p>v1</p>", ArtifactThreadLimit, T0, default);

        Assert.True(created);
        Assert.Equal((thread.Id, thread.ProjectId, "Pipeline", 1, Ben), (artifact.ThreadId, artifact.ProjectId, artifact.Title, artifact.CurrentVersion, artifact.CreatedBy));
        var version = await Store.GetArtifactVersionAsync(artifact.Id, null, default);
        Assert.Equal(("<p>v1</p>", 9L, runId), (version!.Html, version.SizeBytes, version.RunId));
        Assert.Equal(thread.ProjectId, await Store.FindProjectIdAsync(ProjectScoped.Artifact, artifact.Id, default));
    }

    [SkippableFact]
    public async Task PublishArtifact_WithItsId_AddsAVersion_KeepingTheEarlierOne()
    {
        var (_, runId) = await ChattingAsync();
        var (first, _) = await Store.PublishArtifactAsync(runId, null, "Pipeline", "<p>v1</p>", ArtifactThreadLimit, T0, default);

        var (second, created) = await Store.PublishArtifactAsync(runId, first.Id, "Pipeline, red", "<p>v2</p>", ArtifactThreadLimit, T0.AddMinutes(1), default);

        Assert.False(created);
        Assert.Equal((first.Id, 2, "Pipeline, red", T0.AddMinutes(1)), (second.Id, second.CurrentVersion, second.Title, second.UpdatedAt));
        Assert.Equal("<p>v1</p>", (await Store.GetArtifactVersionAsync(first.Id, 1, default))!.Html);
        Assert.Equal("<p>v2</p>", (await Store.GetArtifactVersionAsync(first.Id, null, default))!.Html);
        Assert.Null(await Store.GetArtifactVersionAsync(first.Id, 3, default));
        Assert.Equal(2, (await Store.GetArtifactAsync(first.Id, default))!.CurrentVersion);
    }

    [SkippableFact]
    public async Task PublishArtifact_WithAnotherThreadsId_IsNotFound_AndChangesNothing()
    {
        var (_, otherRun) = await ChattingAsync("billing");
        var (theirs, _) = await Store.PublishArtifactAsync(otherRun, null, "Theirs", "<p>x</p>", ArtifactThreadLimit, T0, default);
        var (mine, myRun) = await ChattingAsync();

        await Assert.ThrowsAsync<StoreNotFoundException>(
            () => Store.PublishArtifactAsync(myRun, theirs.Id, "Hijack", "<p>y</p>", ArtifactThreadLimit, T0, default));
        await Assert.ThrowsAsync<StoreNotFoundException>(
            () => Store.PublishArtifactAsync(myRun, Guid.NewGuid(), "Unknown", "<p>y</p>", ArtifactThreadLimit, T0, default));
        await Assert.ThrowsAsync<StoreNotFoundException>(
            () => Store.PublishArtifactAsync(Guid.NewGuid(), null, "No run", "<p>y</p>", ArtifactThreadLimit, T0, default));

        Assert.Equal(1, (await Store.GetArtifactAsync(theirs.Id, default))!.CurrentVersion);
        Assert.Empty(await Store.ListArtifactsAsync(mine.Id, default));
    }

    [SkippableFact]
    public async Task PublishArtifact_OverTheThreadsLimit_IsRefused_CountingEveryVersion()
    {
        var (thread, runId) = await ChattingAsync();
        var (artifact, _) = await Store.PublishArtifactAsync(runId, null, "A", new string('a', 600), 1_000, T0, default);

        var refused = await Assert.ThrowsAsync<StoreConflictException>(
            () => Store.PublishArtifactAsync(runId, artifact.Id, "A", new string('b', 600), 1_000, T0, default));

        Assert.Contains("over its limit", refused.Message);
        Assert.Equal(1, (await Store.GetArtifactAsync(artifact.Id, default))!.CurrentVersion);
        Assert.Single(await Store.ListArtifactsAsync(thread.Id, default));
    }

    [SkippableFact]
    public async Task PublishArtifact_CountsUtf8Bytes_NotCharacters()
    {
        var (_, runId) = await ChattingAsync();

        var (artifact, _) = await Store.PublishArtifactAsync(runId, null, "Ünïcödé", "é€", ArtifactThreadLimit, T0, default);

        Assert.Equal(5L, (await Store.GetArtifactVersionAsync(artifact.Id, null, default))!.SizeBytes);
    }

    [SkippableFact]
    public async Task ListArtifacts_IsTheThreadsOwn_OldestFirst()
    {
        var (thread, runId) = await ChattingAsync();
        var (first, _) = await Store.PublishArtifactAsync(runId, null, "First", "<p>1</p>", ArtifactThreadLimit, T0, default);
        var (second, _) = await Store.PublishArtifactAsync(runId, null, "Second", "<p>2</p>", ArtifactThreadLimit, T0.AddMinutes(1), default);
        var (_, otherRun) = await ChattingAsync("billing");
        await Store.PublishArtifactAsync(otherRun, null, "Elsewhere", "<p>3</p>", ArtifactThreadLimit, T0, default);

        Assert.Equal([first.Id, second.Id], (await Store.ListArtifactsAsync(thread.Id, default)).Select(a => a.Id));
        Assert.Null(await Store.GetArtifactAsync(Guid.NewGuid(), default));
    }

    [SkippableFact]
    public async Task FinishingAChat_ShowsThePagesThatRunPublished_OnTheAnswer_AtTheirLastVersion()
    {
        var (thread, runId) = await ChattingAsync();
        var (a, _) = await Store.PublishArtifactAsync(runId, null, "A", "<p>a1</p>", ArtifactThreadLimit, T0, default);
        var (b, _) = await Store.PublishArtifactAsync(runId, null, "B", "<p>b1</p>", ArtifactThreadLimit, T0.AddSeconds(1), default);
        await Store.PublishArtifactAsync(runId, a.Id, "A", "<p>a2</p>", ArtifactThreadLimit, T0.AddSeconds(2), default);

        await Store.FinishChatRunAsync(runId, "Here are two pages.", null, T0.AddSeconds(3), default);

        var answer = (await Store.GetThreadAsync(thread.Id, default))!.Messages.Last(m => m.Author == MessageAuthor.Factory);
        Assert.Equal(MessageKind.Text, answer.Kind);
        Assert.Equal([new PublishedArtifact(a.Id, 2), new PublishedArtifact(b.Id, 1)], ArtifactsOn(answer)!.Artifacts);
    }

    [SkippableFact]
    public async Task FinishingAChat_ThatFailedAfterPublishing_ShowsThePageOnTheFailureNote()
    {
        var (thread, runId) = await ChattingAsync();
        var (a, _) = await Store.PublishArtifactAsync(runId, null, "A", "<p>a</p>", ArtifactThreadLimit, T0, default);

        await Store.FinishChatRunAsync(runId, null, "Litos stopped before answering.", T0, default);

        var note = (await Store.GetThreadAsync(thread.Id, default))!.Messages.Last(m => m.Author == MessageAuthor.Factory);
        Assert.Equal(MessageKind.Status, note.Kind);
        Assert.Equal([new PublishedArtifact(a.Id, 1)], ArtifactsOn(note)!.Artifacts);
    }

    [SkippableFact]
    public async Task FinishingAChat_ThatPublishedNothing_HasNoPayload_EvenWhenAnEarlierAnswerDid()
    {
        var (thread, first) = await ChattingAsync();
        await Store.PublishArtifactAsync(first, null, "A", "<p>a</p>", ArtifactThreadLimit, T0, default);
        await Store.FinishChatRunAsync(first, "A page.", null, T0, default);
        var second = (await ChatAsync(thread.Id, "And in words?")).RunId!.Value;

        await Store.FinishChatRunAsync(second, "In words.", null, T0, default);

        var answer = (await Store.GetThreadAsync(thread.Id, default))!.Messages.Last(m => m.Author == MessageAuthor.Factory);
        Assert.Null(answer.PayloadJson);
    }
}
