namespace Litos.Mail;

/// <summary>
/// One message as the tools see it (Implement_Buzzies.md, Phase 4). Provider-neutral on purpose: the
/// fixture inbox and the real Gmail client both produce these, so every invariant that matters —
/// "mail text is untrusted content", "nothing deletes mail", "no tool can send" — is decided here
/// and not in provider code that only runs when credentials exist.
/// </summary>
/// <param name="InternetMessageId">The message's own <c>Message-ID</c> header, when the provider
/// exposes it. A draft reply is threaded by putting this in <c>In-Reply-To</c>/<c>References</c>, so
/// the reply lands in the same conversation in the user's mail client rather than as a new one.</param>
public sealed record MailMessage(
    string Id,
    string From,
    IReadOnlyList<string> To,
    string Subject,
    DateTimeOffset Received,
    string Body,
    IReadOnlyList<MailAttachment>? Attachments = null,
    string? InternetMessageId = null)
{
    public string? Snippet => Body.Length <= 200 ? Body : Body[..200].TrimEnd() + "…";
}

/// <summary>An attachment named in a message. Its bytes are fetched separately, behind approval.</summary>
/// <param name="Id">A stable id the model can quote back: the same on every read of the message.</param>
/// <param name="FetchId">The provider's handle for fetching the bytes, when that differs from
/// <paramref name="Id"/>. Gmail's <c>attachmentId</c> is regenerated on every <c>messages.get</c>, so
/// it can't be the id a later tool call matches on (first live Gmail run, 2026-11-27: every download
/// was refused as "not on that message"); Gmail attachments are identified by MIME <c>partId</c>
/// instead, and the fresh <c>attachmentId</c> rides along here. Null means "fetch by Id".</param>
public sealed record MailAttachment(string Id, string FileName, string MediaType, long SizeBytes, string? FetchId = null)
{
    /// <summary>What to pass to <see cref="IMailClient.GetAttachmentAsync"/>.</summary>
    public string FetchHandle => FetchId ?? Id;
}

/// <summary>A search the model asked for, in the provider's own query language plus a cap.</summary>
public sealed record MailSearch(string Query, int MaxResults = 10);
