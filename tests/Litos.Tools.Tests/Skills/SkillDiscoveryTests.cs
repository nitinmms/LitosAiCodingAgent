using Litos.Tools.Skills;

namespace Litos.Tools.Tests.Skills;

public class SkillDiscoveryTests : IDisposable
{
    private readonly string _tempRoot = Directory.CreateTempSubdirectory("litos-skilldiscovery-").FullName;

    public void Dispose() => Directory.Delete(_tempRoot, recursive: true);

    private static void WriteSkill(string skillsRoot, string skillName, string name, string description)
    {
        var skillDir = Path.Combine(skillsRoot, skillName);
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), $"---\nname: {name}\ndescription: {description}\n---\nBody.");
    }

    [Fact]
    public async Task DiscoverAsync_FindsProjectLevelSkill_InStartDirectory()
    {
        var skillsRoot = Path.Combine(_tempRoot, ".litos", "skills");
        WriteSkill(skillsRoot, "my-skill", "my-skill", "Does a thing.");
        var discovery = new SkillDiscovery(startDirectory: _tempRoot);

        var skills = await discovery.DiscoverAsync(CancellationToken.None);

        Assert.Contains(skills, s => s.Name == "my-skill" && s.Description == "Does a thing.");
    }

    [Fact]
    public async Task DiscoverAsync_WalksUpFromNestedStartDirectory_ToFindAncestorSkillsRoot()
    {
        var skillsRoot = Path.Combine(_tempRoot, ".litos", "skills");
        WriteSkill(skillsRoot, "ancestor-skill", "ancestor-skill", "From the top.");
        var nested = Path.Combine(_tempRoot, "child", "grandchild");
        Directory.CreateDirectory(nested);
        var discovery = new SkillDiscovery(startDirectory: nested);

        var skills = await discovery.DiscoverAsync(CancellationToken.None);

        Assert.Contains(skills, s => s.Name == "ancestor-skill");
    }

    [Fact]
    public async Task DiscoverAsync_CloserDirectoryWinsOnNameCollision()
    {
        var farSkillsRoot = Path.Combine(_tempRoot, ".litos", "skills");
        WriteSkill(farSkillsRoot, "shared", "shared", "far description");

        var childDir = Path.Combine(_tempRoot, "child");
        Directory.CreateDirectory(childDir);
        var closeSkillsRoot = Path.Combine(childDir, ".litos", "skills");
        WriteSkill(closeSkillsRoot, "shared", "shared", "close description");

        var discovery = new SkillDiscovery(startDirectory: childDir);

        var skills = await discovery.DiscoverAsync(CancellationToken.None);

        var shared = Assert.Single(skills, s => s.Name == "shared");
        Assert.Equal("close description", shared.Description);
    }

    [Fact]
    public async Task DiscoverAsync_SkipsSkillDirectoryWithNoSkillMdFile()
    {
        var skillsRoot = Path.Combine(_tempRoot, ".litos", "skills");
        Directory.CreateDirectory(Path.Combine(skillsRoot, "incomplete-skill"));
        var discovery = new SkillDiscovery(startDirectory: _tempRoot);

        var skills = await discovery.DiscoverAsync(CancellationToken.None);

        Assert.DoesNotContain(skills, s => s.DirectoryPath.Contains("incomplete-skill"));
    }

    [Fact]
    public async Task DiscoverAsync_SkipsSkillMissingNameOrDescriptionField()
    {
        var skillsRoot = Path.Combine(_tempRoot, ".litos", "skills");
        var skillDir = Path.Combine(skillsRoot, "half-baked");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), "---\nname: half-baked\n---\nNo description field.");
        var discovery = new SkillDiscovery(startDirectory: _tempRoot);

        var skills = await discovery.DiscoverAsync(CancellationToken.None);

        Assert.DoesNotContain(skills, s => s.Name == "half-baked");
    }

    [Fact]
    public async Task DiscoverAsync_NoLitosDirectoryAnywhere_ReturnsSkillsListWithoutThrowing()
    {
        var isolatedDir = Path.Combine(_tempRoot, "no-skills-here");
        Directory.CreateDirectory(isolatedDir);
        var discovery = new SkillDiscovery(startDirectory: isolatedDir);

        var skills = await discovery.DiscoverAsync(CancellationToken.None);

        // Not asserting an exact empty list: the real (unfakeable) user-profile
        // ~/.litos/skills and ~/.claude/skills paths are always scanned too (see
        // SkillDiscovery.cs), so this only asserts our own temp-tree skill is absent, not
        // that the whole result is empty.
        Assert.DoesNotContain(skills, s => s.DirectoryPath.StartsWith(_tempRoot));
    }

    [Fact]
    public async Task DiscoverAsync_GlobalClaudeSkillsRoot_IsIncluded_WhenPresentOnRealUserProfile()
    {
        // ~/.claude/skills is not fakeable (SkillDiscovery hardcodes the user-profile path),
        // so this only documents the contract rather than asserting exact contents: if the
        // real ~/.claude/skills has any valid SKILL.md directories, they must appear in the
        // result unless a .litos root shadows them by name.
        var claudeSkillsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "skills");
        if (!Directory.Exists(claudeSkillsRoot))
            return; // nothing to assert on a machine without a global .claude/skills root

        var isolatedDir = Path.Combine(_tempRoot, "no-litos-here");
        Directory.CreateDirectory(isolatedDir);
        var discovery = new SkillDiscovery(startDirectory: isolatedDir);

        var skills = await discovery.DiscoverAsync(CancellationToken.None);

        foreach (var dir in Directory.EnumerateDirectories(claudeSkillsRoot))
        {
            var skillMdPath = Path.Combine(dir, "SKILL.md");
            if (!File.Exists(skillMdPath))
                continue;

            var (fields, _) = SkillFrontmatter.Parse(File.ReadAllText(skillMdPath));
            if (fields.TryGetValue("name", out var name) && fields.ContainsKey("description"))
                Assert.Contains(skills, s => s.Name == name);
        }
    }

    [Fact]
    public async Task DiscoverAsync_SkillDirectoryPath_PointsAtTheSkillFolder()
    {
        var skillsRoot = Path.Combine(_tempRoot, ".litos", "skills");
        WriteSkill(skillsRoot, "path-check", "path-check", "desc");
        var discovery = new SkillDiscovery(startDirectory: _tempRoot);

        var skills = await discovery.DiscoverAsync(CancellationToken.None);

        var skill = Assert.Single(skills, s => s.Name == "path-check");
        Assert.Equal(Path.Combine(skillsRoot, "path-check"), skill.DirectoryPath);
    }
}
