using System.Text.Json;

namespace Litos.Host.Tests;

public class LitosConfigTests
{
    [Fact]
    public void ChatProviderNames_ExcludesTavily()
    {
        // Tavily is a tool-only key (WebSearchTool), not a chat provider — callers that pick
        // an active chat provider from ApiKeys.Keys must filter through this list, or a
        // Tavily-only key (no LLM key configured) gets mistaken for an available provider.
        Assert.DoesNotContain("tavily", LitosConfig.ChatProviderNames);
        Assert.Equal(["anthropic", "openai", "gemini", "openrouter", "local"], LitosConfig.ChatProviderNames);
    }

    [Fact]
    public void IsProviderConfigured_Local_TrueOnlyWithBaseUrl_NoKeyNeeded()
    {
        // "local" is deliberately the one provider that doesn't need an ApiKeys entry — a local
        // OpenAI-compatible server (LM Studio, Ollama, ...) typically needs no key at all, so
        // gating it on ApiKeys the way every other provider is gated would make it unreachable.
        var withoutBaseUrl = new LitosConfig("anthropic", null, null, new Dictionary<string, string>());
        var withBaseUrl = withoutBaseUrl with { LocalBaseUrl = "http://localhost:1234/v1" };

        Assert.False(withoutBaseUrl.IsProviderConfigured("local"));
        Assert.True(withBaseUrl.IsProviderConfigured("local"));
    }

    [Fact]
    public void IsProviderConfigured_VendorProvider_RequiresApiKey()
    {
        var withKey = new LitosConfig("anthropic", null, null, new Dictionary<string, string> { ["anthropic"] = "sk-test" });
        var withoutKey = new LitosConfig("anthropic", null, null, new Dictionary<string, string>());

        Assert.True(withKey.IsProviderConfigured("anthropic"));
        Assert.False(withoutKey.IsProviderConfigured("anthropic"));
    }

    [Fact]
    public void AvailableChatProviders_IncludesLocal_WhenBaseUrlSetButNoKey()
    {
        var config = new LitosConfig("anthropic", null, null, new Dictionary<string, string>())
            with
        { LocalBaseUrl = "http://localhost:1234/v1" };

        Assert.Equal(["local"], config.AvailableChatProviders);
    }

    [Fact]
    public void ShellCommandTimeout_Unset_IsNull()
    {
        // Null means "let ShellTool use its own 5-minute default" — Load() must not invent a
        // value here, or a user who never touched config.json would silently get a different
        // default than ShellTool's own documented one.
        var config = new LitosConfig("anthropic", null, null, new Dictionary<string, string>());

        Assert.Null(config.ShellCommandTimeout);
    }

    [Fact]
    public void ShellCommandTimeout_Set_ConvertsSecondsToTimeSpan()
    {
        var config = new LitosConfig("anthropic", null, null, new Dictionary<string, string>(), ShellCommandTimeoutSeconds: 600);

        Assert.Equal(TimeSpan.FromSeconds(600), config.ShellCommandTimeout);
    }

    [Fact]
    public void ShellCommandTimeoutSeconds_RoundTripsThroughJson_AsPlainNumber()
    {
        // Stored as a raw int (not a serialized TimeSpan) specifically so a user can hand-edit
        // config.json with an ordinary number rather than a TimeSpan's "hh:mm:ss" string format.
        var config = new LitosConfig("anthropic", null, null, new Dictionary<string, string>(), ShellCommandTimeoutSeconds: 600);

        var json = JsonSerializer.Serialize(config);
        using var parsed = JsonDocument.Parse(json);
        Assert.Equal(600, parsed.RootElement.GetProperty("ShellCommandTimeoutSeconds").GetInt32());

        var roundTripped = JsonSerializer.Deserialize<LitosConfig>(json);
        Assert.Equal(600, roundTripped!.ShellCommandTimeoutSeconds);
    }
}
