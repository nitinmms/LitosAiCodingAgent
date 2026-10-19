using Litos.SoftwareFactory.Core.Store;

namespace Litos.SoftwareFactory.Infrastructure.Tests.Persistence;

/// <summary>Files attached to threads (attachments-architecture.md §3).</summary>
public abstract partial class FactoryStoreContract
{
    private static readonly Guid Member = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static ThreadAttachment File(Guid threadId, string name = "shot.png", long size = 1_000, Guid? by = null, DateTimeOffset? at = null) => new()
    {
        ThreadId = threadId,
        FileName = name,
        MediaType = "image/png",
        Kind = AttachmentKind.Image,
        SizeBytes = size,
        Sha256 = new string('a', 64),
        UploadedBy = by ?? Admin,
        UploadedAt = at ?? T0,
    };

    [SkippableFact]
    public async Task AddAttachment_RecordsItUnsent_OnTheThreadsProject()
    {
        var running = await RunningAsync();

        var added = await Store.AddAttachmentAsync(File(running.Thread.Id), 50_000, default);

        var read = await Store.GetAttachmentAsync(added.Id, default);
        Assert.NotNull(read);
        Assert.Equal((running.Project.Id, AttachmentKind.Image, (string?)null), (read.ProjectId, read.Kind, read.MessageKey));
        Assert.Empty(await Store.ListAttachmentsAsync(running.Thread.Id, includeUnsent: false, default));
        Assert.Single(await Store.ListAttachmentsAsync(running.Thread.Id, includeUnsent: true, default));
        Assert.Equal(running.Project.Id, await Store.FindProjectIdAsync(ProjectScoped.Attachment, added.Id, default));
    }

    [SkippableFact]
    public async Task AddAttachment_OverTheThreadsLimit_IsRefused_CountingUnsentFiles()
    {
        var running = await RunningAsync();
        await Store.AddAttachmentAsync(File(running.Thread.Id, size: 30_000), 50_000, default);

        var refused = await Assert.ThrowsAsync<StoreConflictException>(
            () => Store.AddAttachmentAsync(File(running.Thread.Id, "second.png", size: 30_000), 50_000, default));

        Assert.Contains("over its limit", refused.Message);
        Assert.Single(await Store.ListAttachmentsAsync(running.Thread.Id, includeUnsent: true, default));
    }

    [SkippableFact]
    public async Task AddAttachment_ToAThreadThatDoesNotExist_IsNotFound()
    {
        await RunningAsync();

        await Assert.ThrowsAsync<StoreNotFoundException>(() => Store.AddAttachmentAsync(File(Guid.NewGuid()), 50_000, default));
    }

    [SkippableFact]
    public async Task SendAttachments_MarksThemSentWithTheMessage_AndARetrySendsThemAgain()
    {
        var running = await RunningAsync();
        var a = await Store.AddAttachmentAsync(File(running.Thread.Id, "a.png"), 50_000, default);
        var b = await Store.AddAttachmentAsync(File(running.Thread.Id, "b.png"), 50_000, default);

        var sent = await Store.SendAttachmentsAsync(running.Thread.Id, Admin, "msg-1", [b.Id, a.Id], T0.AddMinutes(1), default);
        var again = await Store.SendAttachmentsAsync(running.Thread.Id, Admin, "msg-1", [b.Id, a.Id], T0.AddMinutes(2), default);

        Assert.Equal(["b.png", "a.png"], sent.Select(s => s.FileName));
        Assert.Equal(2, again.Count);
        var listed = await Store.ListAttachmentsAsync(running.Thread.Id, includeUnsent: false, default);
        Assert.All(listed, l => Assert.Equal(("msg-1", (DateTimeOffset?)T0.AddMinutes(1)), (l.MessageKey, l.SentAt)));
    }

    public static TheoryData<string, string> SendRefusals => new()
    {
        { "another thread's", "not attached to this thread" },
        { "someone else's", "only they can send it" },
        { "sent with another message", "already sent with another message" },
    };

    [SkippableTheory]
    [MemberData(nameof(SendRefusals))]
    public async Task SendAttachments_ThatMayNotBeSent_AreRefused_AndNothingIsChanged(string which, string reason)
    {
        var running = await RunningAsync();
        var other = await AddThreadAsync(running.Project, "Other");
        var mine = await Store.AddAttachmentAsync(File(running.Thread.Id, "mine.png"), 50_000, default);
        var bad = which switch
        {
            "another thread's" => await Store.AddAttachmentAsync(File(other.Id), 50_000, default),
            "someone else's" => await Store.AddAttachmentAsync(File(running.Thread.Id, by: Member), 50_000, default),
            _ => await Store.AddAttachmentAsync(File(running.Thread.Id), 50_000, default),
        };
        if (which == "sent with another message")
            await Store.SendAttachmentsAsync(running.Thread.Id, Admin, "msg-0", [bad.Id], T0, default);

        var refused = await Assert.ThrowsAsync<StoreConflictException>(
            () => Store.SendAttachmentsAsync(running.Thread.Id, Admin, "msg-1", [mine.Id, bad.Id], T0, default));

        Assert.Contains(reason, refused.Message);
        Assert.Null((await Store.GetAttachmentAsync(mine.Id, default))!.MessageKey);
    }

    [SkippableFact]
    public async Task UnsendAttachments_LetsThemBeSentAgain()
    {
        var running = await RunningAsync();
        var a = await Store.AddAttachmentAsync(File(running.Thread.Id), 50_000, default);
        await Store.SendAttachmentsAsync(running.Thread.Id, Admin, "msg-1", [a.Id], T0, default);

        await Store.UnsendAttachmentsAsync(running.Thread.Id, "msg-1", default);

        Assert.Null((await Store.GetAttachmentAsync(a.Id, default))!.MessageKey);
        await Store.SendAttachmentsAsync(running.Thread.Id, Admin, "msg-2", [a.Id], T0, default);
    }

    [SkippableFact]
    public async Task RemoveUnsentAttachments_TakesOnlyOldUnsentOnes()
    {
        var running = await RunningAsync();
        var old = await Store.AddAttachmentAsync(File(running.Thread.Id, "old.png", at: T0), 50_000, default);
        var fresh = await Store.AddAttachmentAsync(File(running.Thread.Id, "fresh.png", at: T0.AddDays(1)), 50_000, default);
        var sent = await Store.AddAttachmentAsync(File(running.Thread.Id, "sent.png", at: T0), 50_000, default);
        await Store.SendAttachmentsAsync(running.Thread.Id, Admin, "msg-1", [sent.Id], T0, default);

        var removed = await Store.RemoveUnsentAttachmentsAsync(T0.AddHours(12), default);

        Assert.Equal([old.Id], removed.Select(r => r.Id));
        Assert.Equal(["fresh.png", "sent.png"], (await Store.ListAttachmentsAsync(running.Thread.Id, true, default)).Select(a => a.FileName).Order());
    }
}
