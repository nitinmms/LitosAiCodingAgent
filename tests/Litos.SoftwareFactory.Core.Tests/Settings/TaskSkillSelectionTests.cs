using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Settings;

namespace Litos.SoftwareFactory.Core.Tests.Settings;

/// <summary>The skills chosen for a task (m3-architecture.md §13): defaults, choosing, the size limit and what a run is given.</summary>
public class TaskSkillSelectionTests
{
    private static LibrarySkill Skill(string name, int chars = 100, params string[] defaultFor) =>
        new() { Name = name, Description = $"When to use {name}.", Body = new string('x', chars), DefaultFor = defaultFor };

    private static SkillSettings Library(params LibrarySkill[] skills) => new() { Library = skills };

    [Fact]
    public void DefaultsFor_AType_AreItsEnabledSkills_InTheLibrarysOrder()
    {
        var library = Library(Skill("tdd", defaultFor: ["bug", "feature"]), Skill("off", defaultFor: ["bug"]) with { Enabled = false }, Skill("prove-it-works", defaultFor: ["bug"]));

        Assert.Equal(["tdd", "prove-it-works"], TaskSkillSelection.DefaultsFor(library, "bug"));
        Assert.Equal(["tdd"], TaskSkillSelection.DefaultsFor(library, "feature"));
        Assert.Empty(TaskSkillSelection.DefaultsFor(library, "chore"));
    }

    [Fact]
    public void Choose_GivesTheLibrarysNames_InTheLibrarysOrder()
    {
        var library = Library(Skill("tdd"), Skill("fix-root-causes"), Skill("prove-it-works"));

        var chosen = TaskSkillSelection.Choose(["PROVE-IT-WORKS", "tdd"], library, out var refused);

        Assert.Null(refused);
        Assert.Equal(["tdd", "prove-it-works"], chosen);
        Assert.Empty(TaskSkillSelection.Choose([], library, out refused));
        Assert.Null(refused);
    }

    public static TheoryData<string[], string> Refused => new()
    {
        { ["nope"], "Not a skill in the factory's library, or turned off: nope." },
        { ["off"], "Not a skill in the factory's library, or turned off: off." },
        { ["tdd", "TDD"], "\"tdd\" is chosen twice." },
        { [" "], "A skill name is empty." },
        { ["tdd", "big"], "These skills come to 12,001 characters; a task can take at most 12,000." },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void Choose_RefusesWhatCannotBeGivenToATask(string[] names, string why)
    {
        var library = Library(Skill("tdd", 1), Skill("big", 12_000), Skill("off") with { Enabled = false });

        Assert.Empty(TaskSkillSelection.Choose(names, library, out var refused));
        Assert.Equal(why, refused);
    }

    [Fact]
    public void Choose_TakesExactlyTheLimit()
    {
        var library = Library(Skill("a", 6_000), Skill("b", 6_000));

        Assert.Equal(["a", "b"], TaskSkillSelection.Choose(["a", "b"], library, out var refused));
        Assert.Null(refused);
    }

    [Fact]
    public void Size_IsTheTextThePromptCarries_TrimmedWithUnixLineEnds()
    {
        var skill = new LibrarySkill { Name = "s", Description = "d", Body = "  one\r\ntwo  \n" };

        Assert.Equal("one\ntwo".Length, TaskSkillSelection.Size([skill]));
    }

    [Fact]
    public void Resolve_GivesEachChosenSkillsText_AndNamesOnesGoneOrOff_WithoutFailing()
    {
        var library = Library(new LibrarySkill { Name = "tdd", Description = "d", Body = "Write the test first.\r\n" }, Skill("off") with { Enabled = false });

        var resolved = TaskSkillSelection.Resolve(["tdd", "off", "deleted"], library);

        Assert.Equal([new TaskSkill("tdd", "Write the test first.")], resolved.Applied);
        Assert.Equal(["off", "deleted"], resolved.Missing);
        Assert.Empty(resolved.OverLimit);
        Assert.Equal(
            "Skills chosen for this task that are no longer in the factory's library, or are turned off, are not applied: off, deleted.",
            TaskSkillSelection.Describe(resolved));
    }

    [Fact]
    public void Resolve_LeavesOutWhatNoLongerFits_AfterASkillGrew()
    {
        var library = Library(Skill("a", 7_000), Skill("b", 7_000), Skill("c", 5_000));

        var resolved = TaskSkillSelection.Resolve(["a", "b", "c"], library);

        Assert.Equal(["a", "c"], resolved.Applied.Select(s => s.Name));
        Assert.Equal(["b"], resolved.OverLimit);
        Assert.Contains("now pass 12,000 characters: b.", TaskSkillSelection.Describe(resolved));
    }

    [Fact]
    public void Describe_IsNull_WhenEverySkillWasApplied() =>
        Assert.Null(TaskSkillSelection.Describe(TaskSkillSelection.Resolve(["a"], Library(Skill("a")))));

    [Theory]
    [InlineData(LifecycleState.Running, "They will be applied after the task is paused and resumed.")]
    [InlineData(LifecycleState.PausedUser, "They will be applied when the task resumes.")]
    [InlineData(LifecycleState.PausedBudget, "They will be applied when the task resumes.")]
    [InlineData(LifecycleState.AwaitingDecision, "They will be applied when the task resumes.")]
    [InlineData(LifecycleState.Blocked, "They will be applied when the task resumes.")]
    [InlineData(LifecycleState.Interrupted, "They will be applied when the task resumes.")]
    [InlineData(LifecycleState.Queued, "They apply when the task starts.")]
    [InlineData(LifecycleState.Draft, "They apply from the task's next run.")]
    [InlineData(LifecycleState.AwaitingHumanTesting, "They apply from the task's next run.")]
    public void ChangeNote_SaysWhenTheChangeTakesEffect(LifecycleState state, string when)
    {
        Assert.Equal($"The skills for this task changed to: tdd, prove-it-works. {when}", TaskSkillSelection.ChangeNote(["tdd", "prove-it-works"], state));
        Assert.StartsWith("The skills for this task were cleared.", TaskSkillSelection.ChangeNote([], state));
    }

    [Fact]
    public void AThreadsChoice_RoundTrips_AndAnUnreadableOneIsNone()
    {
        Assert.Equal(["tdd", "prove-it-works"], TaskSkillSelection.Parse(TaskSkillSelection.Serialize(["tdd", "prove-it-works"])));
        Assert.Empty(TaskSkillSelection.Parse("[]"));
        Assert.Empty(TaskSkillSelection.Parse(null));
        Assert.Empty(TaskSkillSelection.Parse("{ not json"));
    }

    // ---- Defaults on the Skills tab ----

    [Fact]
    public void ADefaultFor_MustNameATaskType()
    {
        var errors = Library(Skill("tdd", defaultFor: ["feature", "epic"])).Validate();

        Assert.Equal(["tdd: \"epic\" is not a task type; choose from bug, feature, refactor, chore."], errors);
    }

    [Fact]
    public void ATypesDefaults_MustFitInATask_CountingOnlyEnabledSkills()
    {
        Assert.Empty(Library(Skill("a", 7_000, "bug"), Skill("b", 7_000, "feature"), Skill("c", 7_000, "bug") with { Enabled = false }).Validate());

        var errors = Library(Skill("a", 7_000, "bug"), Skill("b", 7_000, "bug")).Validate();

        Assert.Equal(["The skills chosen by default for a bug task come to 14,000 characters; a task can take at most 12,000."], errors);
    }
}
