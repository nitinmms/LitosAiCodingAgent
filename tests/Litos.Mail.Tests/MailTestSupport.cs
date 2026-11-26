using System.Text;
using System.Text.Json;
using Litos.Agent.Tools;
using Litos.Mail;
using Litos.Tools.Attachments;

namespace Litos.Mail.Tests;

/// <summary>A mailbox that answers from a list, records what it was asked, and can be told to fail.</summary>
internal sealed class FakeMailClient : IMailClient
{
    public string Describe => "the test inbox";

    public List<MailMessage> Inbox { get; } = [];
    public List<MailDraft> Drafts { get; } = [];
    public List<(string MessageId, string AttachmentId)> AttachmentRequests { get; } = [];
    public byte[]? Attachment { get; set; } = Encoding.UTF8.GetBytes("attachment text");
    public MailAccessException? Failure { get; set; }

    public Task<IReadOnlyList<MailMessage>> SearchAsync(MailSearch search, CancellationToken ct) =>
        Failure is not null
            ? Task.FromException<IReadOnlyList<MailMessage>>(Failure)
            : Task.FromResult<IReadOnlyList<MailMessage>>([.. Inbox.Take(search.MaxResults)]);

    public Task<MailMessage?> GetAsync(string id, CancellationToken ct) =>
        Failure is not null
            ? Task.FromException<MailMessage?>(Failure)
            : Task.FromResult(Inbox.FirstOrDefault(m => m.Id == id));

    public Task<byte[]?> GetAttachmentAsync(string messageId, string attachmentId, CancellationToken ct)
    {
        AttachmentRequests.Add((messageId, attachmentId));
        return Failure is not null ? Task.FromException<byte[]?>(Failure) : Task.FromResult(Attachment);
    }

    public Task<string> CreateDraftAsync(MailDraft draft, CancellationToken ct)
    {
        Drafts.Add(draft);
        return Failure is not null ? Task.FromException<string>(Failure) : Task.FromResult("draft-1");
    }
}

/// <summary>Approves or denies everything, and records how many times it was asked.</summary>
internal sealed class FakeApproval(bool approve = true) : IMailApproval
{
    public int Asked { get; private set; }
    public List<string> Summaries { get; } = [];

    public Task<bool> ApproveAsync(string summary, string? detail, CancellationToken ct)
    {
        Asked++;
        Summaries.Add(summary);
        return Task.FromResult(approve);
    }
}

internal static class Json
{
    public static JsonElement Of(object value) => JsonSerializer.SerializeToElement(value);
}

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "litos-mail-tests", Guid.NewGuid().ToString("n"));

    public TempDir() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Returns whatever text is handed to it, so a test can prove the converter was used.</summary>
internal sealed class EchoConverter(string markdown = "converted") : IAttachmentConverter
{
    public int Called { get; private set; }

    public Task<DocumentMarkdown> ConvertAsync(AttachmentSource source, CancellationToken ct)
    {
        Called++;
        return Task.FromResult(new DocumentMarkdown("doc", markdown, []));
    }
}
