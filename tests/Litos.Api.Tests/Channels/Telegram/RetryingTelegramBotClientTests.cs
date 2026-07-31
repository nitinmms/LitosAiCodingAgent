using Litos.Api.Channels.Telegram;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot;
using Telegram.Bot.Args;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Requests.Abstractions;
using Telegram.Bot.Types;

namespace Litos.Api.Tests.Channels.Telegram;

public class RetryingTelegramBotClientTests
{
    private static readonly ILogger<RetryingTelegramBotClient> Logger = NullLogger<RetryingTelegramBotClient>.Instance;

    /// <summary>Throws a scripted sequence of results (exceptions rethrown, everything else
    /// returned) for successive SendRequest calls — models an inner ITelegramBotClient that fails
    /// with 429s before eventually succeeding.</summary>
    private sealed class ScriptedInnerClient : ITelegramBotClient
    {
        private readonly Queue<Func<object>> _script;
        public int CallCount { get; private set; }

        public ScriptedInnerClient(params Func<object>[] script) => _script = new Queue<Func<object>>(script);

        public Task<TResponse> SendRequest<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            CallCount++;
            var produce = _script.Dequeue();
            return Task.FromResult((TResponse)produce());
        }

        public Task<bool> TestApi(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task DownloadFile(string filePath, Stream destination, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DownloadFile(TGFile file, Stream destination, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public bool LocalBotServer => false;
        public long BotId => 1;
        public TimeSpan Timeout { get; set; }
        public IExceptionParser ExceptionsParser { get; set; } = null!;
        public event AsyncEventHandler<ApiRequestEventArgs>? OnMakingApiRequest;
        public event AsyncEventHandler<ApiResponseEventArgs>? OnApiResponseReceived;
    }

    private static ApiRequestException FloodControl(int retryAfterSeconds) =>
        new("Too Many Requests: retry later", 429, new ResponseParameters { RetryAfter = retryAfterSeconds });

    private static SendMessageRequest AnyRequest() => new() { ChatId = 1, Text = "hi" };

    [Fact]
    public async Task SendRequest_SucceedsFirstTry_ReturnsResultWithoutDelay()
    {
        var inner = new ScriptedInnerClient(() => new Message { Id = 1, Text = "hi" });
        var client = new RetryingTelegramBotClient(inner, Logger);

        var result = await client.SendRequest(AnyRequest(), CancellationToken.None);

        Assert.Equal(1, inner.CallCount);
        Assert.Equal("hi", result.Text);
    }

    [Fact]
    public async Task SendRequest_FloodControlThenSuccess_RetriesAfterRetryAfterAndReturnsResult()
    {
        var inner = new ScriptedInnerClient(
            () => throw FloodControl(retryAfterSeconds: 0),
            () => new Message { Id = 2, Text = "ok" });
        var client = new RetryingTelegramBotClient(inner, Logger);

        var result = await client.SendRequest(AnyRequest(), CancellationToken.None);

        Assert.Equal(2, inner.CallCount);
        Assert.Equal("ok", result.Text);
    }

    [Fact]
    public async Task SendRequest_RepeatedFloodControl_GivesUpAfterMaxAttempts_AndThrows()
    {
        var inner = new ScriptedInnerClient(
            () => throw FloodControl(0),
            () => throw FloodControl(0),
            () => throw FloodControl(0),
            () => throw FloodControl(0),
            () => throw FloodControl(0));
        var client = new RetryingTelegramBotClient(inner, Logger);

        await Assert.ThrowsAsync<ApiRequestException>(() => client.SendRequest(AnyRequest(), CancellationToken.None));
        Assert.Equal(5, inner.CallCount);
    }

    [Fact]
    public async Task SendRequest_NonFloodControlApiError_PropagatesImmediately_WithoutRetrying()
    {
        var inner = new ScriptedInnerClient(() => throw new ApiRequestException("Bad Request", 400));
        var client = new RetryingTelegramBotClient(inner, Logger);

        await Assert.ThrowsAsync<ApiRequestException>(() => client.SendRequest(AnyRequest(), CancellationToken.None));
        Assert.Equal(1, inner.CallCount);
    }
}
