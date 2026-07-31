using System.Text.Json;
using Litos.Tools.Skills;
using Litos.Tools.Tests.Fakes;

namespace Litos.Tools.Tests.Skills;

public class SkillToolTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("litos-skilltool-").FullName;

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private static JsonElement Args(object obj) => JsonSerializer.SerializeToElement(obj);

    [Fact]
    public async Task InvokeAsync_WhitespaceName_ReturnsError_WithoutCallingDiscovery()
    {
        var discovery = new FakeSkillDiscovery([]);
        var tool = new SkillTool(discovery);

        var result = await tool.InvokeAsync(Args(new { name = "  " }), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("A 'name' argument is required.", result.Text);
        Assert.Equal(0, discovery.CallCount);
    }

    [Fact]
    public async Task InvokeAsync_UnknownSkillName_ReturnsError()
    {
        var discovery = new FakeSkillDiscovery([new SkillMetadata("known", "desc", _tempDir)]);
        var tool = new SkillTool(discovery);

        var result = await tool.InvokeAsync(Args(new { name = "unknown" }), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("Unknown skill 'unknown'.", result.Text);
    }

    [Fact]
    public async Task InvokeAsync_KnownSkill_ReturnsBodyOnly_FrontmatterStripped()
    {
        File.WriteAllText(Path.Combine(_tempDir, "SKILL.md"), "---\nname: my-skill\ndescription: desc\n---\nFull instructions.");
        var discovery = new FakeSkillDiscovery([new SkillMetadata("my-skill", "desc", _tempDir)]);
        var tool = new SkillTool(discovery);

        var result = await tool.InvokeAsync(Args(new { name = "my-skill" }), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("Full instructions.", result.Text);
    }

    [Fact]
    public async Task InvokeAsync_DuplicateSkillNamesInDiscoveryResult_Throws()
    {
        // SingleOrDefault throws if more than one match — real SkillDiscovery can't produce
        // duplicates (Dictionary-backed), but a hand-crafted fake can, and this documents
        // the reachable failure mode if that invariant is ever violated.
        var discovery = new FakeSkillDiscovery(
        [
            new SkillMetadata("dup", "first", _tempDir),
            new SkillMetadata("dup", "second", _tempDir),
        ]);
        var tool = new SkillTool(discovery);

        await Assert.ThrowsAsync<InvalidOperationException>(() => tool.InvokeAsync(Args(new { name = "dup" }), CancellationToken.None));
    }
}
