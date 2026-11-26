using System.Text.Json;
using Litos.Agent.Tools;
using Litos.Gui.Buzzies;
using Litos.Tools.Shell;

namespace Litos.Gui.Tests.Buzzies;

public class WorkspaceScopedToolTests
{
    [Fact]
    public async Task AllowedPath_IsPassedOnAsAnAbsolutePathInsideTheWorkspace()
    {
        using var temp = new TempDir();
        var inner = new RecordingTool("read_file");

        var result = await new WorkspaceScopedTool(inner, temp.Path).InvokeAsync(Args.Of(new { path = "notes/a.md", extra = 3 }), default);

        Assert.False(result.IsError);
        Assert.Equal(Path.Combine(Path.GetFullPath(temp.Path), "notes", "a.md"), inner.LastPath);
        Assert.Equal(3, inner.Calls[0].GetProperty("extra").GetInt32());
    }

    [Fact]
    public async Task RefusedPath_NeverReachesTheTool()
    {
        using var temp = new TempDir();
        var inner = new RecordingTool("write_file");

        var result = await new WorkspaceScopedTool(inner, temp.Path).InvokeAsync(Args.Of(new { path = "../../escape.txt", content = "x" }), default);

        Assert.True(result.IsError);
        Assert.Empty(inner.Calls);
    }

    [Fact]
    public async Task MissingOptionalPath_DefaultsToTheWorkspace_NotTheProcessDirectory()
    {
        using var temp = new TempDir();
        var inner = new RecordingTool("search_code", pathRequired: false);

        await new WorkspaceScopedTool(inner, temp.Path).InvokeAsync(Args.Of(new { pattern = "TODO" }), default);

        Assert.Equal(Path.GetFullPath(temp.Path), inner.LastPath);
    }

    [Fact]
    public async Task MissingRequiredPath_IsLeftForTheToolToReport()
    {
        using var temp = new TempDir();
        var inner = new RecordingTool("read_file");

        await new WorkspaceScopedTool(inner, temp.Path).InvokeAsync(Args.Of(new { }), default);

        Assert.Null(inner.LastPath);
        Assert.Single(inner.Calls);
    }
}

public class ApprovalRequiredToolTests
{
    [Fact]
    public async Task Approved_RunsTheTool_AndTheCardShowsTheCommand()
    {
        var approvals = new PendingApprovalStore();
        PendingApproval? asked = null;
        approvals.Added += a =>
        {
            asked = a;
            approvals.Resolve(a.Id, ApprovalDecision.Approve);
        };
        var inner = new RecordingTool("shell", pathRequired: false);

        var result = await new ApprovalRequiredTool(inner, approvals, "Builder", "note").InvokeAsync(Args.Of(new { command = "dotnet test" }), default);

        Assert.False(result.IsError);
        Assert.Single(inner.Calls);
        Assert.Equal("dotnet test", asked!.Preview.DiffOrCommand);
        Assert.Contains("Builder", asked.Preview.Summary);
    }

    [Fact]
    public async Task Denied_DoesNotRunTheTool()
    {
        var approvals = new PendingApprovalStore();
        approvals.Added += a => approvals.Resolve(a.Id, ApprovalDecision.Deny);
        var inner = new RecordingTool("shell", pathRequired: false);

        var result = await new ApprovalRequiredTool(inner, approvals, "Builder", "note").InvokeAsync(Args.Of(new { command = "del *" }), default);

        Assert.True(result.IsError);
        Assert.Empty(inner.Calls);
    }

    [Fact]
    public async Task EveryCallAsksAgain()
    {
        var approvals = new PendingApprovalStore();
        var asks = 0;
        approvals.Added += a =>
        {
            asks++;
            approvals.Resolve(a.Id, ApprovalDecision.ApproveAlways);
        };
        var tool = new ApprovalRequiredTool(new RecordingTool("shell", pathRequired: false), approvals, "Builder", "note");

        await tool.InvokeAsync(Args.Of(new { command = "dir" }), default);
        await tool.InvokeAsync(Args.Of(new { command = "dir" }), default);

        Assert.Equal(2, asks);
    }

