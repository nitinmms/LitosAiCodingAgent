using Litos.Tools.ProjectInstructions;

namespace Litos.Tools.Tests.ProjectInstructions;

public class ProjectInstructionsDiscoveryTests : IDisposable
{
    private readonly string _tempRoot = Directory.CreateTempSubdirectory("litos-projectinstructions-").FullName;

    public void Dispose() => Directory.Delete(_tempRoot, recursive: true);

    [Fact]
    public async Task DiscoverAsync_FindsAgentsMd_InStartDirectory()
    {
        File.WriteAllText(Path.Combine(_tempRoot, "AGENTS.md"), "Root instructions.");
        var discovery = new ProjectInstructionsDiscovery(startDirectory: _tempRoot);

        var files = await discovery.DiscoverAsync(CancellationToken.None);

        Assert.Contains(files, f => f.Path == Path.Combine(_tempRoot, "AGENTS.md") && f.Content == "Root instructions.");
    }

    [Fact]
    public async Task DiscoverAsync_PrefersAgentsMdOverClaudeMd_InSameDirectory()
    {
        File.WriteAllText(Path.Combine(_tempRoot, "AGENTS.md"), "Agents version.");
        File.WriteAllText(Path.Combine(_tempRoot, "CLAUDE.md"), "Claude version.");
        var discovery = new ProjectInstructionsDiscovery(startDirectory: _tempRoot);

        var files = await discovery.DiscoverAsync(CancellationToken.None);

        var match = Assert.Single(files, f => Path.GetDirectoryName(f.Path) == _tempRoot);
        Assert.Equal("Agents version.", match.Content);
    }

    [Fact]
    public async Task DiscoverAsync_FallsBackToClaudeMd_WhenAgentsMdAbsent()
    {
        File.WriteAllText(Path.Combine(_tempRoot, "CLAUDE.md"), "Claude only.");
        var discovery = new ProjectInstructionsDiscovery(startDirectory: _tempRoot);

        var files = await discovery.DiscoverAsync(CancellationToken.None);

        Assert.Contains(files, f => f.Path == Path.Combine(_tempRoot, "CLAUDE.md") && f.Content == "Claude only.");
    }

    [Fact]
    public async Task DiscoverAsync_WalksUpFromNestedStartDirectory_ToFindAncestorFile()
    {
        File.WriteAllText(Path.Combine(_tempRoot, "AGENTS.md"), "From the top.");
        var nested = Path.Combine(_tempRoot, "child", "grandchild");
        Directory.CreateDirectory(nested);
        var discovery = new ProjectInstructionsDiscovery(startDirectory: nested);

        var files = await discovery.DiscoverAsync(CancellationToken.None);

        Assert.Contains(files, f => f.Content == "From the top.");
    }

    [Fact]
    public async Task DiscoverAsync_ReturnsAncestorBeforeStartDirectory_InConcatenationOrder()
    {
        File.WriteAllText(Path.Combine(_tempRoot, "AGENTS.md"), "Ancestor.");
        var childDir = Path.Combine(_tempRoot, "child");
        Directory.CreateDirectory(childDir);
        File.WriteAllText(Path.Combine(childDir, "AGENTS.md"), "Closer.");
        var discovery = new ProjectInstructionsDiscovery(startDirectory: childDir);

        var files = await discovery.DiscoverAsync(CancellationToken.None);

        var ancestorIndex = files.ToList().FindIndex(f => f.Content == "Ancestor.");
        var closerIndex = files.ToList().FindIndex(f => f.Content == "Closer.");
        Assert.True(ancestorIndex >= 0 && closerIndex >= 0 && ancestorIndex < closerIndex);
    }

    [Fact]
    public async Task DiscoverAsync_IncludesBothAncestorAndStartDirectoryFiles_WhenBothPresent()
    {
        File.WriteAllText(Path.Combine(_tempRoot, "AGENTS.md"), "Ancestor.");
        var childDir = Path.Combine(_tempRoot, "child");
        Directory.CreateDirectory(childDir);
        File.WriteAllText(Path.Combine(childDir, "AGENTS.md"), "Closer.");
        var discovery = new ProjectInstructionsDiscovery(startDirectory: childDir);

        var files = await discovery.DiscoverAsync(CancellationToken.None);

        Assert.Contains(files, f => f.Content == "Ancestor.");
        Assert.Contains(files, f => f.Content == "Closer.");
    }

    [Fact]
    public async Task DiscoverAsync_NoInstructionFileAnywhereInTempTree_ReturnsListWithoutThrowing()
    {
        var isolatedDir = Path.Combine(_tempRoot, "no-instructions-here");
        Directory.CreateDirectory(isolatedDir);
        var discovery = new ProjectInstructionsDiscovery(startDirectory: isolatedDir);

        var files = await discovery.DiscoverAsync(CancellationToken.None);

        // Not asserting an exact empty list: the real (unfakeable) user-profile ~/.litos
        // path is always checked too (see ProjectInstructionsDiscovery.cs), so this only
        // asserts our own temp-tree has contributed nothing, not that the whole result is empty.
        Assert.DoesNotContain(files, f => f.Path.StartsWith(_tempRoot));
    }
}
