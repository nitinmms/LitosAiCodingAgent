using Litos.Agent.Session;
using Litos.Agent.Tools;
using Litos.Gui.Buzzies;

namespace Litos.Gui.Tests.Buzzies;

public class BuzzySystemPromptProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 9, 0, 0, TimeSpan.Zero);

    private sealed class FixedInner : ISystemPromptProvider
    {
        public Task<SystemPromptSections?> BuildAsync(ToolRegistry tools, string? workingDirectory, CancellationToken ct) =>
            Task.FromResult<SystemPromptSections?>(new SystemPromptSections(
                "You are LitosAiAgent.", "Available tools:\n- read_file", "Guidelines", "Available skills:\n- artifact-design: Design guidance",
                [new SystemPromptInstructionFile("C:/project/CLAUDE.md", "project rules")], "footer"));
    }

    [Fact]
    public async Task ReplacesIdentity_AndUsesSoulAndMemoryInPlaceOfProjectInstructions()
    {
        using var temp = new TempDir();
        var store = new BuzzyStore(temp.Path);
        var scout = store.Create(BuzzyTemplates.Scout, "Scout", "#2FB457", Now);
        File.AppendAllText(store.MemoryPathOf(scout.Id), "- 2026-11-26: Prefers bullet lists.\n");

        var sections = await new BuzzySystemPromptProvider(new FixedInner(), scout, store).BuildAsync(new ToolRegistry([]), null, default);

        Assert.StartsWith("You are Scout, a Litos Buzzy", sections!.Identity);
        Assert.Contains("researcher", sections.Identity);
        Assert.Equal(2, sections.Instructions.Count);
        Assert.Contains("careful researcher", sections.Instructions[0].Content);
        Assert.Contains("Prefers bullet lists", sections.Instructions[1].Content);
        Assert.DoesNotContain("project rules", sections.Render());
        Assert.Equal("Available tools:\n- read_file", sections.ToolsList);
        // The skill catalog (with the built-in artifact-design skill) is kept as the plain chat has it.
        Assert.Contains("artifact-design", sections.SkillsCatalog);
    }

    [Fact]
    public async Task EditsToSoulApplyOnTheNextBuild()
    {
        using var temp = new TempDir();
        var store = new BuzzyStore(temp.Path);
        var buddy = store.Create(BuzzyTemplates.Buddy, "Buddy", "#FFC21A", Now);
        var provider = new BuzzySystemPromptProvider(new FixedInner(), buddy, store);

        File.WriteAllText(store.SoulPathOf(buddy.Id), "Always answer in haiku.");
        var sections = await provider.BuildAsync(new ToolRegistry([]), null, default);

        Assert.Equal("Always answer in haiku.", sections!.Instructions[0].Content);
    }

    [Fact]
    public async Task OversizedMemory_IsTruncated()
    {
        using var temp = new TempDir();
        var store = new BuzzyStore(temp.Path);
        var buddy = store.Create(BuzzyTemplates.Buddy, "Buddy", "#FFC21A", Now);
        File.WriteAllText(store.MemoryPathOf(buddy.Id), new string('m', BuzzySystemPromptProvider.MaxFileChars + 500));

        var sections = await new BuzzySystemPromptProvider(new FixedInner(), buddy, store).BuildAsync(new ToolRegistry([]), null, default);

        Assert.True(sections!.Instructions[1].Content.Length < BuzzySystemPromptProvider.MaxFileChars + 50);
        Assert.EndsWith("[…truncated]", sections.Instructions[1].Content);
    }

    [Fact]
    public async Task MissingFiles_AreSkipped()
    {
        using var temp = new TempDir();
        var store = new BuzzyStore(temp.Path);
        var buddy = store.Create(BuzzyTemplates.Buddy, "Buddy", "#FFC21A", Now);
        File.Delete(store.SoulPathOf(buddy.Id));
        File.Delete(store.MemoryPathOf(buddy.Id));

        var sections = await new BuzzySystemPromptProvider(new FixedInner(), buddy, store).BuildAsync(new ToolRegistry([]), null, default);

        Assert.Empty(sections!.Instructions);
    }
}

public class BuzzyAvatarMotionTests
{
    [Fact]
    public void Idle_BobsGentlyWithoutTilt()
    {
        var samples = Enumerable.Range(0, 100).Select(i => AnimationFrame.At(i * 0.05, working: false)).ToList();

        Assert.All(samples, s => Assert.InRange(s.OffsetY, -2.0001, 0.0001));
        Assert.All(samples, s => Assert.Equal(0, s.Tilt));
    }

    [Fact]
    public void Working_WingsBeatMuchFasterThanIdle()
    {
        static int Beats(bool working) =>
            Enumerable.Range(1, 600)
                .Count(i => AnimationFrame.Wing(i / 600.0, working).ScaleY > 0.99 && AnimationFrame.Wing((i - 1) / 600.0, working).ScaleY <= 0.99);

        Assert.True(Beats(working: true) >= 5 * Math.Max(1, Beats(working: false)));
    }

    [Fact]
    public void Wing_StaysWithinItsRange()
    {
        foreach (var working in new[] { false, true })
        foreach (var t in Enumerable.Range(0, 200).Select(i => i * 0.013))
        {
            var pose = AnimationFrame.Wing(t, working);
            Assert.InRange(pose.ScaleY, 0.4999, 1.0001);
            Assert.InRange(pose.Angle, -8.0001, 6.0001);
        }
    }

    [Fact]
    public void Shade_DarkensEachChannel()
    {
        var shaded = BuzzyAvatar.Shade(Avalonia.Media.Color.FromRgb(200, 100, 50), 0.5);

        Assert.Equal((byte)100, shaded.R);
        Assert.Equal((byte)50, shaded.G);
        Assert.Equal((byte)25, shaded.B);
    }
}
