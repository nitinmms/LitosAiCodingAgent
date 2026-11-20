using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Settings;

namespace Litos.SoftwareFactory.Core.Tests.Settings;

/// <summary>The skills the factory ships (docs/software-factory/built-in-skills.md).</summary>
public class BuiltInSkillsTests
{
    [Fact]
    public void TheFactoryShips_TheDevelopmentAndTestabilitySkills_InNameOrder()
    {
        Assert.Equal(
            ["address-feedback", "grill-the-request", "review-rubric", "root-cause-debugging", "safe-refactor", "tdd", "testability-dotnet", "testability-node", "verify-before-submit"],
            BuiltInSkills.All.Select(s => s.Name));
        Assert.All(BuiltInSkills.All, s => Assert.True(s.Version >= 1));
    }

    [Fact]
    public void TheShippedLibrary_IsAValidLibrary_WithEveryTypesDefaultsWithinATask()
    {
        var library = new SkillSettings { Library = [.. BuiltInSkills.All.Select(s => s.ToLibrarySkill())] };

        Assert.Empty(library.Validate());
        foreach (var type in TaskTypes.All)
            Assert.True(TaskSkillSelection.Size(library.Library.Where(s => s.DefaultFor.Contains(type))) <= TaskSkillSelection.MaxChars, type);
    }

    [Theory]
    [InlineData("grill-the-request", new[] { "bug", "feature", "refactor" })]
    [InlineData("tdd", new[] { "bug", "feature" })]
    [InlineData("root-cause-debugging", new[] { "bug" })]
    [InlineData("verify-before-submit", new[] { "bug", "feature", "refactor", "chore" })]
    [InlineData("review-rubric", new[] { "bug", "feature", "refactor", "chore" })]
    [InlineData("address-feedback", new[] { "bug", "feature", "refactor", "chore" })]
    [InlineData("safe-refactor", new[] { "refactor" })]
    public void EachSkill_IsChosenByDefault_ForTheTaskTypesItServes(string name, string[] types) =>
        Assert.Equal(types, BuiltInSkills.Find(name)!.DefaultFor);

    [Fact]
    public void EverySkill_SaysTheTurnsItAppliesTo_AndThatTheBriefOwnsTheProcess()
    {
        foreach (var skill in BuiltInSkills.All)
        {
            Assert.StartsWith("Applies ", skill.Body);
            var opening = skill.Body.Split("\n\n")[0];
            Assert.True(opening.Contains("brief comes first", StringComparison.Ordinal) || opening.Contains("brief sets the process", StringComparison.Ordinal), skill.Name);
        }
    }

    [Fact]
    public void AShippedSkill_StartsOn_AndStampedAsItsVersion()
    {
        var tdd = BuiltInSkills.Find("tdd")!;
        var skill = tdd.ToLibrarySkill();

        Assert.True(skill.Enabled);
        Assert.Equal(("tdd", tdd.Version), (skill.BuiltIn, skill.BuiltInVersion));
        Assert.Equal(BuiltInState.AsShipped, BuiltInSkills.StateOf(skill));
        Assert.True(tdd.Matches(skill));
    }

    [Fact]
    public void ASkillIsModified_ByItsDescriptionInstructionsOrDefaults_NotByItsNameOrBeingOff()
    {
        var skill = BuiltInSkills.Find("tdd")!.ToLibrarySkill();

        Assert.Equal(BuiltInState.AsShipped, BuiltInSkills.StateOf(skill with { Name = "test-first", Enabled = false }));
        Assert.Equal(BuiltInState.AsShipped, BuiltInSkills.StateOf(skill with { Body = skill.Body.Replace("\n", "\r\n") + "\n" }));
        Assert.Equal(BuiltInState.Modified, BuiltInSkills.StateOf(skill with { Body = skill.Body + " Always." }));
        Assert.Equal(BuiltInState.Modified, BuiltInSkills.StateOf(skill with { Description = "Use always." }));
        Assert.Equal(BuiltInState.Modified, BuiltInSkills.StateOf(skill with { DefaultFor = ["bug"] }));
        Assert.Equal(BuiltInState.NotBuiltIn, BuiltInSkills.StateOf(new LibrarySkill { Name = "mine", Description = "d" }));
    }

