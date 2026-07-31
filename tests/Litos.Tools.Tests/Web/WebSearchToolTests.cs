using System.Net;
using System.Text;
using System.Text.Json;
using Litos.Tools.Web;

namespace Litos.Tools.Tests.Web;

public class WebSearchToolTests
{
    private static JsonElement Args(object obj) => JsonSerializer.SerializeToElement(obj);

    private static WebSearchTool CreateTool(string? apiKey, HttpMessageHandler? handler = null)
    {
        var client = new HttpClient(handler ?? new StubHttpMessageHandler("""{"results":[]}"""))
        {
            BaseAddress = new Uri("https://api.tavily.com/"),
        };
        return new WebSearchTool(client, apiKey);
    }

    [Fact]
    public async Task InvokeAsync_MissingQuery_ReturnsError_WithoutCallingHttp()
    {
        var handler = new StubHttpMessageHandler("""{"results":[]}""");
        var tool = CreateTool("some-key", handler);

        var result = await tool.InvokeAsync(Args(new { }), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("A 'query' argument is required.", result.Text);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task InvokeAsync_NoApiKey_ReturnsConfigurationError_NamingEnvVar()
    {
        var tool = CreateTool(apiKey: null);

        var result = await tool.InvokeAsync(Args(new { query = "test" }), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("TAVILY_API_KEY", result.Text);
    }

    [Fact]
    public async Task InvokeAsync_SendsApiKeyAndQuery_ToTavily()
    {
        var handler = new StubHttpMessageHandler("""{"results":[]}""");
        var tool = CreateTool("secret-key", handler);

        await tool.InvokeAsync(Args(new { query = "who won the game" }), CancellationToken.None);

        Assert.Equal(1, handler.RequestCount);
        Assert.Contains("\"api_key\":\"secret-key\"", handler.LastRequestBody);
        Assert.Contains("\"query\":\"who won the game\"", handler.LastRequestBody);
    }

    [Fact]
    public async Task InvokeAsync_NoResults_ReturnsFriendlyMessage()
    {
        var tool = CreateTool("key", new StubHttpMessageHandler("""{"results":[]}"""));

        var result = await tool.InvokeAsync(Args(new { query = "test" }), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("No results found.", result.Text);
    }

    [Fact]
    public async Task InvokeAsync_FormatsResults_WithTitleUrlAndContent()
    {
        var handler = new StubHttpMessageHandler(
            """{"results":[{"title":"Example","url":"https://example.com","content":"Some excerpt."}]}""");
        var tool = CreateTool("key", handler);

        var result = await tool.InvokeAsync(Args(new { query = "test" }), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("Example — https://example.com\nSome excerpt.", result.Text);
    }

    [Fact]
    public async Task InvokeAsync_MultipleResults_SeparatesWithBlankLine()
    {
        var handler = new StubHttpMessageHandler("""
            {"results":[
                {"title":"One","url":"https://a.example","content":"First."},
                {"title":"Two","url":"https://b.example","content":"Second."}
            ]}
            """);
        var tool = CreateTool("key", handler);

        var result = await tool.InvokeAsync(Args(new { query = "test" }), CancellationToken.None);

        Assert.Equal(
            "One — https://a.example\nFirst.\n\nTwo — https://b.example\nSecond.",
            result.Text);
    }

    [Fact]
    public async Task InvokeAsync_NonSuccessStatusCode_ReturnsError()
    {
        var tool = CreateTool("key", new StubHttpMessageHandler("", HttpStatusCode.Unauthorized));

        var result = await tool.InvokeAsync(Args(new { query = "test" }), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("401", result.Text);
    }

    [Fact]
    public async Task InvokeAsync_HttpRequestException_ReturnsError()
    {
        var tool = CreateTool("key", new ThrowingHttpMessageHandler());

        var result = await tool.InvokeAsync(Args(new { query = "test" }), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("simulated network failure", result.Text);
    }

    private sealed class StubHttpMessageHandler(string responseJson, HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            RequestCount++;
            LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class ThrowingHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("simulated network failure");
    }
}
