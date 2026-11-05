namespace Litos.Artifacts.Tests;

/// <summary>
/// Pins the security-relevant parts of the artifact CSP (ReadMe_ArtifactsFeature.md §7). A change
/// that makes any of these fail weakens the boundary between agent-authored pages and the host's
/// unauthenticated API — it should be a deliberate, reviewed decision, not a drive-by edit.
/// </summary>
public sealed class ArtifactContentPolicyTests
{
    private static readonly string Policy = ArtifactContentPolicy.ContentSecurityPolicy;

    private static string Directive(string name) =>
        Policy.Split("; ").Single(d => d == name || d.StartsWith(name + " "));

    [Fact]
    public void Sandbox_AllowsScriptsButNeverSameOriginFormsOrTopNavigation()
    {
        var sandbox = Directive("sandbox");

        Assert.Contains("allow-scripts", sandbox);
        Assert.DoesNotContain("allow-same-origin", sandbox);
        Assert.DoesNotContain("allow-forms", sandbox);
        Assert.DoesNotContain("allow-top-navigation", sandbox);
        Assert.DoesNotContain("allow-popups-to-escape-sandbox", sandbox);
    }

    [Theory]
    [InlineData("default-src 'none'")]
    [InlineData("connect-src 'none'")]
    [InlineData("form-action 'none'")]
    [InlineData("frame-src 'none'")]
    [InlineData("object-src 'none'")]
    [InlineData("base-uri 'none'")]
    [InlineData("frame-ancestors 'none'")]
    public void LocksDown(string directive) => Assert.Equal(directive, Directive(directive.Split(' ')[0]));

    [Fact]
    public void Scripts_InlineAndEveryAllowlistedCdn_ButNoEval()
    {
        var scripts = Directive("script-src");

        Assert.Contains("'unsafe-inline'", scripts);
        foreach (var cdn in ArtifactContentPolicy.Cdns)
            Assert.Contains(cdn, scripts);
        Assert.DoesNotContain("unsafe-eval", Policy);
        Assert.DoesNotContain("*", Policy);
        Assert.DoesNotContain("http:", Policy);
    }

    [Fact]
    public void Cdns_AreAllHttpsOrigins()
    {
        Assert.NotEmpty(ArtifactContentPolicy.Cdns);
        Assert.All(ArtifactContentPolicy.Cdns, cdn =>
        {
            var uri = new Uri(cdn);
            Assert.Equal("https", uri.Scheme);
            Assert.Equal("/", uri.AbsolutePath);
        });
    }

    [Fact]
    public void Styles_AndFonts_AllowGoogleFonts()
    {
        Assert.Contains(ArtifactContentPolicy.GoogleFontsStylesheets, Directive("style-src"));
        Assert.Contains(ArtifactContentPolicy.GoogleFontsFiles, Directive("font-src"));
    }
}
