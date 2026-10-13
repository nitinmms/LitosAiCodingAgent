using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Settings;

namespace Litos.SoftwareFactory.Core.Tests.Settings;

/// <summary>The Skills tab, which repository skills a run uses, and what it snapshots (m3-architecture.md §7.3).</summary>
public class SkillSettingsTests
{
    private static LibrarySkill Skill(string name = "release-notes", string description = "Write release notes from the merged changes.") =>
        new() { Name = name, Description = description, Body = "Group the changes by area.\nLink each one." };

    [Fact]
    public void TheDefaults_HaveNoLibrary_WaitForApproval_AndAreValid()
    {
        var skills = new SkillSettings();

        Assert.Empty(skills.Library);
        Assert.Equal(RepositorySkillPolicy.ApproveEach, skills.RepositoryPolicy);
        Assert.Empty(skills.Validate());
        Assert.Empty(new SkillSettings { Library = [Skill(), Skill("a.b_c-1")] }.Validate());
    }

    [Fact]
    public void ANewSkill_IsOn_AndOnlyTheOnesThatAreOn_AreGivenToRuns()
    {
        var skills = new SkillSettings { Library = [Skill("on"), Skill("off") with { Enabled = false }] };

        Assert.True(Skill().Enabled);
        Assert.Equal(["on"], skills.Enabled.Select(s => s.Name));
    }

