using Litos.SoftwareFactory.Core.Store;
using Microsoft.EntityFrameworkCore;

namespace Litos.SoftwareFactory.Infrastructure.Persistence;

/// <summary>
/// Files attached to threads (attachments-architecture.md §3): recorded on upload, sent with a
/// message by its client id, and never deleted once sent. The bytes are the host's to keep; the
/// store holds only what describes them.
/// </summary>
public sealed partial class EfFactoryStore
{
    public async Task<ThreadAttachment> AddAttachmentAsync(ThreadAttachment attachment, long maxBytesPerThread, CancellationToken ct)
    {
        await using var write = await BeginWriteAsync(ct);
        var db = write.Db;
        // The thread's row lock makes two uploads at once count each other.
        var thread = await LockThreadAsync(db, attachment.ThreadId, ct);

        var held = await db.Attachments.Where(a => a.ThreadId == thread.Id).SumAsync(a => (long?)a.SizeBytes, ct) ?? 0;
        if (held + attachment.SizeBytes > maxBytesPerThread)
            throw new StoreConflictException(
                $"This thread's files would come to {Megabytes(held + attachment.SizeBytes)}, over its limit of {Megabytes(maxBytesPerThread)}.");

        if (attachment.Id == Guid.Empty)
            attachment.Id = Guid.NewGuid();
        attachment.ProjectId = thread.ProjectId;
        attachment.MessageKey = null;
        attachment.SentAt = null;
        db.Attachments.Add(attachment);
        await write.CommitAsync(ct);
        return attachment;
    }

    public async Task<ThreadAttachment?> GetAttachmentAsync(Guid attachmentId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.Attachments.AsNoTracking().FirstOrDefaultAsync(a => a.Id == attachmentId, ct);
    }

    public async Task<IReadOnlyList<ThreadAttachment>> ListAttachmentsAsync(Guid threadId, bool includeUnsent, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var rows = await db.Attachments.AsNoTracking()
            .Where(a => a.ThreadId == threadId && (includeUnsent || a.MessageKey != null))
            .ToListAsync(ct);
        // Sorted here: SQLite cannot order by a DateTimeOffset.
        return [.. rows.OrderBy(a => a.SentAt ?? a.UploadedAt).ThenBy(a => a.UploadedAt)];
    }

    public async Task<IReadOnlyList<ThreadAttachment>> SendAttachmentsAsync(
        Guid threadId, Guid userId, string messageKey, IReadOnlyList<Guid> attachmentIds, DateTimeOffset now, CancellationToken ct)
    {
        if (attachmentIds.Count == 0)
            return [];

        await using var write = await BeginWriteAsync(ct);
        var db = write.Db;
        await LockThreadAsync(db, threadId, ct);

        var found = await db.Attachments.Where(a => attachmentIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
        var sent = new List<ThreadAttachment>();
        foreach (var id in attachmentIds.Distinct())
        {
            if (!found.TryGetValue(id, out var attachment) || attachment.ThreadId != threadId)
                throw new StoreConflictException("One of the files is not attached to this thread. Attach it again.");
            if (attachment.UploadedBy != userId)
                throw new StoreConflictException($"{attachment.FileName} was attached by someone else; only they can send it.");
            if (attachment.MessageKey is { } already && already != messageKey)
                throw new StoreConflictException($"{attachment.FileName} was already sent with another message.");

            attachment.MessageKey = messageKey;
            attachment.SentAt ??= now;
            sent.Add(attachment);
        }

        await write.CommitAsync(ct);
        return sent;
    }

    public async Task UnsendAttachmentsAsync(Guid threadId, string messageKey, CancellationToken ct)
    {
        await using var write = await BeginWriteAsync(ct);
        var db = write.Db;
        foreach (var attachment in await db.Attachments.Where(a => a.ThreadId == threadId && a.MessageKey == messageKey).ToListAsync(ct))
        {
            attachment.MessageKey = null;
            attachment.SentAt = null;
        }

        await write.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<ThreadAttachment>> RemoveUnsentAttachmentsAsync(DateTimeOffset before, CancellationToken ct)
    {
        await using var write = await BeginWriteAsync(ct);
        var db = write.Db;
        var unsent = (await db.Attachments.Where(a => a.MessageKey == null).ToListAsync(ct))
            .Where(a => a.UploadedAt < before)
            .ToList();
        db.Attachments.RemoveRange(unsent);
        await write.CommitAsync(ct);
        return unsent;
    }

    private static string Megabytes(long bytes) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{bytes / (1024d * 1024):0.#} MB");
}
