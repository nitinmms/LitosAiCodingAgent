using Telegram.Bot;
using Telegram.Bot.Args;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Requests.Abstractions;
using Telegram.Bot.Types;

namespace Litos.Api.Tests.Fakes;

/// <summary>
/// Minimal ITelegramBotClient fake — real bot clients dispatch every API call through
/// SendRequest&lt;TResponse&gt;(IRequest&lt;TResponse&gt;), so a fake needs only pattern-match on
/// the concrete request type to return canned responses, rather than mocking dozens of extension
/// methods (SendMessage/SendChatAction/etc. are all sugar over SendRequest in Telegram.Bot v22).
/// Records every sent-message text so tests can assert what the driver actually sent.
/// </summary>
public sealed class FakeTelegramBotClient : ITelegramBotClient
{
    private int _nextMessageId = 1;

    public List<string> SentMessages { get; } = [];

    /// <summary>Text of every EditMessageText call, in order — separate from SentMessages since a
    /// caller (e.g. TelegramGatingApprovalGate's timeout handler) editing a message it already
    /// sent shouldn't be double-counted as a second "sent" message.</summary>
    public List<string> EditedMessages { get; } = [];

    /// <summary>Every SendDocument call, in order — separate from SentMessages since a document
    /// upload isn't text and tests need the filename/caption, not just a string.</summary>
    public List<SentDocument> SentDocuments { get; } = [];

    public Task<TResponse> SendRequest<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        object? result = request switch
        {
            SendMessageRequest send => RecordAndBuildMessage(send.Text),
            SendChatActionRequest => true,
            GetFileRequest => new TGFile { FileId = "fake", FilePath = "fake/path" },
            GetMeRequest => new User { Id = 1, IsBot = true, FirstName = "TestBot", Username = "test_bot" },
            AnswerCallbackQueryRequest => true,
            EditMessageTextRequest edit => RecordAndBuildEditedMessage(edit.Text),
            SendDocumentRequest doc => RecordAndBuildDocument(doc),
            _ => throw new NotSupportedException($"FakeTelegramBotClient does not support {request.GetType().Name}."),
        };

        return Task.FromResult((TResponse)result!);
    }

    private Message RecordAndBuildMessage(string? text)
    {
        SentMessages.Add(text ?? "");
        return new Message { Id = _nextMessageId++, Text = text };
    }

    private Message RecordAndBuildEditedMessage(string? text)
    {
        EditedMessages.Add(text ?? "");
        return new Message { Text = text };
    }

    private Message RecordAndBuildDocument(SendDocumentRequest doc)
    {
        var fileName = (doc.Document as InputFileStream)?.FileName ?? "";
        SentDocuments.Add(new SentDocument(doc.ChatId, fileName, doc.Caption));
        return new Message { Id = _nextMessageId++, Document = new Document { FileName = fileName } };
    }

    public Task<bool> TestApi(CancellationToken cancellationToken = default) => Task.FromResult(true);

    public Task DownloadFile(string filePath, Stream destination, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task DownloadFile(TGFile file, Stream destination, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public bool LocalBotServer => false;
    public long BotId => 1;
    public TimeSpan Timeout { get; set; }
    public IExceptionParser ExceptionsParser { get; set; } = null!;

    public event AsyncEventHandler<ApiRequestEventArgs>? OnMakingApiRequest;
    public event AsyncEventHandler<ApiResponseEventArgs>? OnApiResponseReceived;
}

public sealed record SentDocument(ChatId ChatId, string FileName, string? Caption);