    [Fact]
    public async Task CancellingTheTurn_StopsWaitingForTheCard()
    {
        var approvals = new PendingApprovalStore();
        using var cts = new CancellationTokenSource();
        var tool = new ApprovalRequiredTool(new RecordingTool("shell", pathRequired: false), approvals, "Builder", "note");

        var call = tool.InvokeAsync(Args.Of(new { command = "dir" }), cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
    }
}

public class RememberToolTests
{
    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    [Fact]
    public async Task AppendsADatedLine()
    {
        using var temp = new TempDir();
        var memory = Path.Combine(temp.Path, "MEMORY.md");
        File.WriteAllText(memory, "# Memory\n\n");

        var result = await new RememberTool(memory, new FixedClock()).InvokeAsync(Args.Of(new { note = "Prefers bullet lists.\nAlways." }), default);

        Assert.False(result.IsError);
        Assert.EndsWith($"- 2026-11-26: Prefers bullet lists. Always.{Environment.NewLine}", File.ReadAllText(memory));
    }

    [Fact]
    public async Task RefusesEmptyAndOverlongNotes()
    {
        using var temp = new TempDir();
        var tool = new RememberTool(Path.Combine(temp.Path, "MEMORY.md"));

        Assert.True((await tool.InvokeAsync(Args.Of(new { note = " " }), default)).IsError);
        Assert.True((await tool.InvokeAsync(Args.Of(new { note = new string('x', RememberTool.MaxNoteLength + 1) }), default)).IsError);
    }

