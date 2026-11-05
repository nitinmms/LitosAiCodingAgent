using System.Text.Json;
using Litos.Agent.Tools;
using Litos.Tools.Skills;
using Microsoft.Extensions.DependencyInjection;

namespace Litos.Artifacts.Tests;

public sealed class ArtifactSkillsTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string BuiltInRoot => _temp.Combine("builtin-skills");

    private string SkillFile => Path.Combine(BuiltInRoot, ArtifactSkills.SkillName, "SKILL.md");

    [Fact]
    public void EmbeddedSkill_HasFrontmatterNamingItArtifactDesign()
    {
        var lines = ArtifactSkills.EmbeddedSkillMarkdown.Replace("\r\n", "\n").Split('\n');

        Assert.Equal("---", lines[0]);
        Assert.Contains("name: artifact-design", lines);
        Assert.Contains(lines, l => l.StartsWith("description: ") && l.Length > "description: ".Length);
    }

    [Fact]
    public void EmbeddedSkill_DocumentsWhatTheSandboxBlocks()
    {
        var skill = ArtifactSkills.EmbeddedSkillMarkdown;

        Assert.Contains("No network", skill);
        Assert.Contains("localStorage", skill);
        Assert.Contains("eval", skill);
        Assert.Contains("prefers-color-scheme: dark", skill);
        foreach (var cdn in ArtifactContentPolicy.Cdns)
            Assert.Contains(cdn, skill);
    }

    [Fact]
    public void EnsureInstalled_WritesTheEmbeddedSkill()
    {
        var path = ArtifactSkills.EnsureInstalled(BuiltInRoot);

        Assert.Equal(SkillFile, path);
        Assert.Equal(ArtifactSkills.EmbeddedSkillMarkdown, File.ReadAllText(SkillFile));
    }

    [Fact]
    public void EnsureInstalled_LeavesAnIdenticalCopyUntouched()
    {
        ArtifactSkills.EnsureInstalled(BuiltInRoot);
        var stamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(SkillFile, stamp);

        ArtifactSkills.EnsureInstalled(BuiltInRoot);

        Assert.Equal(stamp, File.GetLastWriteTimeUtc(SkillFile));
    }

    [Fact]
    public void EnsureInstalled_RefreshesStaleContent_LeavingNoTempFiles()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SkillFile)!);
        File.WriteAllText(SkillFile, "---\nname: artifact-design\ndescription: old\n---\nold body");

        ArtifactSkills.EnsureInstalled(BuiltInRoot);

        Assert.Equal(ArtifactSkills.EmbeddedSkillMarkdown, File.ReadAllText(SkillFile));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(SkillFile)!, "*.tmp"));
    }

    [Fact]
    public void EnsureInstalled_ReturnsNullRatherThanThrowing_WhenItCannotWrite()
    {
        var blocker = _temp.WriteFile("not-a-directory", "x"); // a file where the root directory should be

        Assert.Null(ArtifactSkills.EnsureInstalled(blocker));
    }

    [Fact]
    public void UserRoots_PutTheBuiltInRootLast_AfterTheDefaults()
    {
        var roots = ArtifactSkills.UserRoots(BuiltInRoot);

        Assert.Equal([.. SkillDiscovery.DefaultUserRoots(), BuiltInRoot], roots);
    }

    [Fact]
    public async Task Discovery_FindsTheBuiltInSkill_AndTheSkillToolLoadsItsBody()
    {
        ArtifactSkills.EnsureInstalled(BuiltInRoot);
        var discovery = new SkillDiscovery(_temp.Path, [BuiltInRoot]);

        // Single(name), not Single(): walking up from the temp folder can reach the real
        // ~/.litos/skills as a project-level root, and whatever the user has there also shows up.
        var skill = Assert.Single(await discovery.DiscoverAsync(CancellationToken.None), s => s.Name == ArtifactSkills.SkillName);
        var loaded = await new SkillTool(discovery).InvokeAsync(
            JsonSerializer.SerializeToElement(new { name = ArtifactSkills.SkillName }), CancellationToken.None);

        Assert.Equal(ArtifactSkills.SkillName, skill.Name);
        Assert.False(loaded.IsError, loaded.Text);
        Assert.StartsWith("# Artifact design", loaded.Text);
    }

    [Fact]
    public async Task Discovery_AUserSkillOfTheSameName_OverridesTheBuiltIn()
    {
        ArtifactSkills.EnsureInstalled(BuiltInRoot);
        var userRoot = _temp.Combine("user-skills");
        _temp.WriteFile(Path.Combine("user-skills", "artifact-design", "SKILL.md"), "---\nname: artifact-design\ndescription: mine\n---\nmy rules");
        var discovery = new SkillDiscovery(_temp.Path, [userRoot, BuiltInRoot]);

        var skill = Assert.Single(await discovery.DiscoverAsync(CancellationToken.None), s => s.Name == ArtifactSkills.SkillName);

        Assert.Equal("mine", skill.Description);
    }

    [Fact]
    public async Task AddLitosArtifacts_RegistersTheToolStoreAndASkillDiscoveryThatSeesTheBuiltIn()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISkillDiscovery>(_ => new SkillDiscovery()); // what AddLitosAgent registers first
        services.AddLitosArtifacts(() => "http://127.0.0.1:1", _temp.Combine("artifacts"), BuiltInRoot);
        using var provider = services.BuildServiceProvider();

        Assert.Contains(provider.GetServices<ITool>(), t => t is ArtifactTool);
        Assert.NotNull(provider.GetRequiredService<ArtifactStore>());
        var skills = await provider.GetRequiredService<ISkillDiscovery>().DiscoverAsync(CancellationToken.None);
        Assert.Contains(skills, s => s.Name == ArtifactSkills.SkillName);
        Assert.True(File.Exists(SkillFile));
    }
}
