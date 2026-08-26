using Litos.Agent.Tools;
using Litos.Host.Tests.Fakes;
// ReservedToolNames.KernelCode — the fixed name the toggle-conditional Guidelines below key off of.
using Litos.Tools.ProjectInstructions;
using Litos.Tools.Skills;

namespace Litos.Host.Tests;

public class LitosSystemPromptProviderTests
{
    private static (LitosSystemPromptProvider Provider, ToolRegistry Tools) CreateProvider(
        IEnumerable<ITool> tools,
        IReadOnlyList<SkillMetadata>? skills = null,
        IReadOnlyList<ProjectInstructionsFile>? instructionFiles = null)
    {
        var registry = new ToolRegistry(tools);
        var skillDiscovery = new FakeSkillDiscovery(skills ?? []);
        var projectInstructionsDiscovery = new FakeProjectInstructionsDiscovery(instructionFiles ?? []);
        return (new LitosSystemPromptProvider(skillDiscovery, projectInstructionsDiscovery), registry);
    }

    [Fact]
    public async Task BuildAsync_ListsEveryRegisteredTool_WithItsDescription()
    {
        var (provider, tools) = CreateProvider([new FakeTool("shell", "Run a shell command."), new FakeTool("search_code", "Search file contents.")]);

        var prompt = (await provider.BuildAsync(tools, workingDirectory: null, CancellationToken.None))?.Render();

        Assert.Contains("- shell: Run a shell command.", prompt);
        Assert.Contains("- search_code: Search file contents.", prompt);
    }

