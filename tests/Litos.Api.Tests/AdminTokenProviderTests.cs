using Litos.Api.Auth;
using Microsoft.Extensions.Configuration;

namespace Litos.Api.Tests;

public class AdminTokenProviderTests
{
    private static AdminTokenProvider CreateProvider(string? configuredToken)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configuredToken is null
                ? []
                : new Dictionary<string, string?> { ["ADMIN_TOKEN"] = configuredToken })
            .Build();
        return new AdminTokenProvider(configuration);
    }

    [Fact]
    public void IsConfigured_NoTokenAnywhere_ReturnsFalse()
    {
        var provider = CreateProvider(configuredToken: null);

        Assert.False(provider.IsConfigured);
    }

    [Fact]
    public void IsValid_NoTokenConfigured_AlwaysReturnsFalse()
    {
        // Absence of a configured token must never be interpreted as "accept anything" —
        // that would turn a misconfiguration into an open admin UI.
        var provider = CreateProvider(configuredToken: null);

        Assert.False(provider.IsValid(""));
        Assert.False(provider.IsValid("anything"));
    }

    [Fact]
    public void IsValid_MatchingToken_ReturnsTrue()
    {
        var provider = CreateProvider("s3cr3t-token");

        Assert.True(provider.IsValid("s3cr3t-token"));
    }

    [Fact]
    public void IsValid_WrongToken_ReturnsFalse()
    {
        var provider = CreateProvider("s3cr3t-token");

        Assert.False(provider.IsValid("wrong-token"));
    }

    [Fact]
    public void IsValid_DifferentLengthToken_ReturnsFalse()
    {
        var provider = CreateProvider("s3cr3t-token");

        Assert.False(provider.IsValid("short"));
    }

    [Fact]
    public void IsValid_EmptyPresentedToken_ReturnsFalse()
    {
        var provider = CreateProvider("s3cr3t-token");

        Assert.False(provider.IsValid(""));
    }
}
