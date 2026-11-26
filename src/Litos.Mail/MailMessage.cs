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
public sealed record MailAttachment(string Id, string FileName, string MediaType, long SizeBytes);

/// <summary>A search the model asked for, in the provider's own query language plus a cap.</summary>
public sealed record MailSearch(string Query, int MaxResults = 10);