    [Fact]
    public async Task BuildAsync_Guidelines_SteerModelTowardSearchCode_OverShellingOutForSearch()
    {
        var (provider, tools) = CreateProvider([new FakeTool("shell"), new FakeTool("search_code")]);

        var prompt = (await provider.BuildAsync(tools, workingDirectory: null, CancellationToken.None))?.Render();

        Assert.Contains("search_code", prompt);
        Assert.Contains("grep", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BuildAsync_NoTools_ShowsPlaceholder()
    {
        var (provider, tools) = CreateProvider([]);

        var prompt = (await provider.BuildAsync(tools, workingDirectory: null, CancellationToken.None))?.Render();

        Assert.Contains("(none)", prompt);
    }

    [Fact]
    public async Task BuildAsync_NoSkills_OmitsSkillsSection()
    {
        var (provider, tools) = CreateProvider([new FakeTool("read_file")], skills: []);

        var prompt = (await provider.BuildAsync(tools, workingDirectory: null, CancellationToken.None))?.Render();

        Assert.DoesNotContain("Available skills", prompt);
    }

    [Fact]
    public async Task BuildAsync_WithSkills_ListsEachSkill_ByNameAndDescription()
    {
        var skills = new[] { new SkillMetadata("deploy", "Deploys the app.", "/skills/deploy") };
        var (provider, tools) = CreateProvider([new FakeTool("read_file")], skills);

        var prompt = (await provider.BuildAsync(tools, workingDirectory: null, CancellationToken.None))?.Render();

        Assert.Contains("Available skills", prompt);
        Assert.Contains("- deploy: Deploys the app.", prompt);
    }

    [Fact]
    public async Task BuildAsync_IncludesGivenWorkingDirectory_WithForwardSlashes()
    {
        var (provider, tools) = CreateProvider([]);

        var prompt = (await provider.BuildAsync(tools, workingDirectory: @"C:\repo\project", CancellationToken.None))?.Render();

        Assert.Contains("Current working directory: C:/repo/project", prompt);
    }

    [Fact]
    public async Task BuildAsync_NullWorkingDirectory_FallsBackToCurrentDirectory()
    {
        var (provider, tools) = CreateProvider([]);

        var prompt = (await provider.BuildAsync(tools, workingDirectory: null, CancellationToken.None))?.Render();

        var expected = Directory.GetCurrentDirectory().Replace('\\', '/');
        Assert.Contains($"Current working directory: {expected}", prompt);
    }

    [Fact]
    public async Task BuildAsync_NoInstructionFiles_OmitsInstructionsSection()
    {
        var (provider, tools) = CreateProvider([], instructionFiles: []);

        var prompt = (await provider.BuildAsync(tools, workingDirectory: null, CancellationToken.None))?.Render();

        Assert.DoesNotContain("Instructions from", prompt);
    }

    [Fact]
    public async Task BuildAsync_WithInstructionFile_IncludesItsPathAndContent()
    {
        var files = new[] { new ProjectInstructionsFile(@"C:\repo\AGENTS.md", "Use tabs, not spaces.") };
        var (provider, tools) = CreateProvider([], instructionFiles: files);

        var prompt = (await provider.BuildAsync(tools, workingDirectory: null, CancellationToken.None))?.Render();

        Assert.Contains("Instructions from C:/repo/AGENTS.md:", prompt);
        Assert.Contains("Use tabs, not spaces.", prompt);
    }

    // ---- Kernel-mode-conditional Guidelines (ReadMe_PTCPersistentKernel.md §6, §8.8) ----

    [Fact]
    public async Task BuildAsync_OrdinaryToolList_UsesTheOffStateGuidelines()
    {
        var (provider, tools) = CreateProvider([new FakeTool("shell"), new FakeTool("search_code")]);

        var prompt = (await provider.BuildAsync(tools, workingDirectory: null, CancellationToken.None))?.Render();

        Assert.Contains("search_code", prompt);
        Assert.DoesNotContain("run_kernel_code", prompt);
    }

    [Fact]
    public async Task BuildAsync_ToolRegistryIsExactlyRunKernelCode_SwitchesToKernelModeGuidelines()
    {
        var (provider, tools) = CreateProvider([new FakeTool(ReservedToolNames.KernelCode, "Run a persistent C# kernel script.")]);

        var prompt = (await provider.BuildAsync(tools, workingDirectory: null, CancellationToken.None))?.Render();

        Assert.Contains("run_kernel_code is your only tool this session", prompt);
        // The OFF-state search_code steering is specific to having an ordinary tool list — must not
        // leak into the ON-state Guidelines, which has nothing but run_kernel_code to steer toward.
        Assert.DoesNotContain("ALWAYS use search_code", prompt);
    }

    [Fact]
    public async Task BuildAsync_KernelModeGuidelines_MentionsStatePersistenceAndShortOutputAdvice()
    {
        var (provider, tools) = CreateProvider([new FakeTool(ReservedToolNames.KernelCode, "Run a persistent C# kernel script.")]);

        var prompt = (await provider.BuildAsync(tools, workingDirectory: null, CancellationToken.None))?.Render();

        Assert.Contains("persist", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("KernelState.List", prompt);
    }

    [Fact]
    public async Task BuildAsync_RunKernelCodePlusAnotherTool_IsNotTreatedAsKernelModeOn()
    {
        // Kernel-only means tools.Schemas has EXACTLY one entry (§1/§6) — a registry that somehow
        // contains run_kernel_code alongside anything else is not the toggle's ON shape and should
        // fall back to the ordinary Guidelines rather than silently claiming exclusivity that isn't
        // actually true.
        var (provider, tools) = CreateProvider([
            new FakeTool(ReservedToolNames.KernelCode, "Run a persistent C# kernel script."),
            new FakeTool("shell"),
        ]);

        var prompt = (await provider.BuildAsync(tools, workingDirectory: null, CancellationToken.None))?.Render();

        Assert.DoesNotContain("run_kernel_code is your only tool this session", prompt);
    }

    [Fact]
    public async Task BuildAsync_WithMultipleInstructionFiles_IncludesEachInOrder()
    {
        var files = new[]
        {
            new ProjectInstructionsFile(@"C:\repo\AGENTS.md", "Global rule."),
            new ProjectInstructionsFile(@"C:\repo\sub\AGENTS.md", "Nested rule."),
        };
        var (provider, tools) = CreateProvider([], instructionFiles: files);

        var prompt = (await provider.BuildAsync(tools, workingDirectory: null, CancellationToken.None))?.Render();

        Assert.NotNull(prompt);
        Assert.True(prompt.IndexOf("Global rule.", StringComparison.Ordinal)
            < prompt.IndexOf("Nested rule.", StringComparison.Ordinal));
    }
}