    public static TheoryData<LibrarySkill, string> Invalid => new()
    {
        { Skill("-dash"), "is not a usable skill name" },
        { Skill("has space"), "is not a usable skill name" },
        { Skill(new string('a', 65)), "is not a usable skill name" },
        { Skill(description: " "), "describe when the agent should use it" },
        { Skill(description: "two\nlines"), "must be a single line" },
        { Skill() with { Body = "  " }, "write its instructions" },
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public void ASkillThatCannotWork_IsRefused_WithWhy(LibrarySkill skill, string error) =>
        Assert.Contains(error, Assert.Single(new SkillSettings { Library = [skill] }.Validate()));

    [Fact]
    public void ATooLongDescriptionOrBody_IsRefused()
    {
        Assert.Contains("at most 1,024 characters",
            Assert.Single(new SkillSettings { Library = [Skill(description: new string('d', SkillSettings.MaxDescriptionChars + 1))] }.Validate()));
        Assert.Contains("at most 100,000 characters",
            Assert.Single(new SkillSettings { Library = [Skill() with { Body = new string('b', SkillSettings.MaxBodyChars + 1) }] }.Validate()));
    }

    [Fact]
    public void TwoSkillsOfOneName_InAnyCase_AreRefused() =>
        Assert.Contains("two skills named", Assert.Single(new SkillSettings { Library = [Skill("lint"), Skill("LINT")] }.Validate()));

    [Fact]
    public void ALibraryOverTheLimit_IsRefused() =>
        Assert.Contains("at most 100 skills",
            new SkillSettings { Library = [.. Enumerable.Range(0, SkillSettings.MaxSkills + 1).Select(i => Skill($"s{i}"))] }.Validate().First());

    [Fact]
    public void AnUnknownPolicy_IsRefused() =>
        Assert.Contains("how repository skills are treated", Assert.Single(new SkillSettings { RepositoryPolicy = (RepositorySkillPolicy)7 }.Validate()));

    [Fact]
    public void ASkillsMarkdown_HasTheFrontmatterTheEngineReads_AndItsBodyWithUnixLineEnds()
    {
        var markdown = (Skill() with { Body = "One.\r\nTwo." }).Markdown();

        Assert.Equal("---\nname: release-notes\ndescription: Write release notes from the merged changes.\n---\n\nOne.\nTwo.\n", markdown);
    }

    // ---- Which repository skills a run uses ----

    private static readonly FoundSkill Deploy = new("deploy", "Deploy to staging.", "sha256:aaa");
    private static readonly FoundSkill Lint = new("lint", "Run the linters.", "sha256:bbb");

    [Fact]
    public void UnderApproveEach_OnlyTheApprovedVersion_IsUsed_AndAChangedOneSaysSo()
    {
        var uses = SkillSelection.Decide(RepositorySkillPolicy.ApproveEach, [Deploy, Lint, new FoundSkill("new", "", "sha256:ccc")],
            [("deploy", "sha256:aaa"), ("lint", "sha256:old")], library: []);

        Assert.Equal(
            [RepositorySkillStatus.Used, RepositorySkillStatus.ChangedSinceApproved, RepositorySkillStatus.WaitingForApproval],
            uses.Select(u => u.Status));
        Assert.True(uses[0].Used);
        Assert.False(uses[1].Used);
    }

    [Fact]
    public void Always_UsesEverySkill_AndNever_UsesNone_WhateverWasApproved()
    {
        Assert.All(SkillSelection.Decide(RepositorySkillPolicy.Always, [Deploy, Lint], [], []), u => Assert.True(u.Used));
        Assert.All(SkillSelection.Decide(RepositorySkillPolicy.Never, [Deploy, Lint], [("deploy", "sha256:aaa")], []),
            u => Assert.Equal(RepositorySkillStatus.NotUsedByPolicy, u.Status));
    }

    [Fact]
    public void WhereTwoFoldersGiveOneName_TheFirstIsTheSkill() =>
        Assert.Equal("sha256:aaa", Assert.Single(SkillSelection.Decide(RepositorySkillPolicy.Always, [Deploy, Deploy with { Hash = "sha256:zzz" }], [], [])).Hash);

    [Fact]
    public void AUsedRepositorySkill_ReplacesTheLibrarysOfTheSameName_AndOneNotUsedDoesNot()
    {
        var used = SkillSelection.Decide(RepositorySkillPolicy.Always, [Deploy], [], library: ["deploy"]);
        var waiting = SkillSelection.Decide(RepositorySkillPolicy.ApproveEach, [Deploy], [], library: ["deploy"]);

        Assert.True(Assert.Single(used).ReplacesLibrarySkill);
        Assert.False(Assert.Single(waiting).ReplacesLibrarySkill);
    }

    [Fact]
    public void TheThread_IsToldWhatWasSkipped_AndWhatReplacedALibrarySkill()
    {
        var text = SkillSelection.Describe(
        [
            new RepositorySkillUse("deploy", "h1", RepositorySkillStatus.WaitingForApproval),
            new RepositorySkillUse("lint", "h2", RepositorySkillStatus.ChangedSinceApproved),
            new RepositorySkillUse("notes", "h3", RepositorySkillStatus.Used, ReplacesLibrarySkill: true),
            new RepositorySkillUse("fine", "h4", RepositorySkillStatus.Used),
        ]);

        Assert.Equal(
            "Skipped repository skills waiting for an Admin's approval: deploy, lint (changed since it was approved).\n" +
            "The repository's skill \"notes\" is used in place of the factory library's.",
            text);
    }

    [Fact]
    public void TheThread_IsToldWhenThePolicyTurnsRepositorySkillsOff_AndNothingWhenAllIsUsed()
    {
        Assert.Equal("The repository's skills are not used, by the factory's settings: deploy.",
            SkillSelection.Describe([new RepositorySkillUse("deploy", "h", RepositorySkillStatus.NotUsedByPolicy)]));
        Assert.Null(SkillSelection.Describe([new RepositorySkillUse("deploy", "h", RepositorySkillStatus.Used)]));
        Assert.Null(SkillSelection.Describe([]));
    }

    // ---- The run's snapshot ----

    [Fact]
    public void ARunSnapshots_TheLibraryRevision_ItsEnabledSkills_AndThePolicy()
    {
        var skills = new SkillSettings { RepositoryPolicy = RepositorySkillPolicy.Always, Library = [Skill(), Skill("off") with { Enabled = false }] };

        var capabilities = RunCapabilities.From(new ToolSettings(), WebSearchAccess.Off, new McpSettings(), ptc: true, skills, skillLibraryRevision: 4);

        Assert.Equal(4, capabilities.SkillLibraryRevision);
        Assert.Equal([new RunLibrarySkill("release-notes", "Write release notes from the merged changes.")], capabilities.LibrarySkills);
        Assert.Equal(RepositorySkillPolicy.Always, capabilities.RepositorySkillPolicy);
        Assert.Null(capabilities.RepositorySkills);
    }

    [Fact]
    public void ARework_KeepsTheLibraryAndPolicy_ButLooksAtTheWorkingCopyAgain()
    {
        var first = RunCapabilities.From(new ToolSettings(), WebSearchAccess.Off, new McpSettings(), true, new SkillSettings { Library = [Skill()] }, 2)
            with { RepositorySkills = [new RepositorySkillUse("deploy", "h", RepositorySkillStatus.Used)] };

        var rework = first.ForRework();

        Assert.Equal((2L, 1), (rework.SkillLibraryRevision, rework.LibrarySkills.Count));
        Assert.Null(rework.RepositorySkills);
    }

    [Fact]
    public void ASnapshotFromBeforeSkills_ReadsAsHavingNone()
    {
        var old = System.Text.Json.JsonSerializer.Deserialize<RunCapabilities>("""{"ptc":true,"shellTimeoutSeconds":300}""", FactoryWire.Json)!;

        Assert.Equal((0L, 0, RepositorySkillPolicy.ApproveEach), (old.SkillLibraryRevision, old.LibrarySkills.Count, old.RepositorySkillPolicy));
        Assert.Null(old.RepositorySkills);
    }
}

/// <summary>A skill folder's identity, which an approval is of (m3-architecture.md §7.3).</summary>
public sealed class SkillHashTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("litos-skillhash-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Folder(string name, params (string Path, string Content)[] files)
    {
        var folder = Path.Combine(_root, name);
        foreach (var (path, content) in files)
        {
            var full = Path.Combine(folder, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        return folder;
    }

    [Fact]
    public void TwoFoldersWithTheSameFiles_HaveTheSameHash_WhereverTheyAre()
    {
        var a = Folder("a", ("SKILL.md", "x"), ("scripts/run.ps1", "y"));
        var b = Folder("b", ("scripts/run.ps1", "y"), ("SKILL.md", "x"));

        Assert.Equal(SkillHash.Of(a), SkillHash.Of(b));
        Assert.StartsWith("sha256:", SkillHash.Of(a));
        Assert.Equal(71, SkillHash.Of(a).Length);
    }

    [Fact]
    public void ChangingAHelperScript_AddingAFile_OrRenamingOne_ChangesTheHash()
    {
        var original = SkillHash.Of(Folder("o", ("SKILL.md", "x"), ("run.ps1", "y")));

        Assert.NotEqual(original, SkillHash.Of(Folder("c", ("SKILL.md", "x"), ("run.ps1", "z"))));
        Assert.NotEqual(original, SkillHash.Of(Folder("d", ("SKILL.md", "x"), ("run.ps1", "y"), ("extra.txt", ""))));
        Assert.NotEqual(original, SkillHash.Of(Folder("e", ("SKILL.md", "x"), ("go.ps1", "y"))));
    }

    [Fact]
    public void ContentCannotMoveBetweenAFilesNameAndItsBytes() =>
        Assert.NotEqual(SkillHash.Of(Folder("p", ("ab", "c"))), SkillHash.Of(Folder("q", ("a", "bc"))));

    [Fact]
    public void TheRepositorysSkillFolder_IsDotLitosSkills() =>
        Assert.Equal(Path.Combine("repo", ".litos", "skills"), RepositorySkillFolder.In("repo"));
}