    [Fact]
    public async Task StopsWhenMemoryIsFull()
    {
        using var temp = new TempDir();
        var memory = Path.Combine(temp.Path, "MEMORY.md");
        File.WriteAllText(memory, new string('x', RememberTool.MaxMemoryBytes + 1));

        var result = await new RememberTool(memory).InvokeAsync(Args.Of(new { note = "one more" }), default);

        Assert.True(result.IsError);
        Assert.Contains("full", result.Text);
    }
}

public class BuzzyToolPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 9, 0, 0, TimeSpan.Zero);

    private static ToolRegistry FullRegistry() => new(
    [
        new RecordingTool("read_file"), new RecordingTool("write_file"), new RecordingTool("edit_file"),
        new RecordingTool("list_directory"), new RecordingTool("search_code", pathRequired: false),
        new RecordingTool("share_file"), new RecordingTool("web_search", pathRequired: false),
        new RecordingTool("skill", pathRequired: false), new RecordingTool("artifact", pathRequired: false),
        new RecordingTool("shell", pathRequired: false), new RecordingTool("mcp__github__create_issue", pathRequired: false),
    ]);

    private static IReadOnlyList<string> Names(ToolRegistry registry) => [.. registry.Schemas.Select(s => s.Name).Order()];

    [Fact]
    public void Scout_GetsItsAllowlist_WithNoShellOrMcp()
    {
        using var temp = new TempDir();
        var store = new BuzzyStore(temp.Path);
        var scout = store.Create(BuzzyTemplates.Scout, "Scout", "#2FB457", Now);

        var registry = BuzzyToolPolicy.Build(FullRegistry(), scout, new BuzzyToolContext(store, new PendingApprovalStore()));

        Assert.Equal(BuzzyTemplates.Scout.Tools.Order(), Names(registry));
        Assert.DoesNotContain("shell", Names(registry));
        Assert.DoesNotContain(Names(registry), n => n.StartsWith("mcp__", StringComparison.Ordinal));
    }

    [Fact]
    public void FileToolsAreWorkspaceScoped_ShellAsks_RememberIsTheBuzzysOwn()
    {
        using var temp = new TempDir();
        var store = new BuzzyStore(temp.Path);
        var builder = store.Create(BuzzyTemplates.Builder, "Builder", "#9B4DE0", Now);

        var registry = BuzzyToolPolicy.Build(FullRegistry(), builder, new BuzzyToolContext(store, new PendingApprovalStore()));

        foreach (var name in BuzzyToolNames.PathScoped)
            Assert.IsType<WorkspaceScopedTool>(registry.Resolve(name));
        Assert.IsType<ApprovalRequiredTool>(registry.Resolve("shell"));
        Assert.IsType<RememberTool>(registry.Resolve("remember"));
        Assert.IsType<RecordingTool>(registry.Resolve("web_search"));
    }

    [Fact]
    public void HandEditedProfile_CannotGrantUnknownOrMcpTools()
    {
        using var temp = new TempDir();
        var store = new BuzzyStore(temp.Path);
        var scout = store.Create(BuzzyTemplates.Scout, "Scout", "#2FB457", Now) with
        {
            Tools = ["read_file", "mcp__github__create_issue", "run_kernel_code"],
        };

        var registry = BuzzyToolPolicy.Build(FullRegistry(), scout, new BuzzyToolContext(store, new PendingApprovalStore()));

        Assert.Equal(["artifact", "read_file", "skill"], Names(registry));
    }

    [Fact]
    public void ArtifactsAndSkills_AreAlwaysGranted_EvenWhenTheProfileLeavesThemOut()
    {
        using var temp = new TempDir();
        var store = new BuzzyStore(temp.Path);
        var bare = store.Create(BuzzyTemplates.Buddy, "Bare", "#FFC21A", Now) with { Tools = [] };

        var registry = BuzzyToolPolicy.Build(FullRegistry(), bare, new BuzzyToolContext(store, new PendingApprovalStore()));

        Assert.Equal(["artifact", "skill"], Names(registry));
        // The same tool instance the plain chat uses, unwrapped: artifacts publish as they do there.
        Assert.IsType<RecordingTool>(registry.Resolve("artifact"));
    }

    [Fact]
    public void Unattended_LeavesShellOut_EvenForBuilder()
    {
        using var temp = new TempDir();
        var store = new BuzzyStore(temp.Path);
        var builder = store.Create(BuzzyTemplates.Builder, "Builder", "#9B4DE0", Now);

        var registry = BuzzyToolPolicy.Build(FullRegistry(), builder, new BuzzyToolContext(store, new PendingApprovalStore(), Unattended: true));

        Assert.DoesNotContain("shell", Names(registry));
        Assert.Contains("write_file", Names(registry));
    }

    [Fact]
    public void ShellFactory_IsGivenTheBuzzysWorkspace()
    {
        using var temp = new TempDir();
        var store = new BuzzyStore(temp.Path);
        var builder = store.Create(BuzzyTemplates.Builder, "Builder", "#9B4DE0", Now);
        string? startedIn = null;

        BuzzyToolPolicy.Build(FullRegistry(), builder, new BuzzyToolContext(store, new PendingApprovalStore(),
            ShellFactory: workspace => { startedIn = workspace; return new RecordingTool("shell", pathRequired: false); }));

        Assert.Equal(store.WorkspaceOf(builder.Id), startedIn);
    }

    [Fact]
    public async Task SkillTool_SeesTheBuzzysOwnSkills()
    {
        using var temp = new TempDir();
        var store = new BuzzyStore(temp.Path);
        var scout = store.Create(BuzzyTemplates.Scout, "Scout", "#2FB457", Now);
        var skillDir = Directory.CreateDirectory(Path.Combine(store.SkillsOf(scout.Id), "weekly-report")).FullName;
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), "---\nname: weekly-report\ndescription: How Scout writes the weekly report\n---\nUse bullet lists.");

        var registry = BuzzyToolPolicy.Build(FullRegistry(), scout, new BuzzyToolContext(store, new PendingApprovalStore()));
        var result = await registry.Resolve("skill").InvokeAsync(Args.Of(new { name = "weekly-report" }), default);

        Assert.IsType<Litos.Tools.Skills.SkillTool>(registry.Resolve("skill"));
        Assert.False(result.IsError, result.Text);
        Assert.Contains("Use bullet lists.", result.Text);
    }

    [Fact]
    public void EveryTemplate_GrantsArtifacts()
    {
        Assert.All(BuzzyTemplates.All, t => Assert.Contains(BuzzyToolNames.Artifact, t.Tools));
    }

    [Fact]
    public async Task ScopedReadFile_RefusesAPathOutsideThatBuzzysWorkspace()
    {
        using var temp = new TempDir();
        var store = new BuzzyStore(temp.Path);
        var scout = store.Create(BuzzyTemplates.Scout, "Scout", "#2FB457", Now);
        var builder = store.Create(BuzzyTemplates.Builder, "Builder", "#9B4DE0", Now);
        var registry = BuzzyToolPolicy.Build(FullRegistry(), scout, new BuzzyToolContext(store, new PendingApprovalStore()));

        var otherBuzzysFile = Path.Combine(store.WorkspaceOf(builder.Id), "secret.txt");
        var result = await registry.Resolve("read_file").InvokeAsync(Args.Of(new { path = otherBuzzysFile }), default);

        Assert.True(result.IsError);
    }

    [Fact]
    public void ToolMissingFromTheAvailableRegistry_IsSimplyAbsent()
    {
        using var temp = new TempDir();
        var store = new BuzzyStore(temp.Path);
        var scout = store.Create(BuzzyTemplates.Scout, "Scout", "#2FB457", Now);
        var noWebSearch = new ToolRegistry([new RecordingTool("read_file")]);

        var registry = BuzzyToolPolicy.Build(noWebSearch, scout, new BuzzyToolContext(store, new PendingApprovalStore()));

        Assert.Equal(["read_file", "remember"], Names(registry));
        // artifact and skill are granted but not offered by this registry, so they're absent too.
    }
}
