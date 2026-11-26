using Litos.Agent.Providers;

namespace Litos.Agent.Tests.Providers;

public class ModelContextWindowsTests
{
    [Theory]
    // Anthropic rows: max_input_tokens from Anthropic's /v1/models on 2026-11-26.
    [InlineData("claude-sonnet-5-20260115", 1_000_000)]
    [InlineData("claude-sonnet-5-5", 1_000_000)]
    [InlineData("claude-sonnet-4-6", 1_000_000)]
    [InlineData("claude-sonnet-4-5-20250929", 200_000)]
    [InlineData("claude-sonnet-4-20250514", 200_000)]
    [InlineData("claude-opus-5-5", 1_000_000)]
    [InlineData("claude-opus-5", 1_000_000)]
    [InlineData("claude-opus-4-8", 1_000_000)]
    [InlineData("claude-opus-4-7", 1_000_000)]
    [InlineData("claude-opus-4-6", 1_000_000)]
    [InlineData("claude-opus-4-5-20251101", 200_000)]
    [InlineData("claude-opus-4-1-20250805", 200_000)]
    [InlineData("claude-haiku-5-5", 1_000_000)]
    [InlineData("claude-haiku-4-5-20251001", 200_000)]
    [InlineData("claude-3-5-haiku-20241022", 200_000)]
    [InlineData("claude-fable-5-2", 1_000_000)]
    [InlineData("claude-fable-5", 1_000_000)]
    [InlineData("gpt-4o", 128_000)]
    [InlineData("gpt-4.1-mini", 1_000_000)]
    [InlineData("gpt-5.4", 1_000_000)]
    [InlineData("o4-mini", 200_000)]
    [InlineData("o3", 200_000)]
    [InlineData("gemini-3-flash", 200_000)]
    [InlineData("gemini-2.5-flash", 1_000_000)]
    [InlineData("gemini-1.5-pro", 1_000_000)]
    public void Resolve_MatchesKnownModelFamilies(string modelId, int expectedContextLength)
    {
        Assert.Equal(expectedContextLength, ModelContextWindows.Resolve(modelId));
    }

    [Fact]
    public void Resolve_ReturnsFallback_ForUnrecognizedModelId()
    {
        Assert.Equal(ModelContextWindows.FallbackContextLength, ModelContextWindows.Resolve("some-future-model-nobody-has-heard-of"));
    }

    [Fact]
    public void Resolve_IsCaseInsensitive()
    {
        Assert.Equal(ModelContextWindows.Resolve("gpt-4o"), ModelContextWindows.Resolve("GPT-4O"));
    }
}