    [Fact]
    public void TheHash_IgnoresTheOrderOfTheDefaults()
    {
        Assert.Equal(BuiltInSkills.ContentHash("d", "b", ["bug", "feature"]), BuiltInSkills.ContentHash("d", "b", ["feature", "bug"]));
        Assert.NotEqual(BuiltInSkills.ContentHash("d", "b", ["bug"]), BuiltInSkills.ContentHash("d", "b", []));
    }

    [Fact]
    public void Parse_ReadsTheFrontmatter_AndKeepsOnlyKnownTaskTypes_InTheirOrder()
    {
        var skill = BuiltInSkills.Parse("---\nname: x\nversion: 3\ndescription: Use when x.\ndefaultFor: feature, nonsense, bug\n---\n\n## Do x\nStep.\n");

        Assert.Equal(("x", 3, "Use when x.", "## Do x\nStep."), (skill.Name, skill.Version, skill.Description, skill.Body));
        Assert.Equal(["bug", "feature"], skill.DefaultFor);
    }

    [Theory]
    [InlineData("name: x\n", "frontmatter")]
    [InlineData("---\nname: x\nversion: 1\ndescription: d\n", "closing")]
    [InlineData("---\nname: x\ndescription: d\n---\nbody", "version")]
    [InlineData("---\nname: x\nversion: zero\ndescription: d\n---\nbody", "whole number")]
    [InlineData("---\nversion: 1\ndescription: d\n---\nbody", "\"name\"")]
    [InlineData("---\nname x\n---\nbody", "key: value")]
    public void Parse_RefusesAFileThatIsNotABuiltInSkill(string text, string why) =>
        Assert.Contains(why, Assert.Throws<FormatException>(() => BuiltInSkills.Parse(text)).Message);

    [Fact]
    public void ALibraryNamingAnUnknownBuiltIn_OrOneBuiltInTwice_IsRefused()
    {
        var tdd = BuiltInSkills.Find("tdd")!.ToLibrarySkill();

        Assert.Contains(new SkillSettings { Library = [tdd with { BuiltIn = "nonsense" }] }.Validate(), e => e.Contains("not one of the factory's built-in skills"));
        Assert.Contains(new SkillSettings { Library = [tdd, tdd with { Name = "tdd-2" }] }.Validate(), e => e.Contains("in the library twice"));
    }
}

public class TestabilitySkillsTests
{
    [Theory]
    [InlineData("testability-dotnet", "UseStaticWebAssets")]
    [InlineData("testability-node", "process.env.PORT")]
    [InlineData("testability-dotnet", "<Compile Remove=\"Tests/**\" />")]
    [InlineData("testability-dotnet", "[Collection(\"Database\")]")]
    [InlineData("testability-dotnet", "Seed domain data too")]
    [InlineData("testability-node", "test-results/junit.xml")]
    [InlineData("testability-node", "Seed domain data too")]
    public void The_retrofit_skills_start_every_Make_testable_task_and_carry_what_the_real_apps_taught(string name, string fact)
    {
        var skill = BuiltInSkills.All.Single(s => s.Name == name);

        Assert.Equal([Litos.SoftwareFactory.Core.Lifecycle.TaskTypes.Testability], skill.DefaultFor);
        Assert.Contains(fact, skill.Body);
        Assert.Contains("The brief comes first", skill.Body);
        Assert.InRange(skill.Body.Length, 500, 3_000);
    }

    [Fact]
    public void A_Make_testable_tasks_default_skills_fit_its_limit() =>
        Assert.True(BuiltInSkills.All.Where(s => s.DefaultFor.Contains(Litos.SoftwareFactory.Core.Lifecycle.TaskTypes.Testability)).Sum(s => s.Body.Length) < TaskSkillSelection.MaxChars);
}
