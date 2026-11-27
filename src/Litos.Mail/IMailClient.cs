namespace Litos.Mail;

/// <summary>
/// The narrow mail surface the broker is allowed to use — read-only plus drafting, and nothing else.
/// There is deliberately no delete, no move, no mark-as-read and no send: a capability that does not
/// exist on this interface cannot be reached by a model, a confused provider reply or a
/// hand-edited configuration, which is what "mail is never deleted" has to mean in practice
/// (Implement_Buzzies.md, Phase 4).
/// </summary>
public interface IMailClient
{
    /// <summary>What this client is, for the tools to say where an answer came from.</summary>
    string Describe { get; }

    Task<IReadOnlyList<MailMessage>> SearchAsync(MailSearch search, CancellationToken ct);

    Task<MailMessage?> GetAsync(string id, CancellationToken ct);

    /// <summary>Attachment bytes, or null when the message or attachment no longer exists.
    /// <paramref name="attachmentId"/> is <see cref="MailAttachment.FetchHandle"/> from a fresh read.</summary>
    Task<byte[]?> GetAttachmentAsync(string messageId, string attachmentId, CancellationToken ct);

    /// <summary>Creates a draft. Never sends: a draft sits in the mailbox for the user to review.</summary>
    Task<string> CreateDraftAsync(MailDraft draft, CancellationToken ct);
}

/// <summary>A reply the model proposes. Saved as a draft; sending is not this API's business.</summary>
/// <param name="ReplyToMessageId">The provider's id for the message being answered (Gmail's own id),
/// which threads the draft into the right conversation on the server.</param>
/// <param name="InReplyToHeader">The RFC 5322 <c>Message-ID</c> header value of the message being
/// answered (<c>&lt;abc@example.com&gt;</c>), written into the draft's <c>In-Reply-To</c> and
/// <c>References</c> headers so mail clients thread it (RFC 5322 §3.6.4). Null when not replying.</param>
public sealed record MailDraft(
    string? ReplyToMessageId,
    IReadOnlyList<string> To,
    string Subject,
    string Body,
    string? InReplyToHeader = null);
