using Litos.Agent.Providers;

namespace Litos.Agent.Tests.Providers;

public class ModelContextWindowsTests
{
    [Theory]
    [InlineData("claude-sonnet-5-20260115", 1_000_000)]
    [InlineData("claude-sonnet-4-5-20250929", 200_000)]
    [InlineData("claude-opus-4-8", 200_000)]
    [InlineData("claude-haiku-4-5", 200_000)]
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
