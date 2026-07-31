using Telegram.Bot;
using Telegram.Bot.Args;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests.Abstractions;
using Telegram.Bot.Types;
using Microsoft.Extensions.Logging;

namespace Litos.Api.Channels.Telegram;

/// <summary>
/// Decorates <see cref="ITelegramBotClient"/> with 429 (flood control) retry-with-backoff — every
/// extension method used throughout this bridge (SendMessage, EditMessageText, SendDocument, ...)
/// is sugar over <see cref="ITelegramBotClient.SendRequest{TResponse}"/> in Telegram.Bot v22
/// (confirmed by inspection: it's the interface's only request-dispatching member), so wrapping
/// that single method covers every Telegram API call this process makes without touching any call
/// site in TelegramSessionDriver/TelegramGatingApprovalGate/SendFileTool/TelegramBridge.
///
/// Motivated by a live incident: a single turn that made two gated tool calls in sequence (each
/// round-tripping an approval prompt + edited confirmation through Telegram) burst ~5-6 API calls
/// to one chat within ~15 seconds — enough to trip Telegram's own flood control and surface a 429
/// on the user's very next, unrelated message. Telegram tells the caller exactly how long to wait
/// via ResponseParameters.RetryAfter on the 429 response; honoring it is the documented way to
/// recover (core.telegram.org/bots/faq#my-bot-is-hitting-limits-how-do-i-avoid-this).
/// </summary>
public sealed class RetryingTelegramBotClient(ITelegramBotClient inner, ILogger<RetryingTelegramBotClient> logger) : ITelegramBotClient
{
    private const int MaxAttempts = 5;
    private static readonly TimeSpan FallbackDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);

    public async Task<TResponse> SendRequest<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await inner.SendRequest(request, cancellationToken);
            }
            catch (ApiRequestException ex) when (ex.ErrorCode == 429 && attempt < MaxAttempts)
            {
                var delay = ex.Parameters?.RetryAfter is { } retryAfter
                    ? TimeSpan.FromSeconds(retryAfter)
                    : FallbackDelay;
                if (delay > MaxDelay)
                    delay = MaxDelay;

                logger.LogWarning(
                    "Telegram flood control (429) on {RequestType}, attempt {Attempt}/{MaxAttempts} — waiting {DelaySeconds}s before retrying.",
                    request.GetType().Name, attempt, MaxAttempts, delay.TotalSeconds);

                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    public Task<bool> TestApi(CancellationToken cancellationToken = default) =>
        inner.TestApi(cancellationToken);

    public Task DownloadFile(string filePath, Stream destination, CancellationToken cancellationToken = default) =>
        inner.DownloadFile(filePath, destination, cancellationToken);

    public Task DownloadFile(TGFile file, Stream destination, CancellationToken cancellationToken = default) =>
        inner.DownloadFile(file, destination, cancellationToken);

    public bool LocalBotServer => inner.LocalBotServer;
    public long BotId => inner.BotId;
    public TimeSpan Timeout
    {
        get => inner.Timeout;
        set => inner.Timeout = value;
    }
    public IExceptionParser ExceptionsParser
    {
        get => inner.ExceptionsParser;
        set => inner.ExceptionsParser = value;
    }

    public event AsyncEventHandler<ApiRequestEventArgs>? OnMakingApiRequest
    {
        add => inner.OnMakingApiRequest += value;
        remove => inner.OnMakingApiRequest -= value;
    }

    public event AsyncEventHandler<ApiResponseEventArgs>? OnApiResponseReceived
    {
        add => inner.OnApiResponseReceived += value;
        remove => inner.OnApiResponseReceived -= value;
    }
}
