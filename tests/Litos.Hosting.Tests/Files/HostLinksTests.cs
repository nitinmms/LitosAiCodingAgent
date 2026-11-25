using Litos.Hosting.Files;

namespace Litos.Hosting.Tests.Files;

/// <summary>Same cases as Litos.VsCode's __tests__/shareLinks.test.ts, which pins the TypeScript
/// original — the two must agree on which links count as host-minted.</summary>
public class HostLinksTests
{
    private const string Current = "http://127.0.0.1:60000";

    [Fact]
    public void RewriteStale_SharedFileLinkFromADeadPort_IsRepointedAtTheRunningHost() =>
        Assert.Equal($"{Current}/files/0123456789abcdef",
            HostLinks.RewriteStale("http://127.0.0.1:51234/files/0123456789abcdef", Current));

    [Fact]
    public void RewriteStale_ArtifactLink_IsRepointedAtTheRunningHost() =>
        Assert.Equal($"{Current}/artifacts/0123456789abcdef",
            HostLinks.RewriteStale("http://127.0.0.1:51234/artifacts/0123456789abcdef", Current));

    [Fact]
    public void RewriteStale_VersionedArtifactLink_KeepsTheVersion() =>
        Assert.Equal($"{Current}/artifacts/0123456789abcdef/v/3",
            HostLinks.RewriteStale("http://127.0.0.1:51234/artifacts/0123456789abcdef/v/3", Current));

    [Fact]
    public void RewriteStale_AcceptsLocalhostAndATrailingSlashOnTheBaseUrl() =>
        Assert.Equal($"{Current}/artifacts/0123456789abcdef",
            HostLinks.RewriteStale("http://localhost:51234/artifacts/0123456789abcdef", $"{Current}/"));

    [Theory]
    [InlineData("https://example.com/artifacts/0123456789abcdef")] // non-loopback host, same path shape
    [InlineData("http://127.0.0.1:51234/artifacts/0123abcd")] // artifact id of the wrong length
    [InlineData("http://127.0.0.1:51234/artifacts/0123456789ABCDEF")] // upper-case id (never minted)
    [InlineData("http://127.0.0.1:51234/config/status")] // some other path on the host
    [InlineData("http://127.0.0.1:51234/artifacts/0123456789abcdef/meta.json")] // anything after an id
    [InlineData("not a url")]
    public void RewriteStale_NonHostLinks_AreLeftUnchanged(string url)
    {
        Assert.Equal(url, HostLinks.RewriteStale(url, Current));
        Assert.False(HostLinks.IsHostLink(url));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void RewriteStale_NoRunningHost_LeavesEveryLinkUnchanged(string? currentBaseUrl)
    {
        const string url = "http://127.0.0.1:51234/artifacts/0123456789abcdef";
        Assert.Equal(url, HostLinks.RewriteStale(url, currentBaseUrl));
    }

    [Theory]
    [InlineData("http://127.0.0.1:51234/files/0123456789abcdef")]
    [InlineData("http://127.0.0.1:51234/artifacts/0123456789abcdef/v/2")]
    public void IsHostLink_HostMintedLinks_AreRecognized(string url) => Assert.True(HostLinks.IsHostLink(url));
}

public class HostLinksFindAllTests
{
    [Fact]
    public void FindAll_PicksTheArtifactLinkOutOfTheToolResult()
    {
        const string result = "Published artifact \"Report\" (id 0123456789abcdef, version 2). Open: http://127.0.0.1:51234/artifacts/0123456789abcdef";

        Assert.Equal(["http://127.0.0.1:51234/artifacts/0123456789abcdef"], HostLinks.FindAll(result));
    }

    [Fact]
    public void FindAll_TrimsSentencePunctuation_AndDropsDuplicates()
    {
        const string text = "See http://127.0.0.1:1/files/0123456789abcdef. Again: http://127.0.0.1:1/files/0123456789abcdef";

        Assert.Equal(["http://127.0.0.1:1/files/0123456789abcdef"], HostLinks.FindAll(text));
    }

    [Fact]
    public void FindAll_IgnoresOtherLinks() =>
        Assert.Empty(HostLinks.FindAll("Docs at https://example.com/artifacts/0123456789abcdef and http://127.0.0.1:1/config/status"));
}
