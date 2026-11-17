using Litos.Probe.Web.Detection;

namespace Litos.Probe.Web.Tests;

public class StackDetectorTests
{
    private static DetectionResult Detect(TempFolder repo) => new StackDetector().Detect(repo.Path, Samples.Profiles.All);

    private const string ExpressPackage = """{ "dependencies": { "express": "^5.2.1", "react": "^19.0.0" } }""";
    private const string WebCsproj = """<Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""";

    [Fact]
    public void An_express_app_is_node_express_spa()
    {
        using var repo = new TempFolder();
        repo.Write("package.json", ExpressPackage);

        var result = Detect(repo);

        Assert.Equal(DetectionStatus.Detected, result.Status);
        var c = Assert.Single(result.Candidates);
        Assert.Equal(("node-express-spa", "", 1), (c.ProfileId, c.Directory, c.Tier));
        Assert.Equal(["package.json"], c.Evidence);
    }

    [Fact]
    public void A_next_app_is_not_mistaken_for_express()
    {
        using var repo = new TempFolder();
        repo.Write("package.json", """{ "dependencies": { "express": "^5", "next": "^16" } }""");
        Assert.Equal(DetectionStatus.NotAWebApp, Detect(repo).Status);
    }

    [Fact]
    public void A_blazor_server_app_needs_both_markers()
    {
        using var repo = new TempFolder();
        repo.Write("src/Web/Web.csproj", WebCsproj);
        Assert.Equal(DetectionStatus.NotAWebApp, Detect(repo).Status);

        repo.Write("src/Web/Components/Pages/Home.razor", "@page \"/\"\n@rendermode InteractiveServer\n");
        var result = Detect(repo);

        Assert.Equal(DetectionStatus.Detected, result.Status);
        Assert.Equal(("aspnet-blazor-server", "src/Web"), (result.Best!.ProfileId, result.Best.Directory));
        Assert.Contains("src/Web/Components/Pages/Home.razor", result.Best.Evidence);
    }

    [Fact]
    public void A_monorepo_lists_each_app_root_first()
    {
        using var repo = new TempFolder();
        repo.Write("web/package.json", ExpressPackage);
        repo.Write("api/Api.csproj", WebCsproj);
        repo.Write("api/Program.cs", "builder.Services.AddRazorComponents().AddInteractiveServerComponents();");

        var result = Detect(repo);

        Assert.Equal(DetectionStatus.Several, result.Status);
        Assert.Equal(["api", "web"], result.Candidates.Select(c => c.Directory).Order());
    }

    [Fact]
    public void Dependencies_and_build_output_are_ignored()
    {
        using var repo = new TempFolder();
        repo.Write("node_modules/some-lib/package.json", ExpressPackage);
        repo.Write("bin/Release/package.json", ExpressPackage);
        Assert.Equal(DetectionStatus.NotAWebApp, Detect(repo).Status);
    }

    [Theory]
    [InlineData("Gemfile", "gem 'rails', '~> 8.0'")]
    [InlineData("composer.json", """{ "require": { "laravel/framework": "^12.0" } }""")]
    [InlineData("mix.exs", "{:phoenix, \"~> 1.8\"}")]
    public void A_web_framework_without_a_toolchain_is_not_supported_yet(string file, string content)
    {
        using var repo = new TempFolder();
        repo.Write(file, content);

        var result = Detect(repo);

        Assert.Equal(DetectionStatus.StackNotSupported, result.Status);
        Assert.Null(result.Best);
    }

    [Fact]
    public void An_empty_repository_is_not_a_web_app()
    {
        using var repo = new TempFolder();
        repo.Write("README.md", "# hello");
        Assert.Equal(DetectionStatus.NotAWebApp, Detect(repo).Status);
    }

    [Fact]
    public void The_test_apps_are_detected_as_their_own_stacks()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Litos.Probe.Web.TestApps"));
        Assert.Equal("node-express-spa", new StackDetector().Detect(Path.Combine(root, "NodeExpressSpa"), Samples.Profiles.All).Best?.ProfileId);
        Assert.Equal("aspnet-blazor-server", new StackDetector().Detect(Path.Combine(root, "AspNetBlazor"), Samples.Profiles.All).Best?.ProfileId);
    }
}
