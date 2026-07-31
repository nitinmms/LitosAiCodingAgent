using System.Net;
using System.Text;
using Litos.Agent.Providers;

namespace Litos.Agent.Tests.Providers;

public class OpenRouterModelCatalogTests
{
    private static OpenRouterModelCatalog CreateCatalog(string responseJson)
    {
        var handler = new StubHttpMessageHandler(responseJson);
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://openrouter.ai/api/v1/") };
        return new OpenRouterModelCatalog(client);
    }

    [Fact]
    public async Task ResolveAsync_ReturnsContextLength_ForExactModelId()
    {
        var catalog = CreateCatalog("""{"data":[{"id":"anthropic/claude-sonnet-5","context_length":1000000}]}""");

        var result = await catalog.ResolveAsync("anthropic/claude-sonnet-5", CancellationToken.None);

        Assert.Equal(1_000_000, result);
    }

    [Theory]
    [InlineData("anthropic/")]
    [InlineData("openai/")]
    [InlineData("google/")]
    public async Task ResolveAsync_MatchesNativeModelId_AgainstProviderPrefixedCatalogEntry(string prefix)
    {
        var catalog = CreateCatalog($$"""{"data":[{"id":"{{prefix}}claude-sonnet-5","context_length":1000000}]}""");

        var result = await catalog.ResolveAsync("claude-sonnet-5", CancellationToken.None);

        Assert.Equal(1_000_000, result);
    }

    [Fact]
    public async Task ResolveAsync_ReturnsNull_WhenModelNotInCatalog()
    {
        var catalog = CreateCatalog("""{"data":[{"id":"anthropic/claude-sonnet-5","context_length":1000000}]}""");

        var result = await catalog.ResolveAsync("some-unrelated-model", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveAsync_ReturnsNull_WhenFetchThrows()
    {
        var client = new HttpClient(new ThrowingHttpMessageHandler()) { BaseAddress = new Uri("https://openrouter.ai/api/v1/") };
        var catalog = new OpenRouterModelCatalog(client);

        var result = await catalog.ResolveAsync("anthropic/claude-sonnet-5", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveAsync_OnlyFetchesCatalogOnce_AcrossMultipleCalls()
    {
        var handler = new StubHttpMessageHandler("""{"data":[{"id":"anthropic/claude-sonnet-5","context_length":1000000}]}""");
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://openrouter.ai/api/v1/") };
        var catalog = new OpenRouterModelCatalog(client);

        await catalog.ResolveAsync("claude-sonnet-5", CancellationToken.None);
        await catalog.ResolveAsync("claude-sonnet-5", CancellationToken.None);

        Assert.Equal(1, handler.RequestCount);
    }

    private sealed class StubHttpMessageHandler(string responseJson) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class ThrowingHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("simulated network failure");
    }
}
