using Litos.SoftwareFactory.Core.Settings;

namespace Litos.SoftwareFactory.Core.Tests.Settings;

/// <summary>How the built-in skills reach a library and stay current (docs/software-factory/built-in-skills.md).</summary>
public class BuiltInSkillSyncTests
{
    private static readonly BuiltInSkill Alpha = new("alpha", 1, "Use for alpha.", "Applies always. The brief comes first.\n\nDo alpha.", ["feature"]);
    private static readonly BuiltInSkill Beta = new("beta", 1, "Use for beta.", "Applies always. The brief comes first.\n\nDo beta.", ["bug", "feature"]);

    private static LibrarySkill Own(string name, string body = "Mine.") => new() { Name = name, Description = "My own.", Body = body };

    [Fact]
    public void ANewFactory_GetsEveryBuiltIn_OnAndAsShipped_AndRemembersItOfferedThem()
    {
        var result = BuiltInSkillSync.OnStart(new SkillSettings(), BuiltInSkills.All);

        Assert.True(result.Changed);
        Assert.Equal(BuiltInSkills.All.Select(s => s.Name), result.Settings.Library.Select(s => s.Name));
        Assert.All(result.Settings.Library, s => Assert.Equal(BuiltInState.AsShipped, BuiltInSkills.StateOf(s)));
        Assert.All(result.Settings.Library, s => Assert.True(s.Enabled));
        Assert.Equal(BuiltInSkills.All.Select(s => s.Name), result.Settings.Offered);
        Assert.Empty(result.Settings.Validate());
    }

    [Fact]
    public void TheNextStart_ChangesNothing()
    {
        var first = BuiltInSkillSync.OnStart(new SkillSettings(), BuiltInSkills.All).Settings;

        var second = BuiltInSkillSync.OnStart(first, BuiltInSkills.All);

        Assert.False(second.Changed);
        Assert.Empty(second.Notes);
        Assert.Equal(first.Library, second.Settings.Library);
    }

    [Fact]
    public void ABuiltInAnAdminRemoved_StaysRemoved()
    {
        var seeded = BuiltInSkillSync.OnStart(new SkillSettings(), [Alpha, Beta]).Settings;
        var saved = BuiltInSkillSync.OnSave(seeded with { Library = [.. seeded.Library.Where(s => s.Name != "alpha")] }, seeded, [Alpha, Beta]);

        var restarted = BuiltInSkillSync.OnStart(saved, [Alpha, Beta]);

        Assert.False(restarted.Changed);
        Assert.Equal(["beta"], restarted.Settings.Library.Select(s => s.Name));
        Assert.Equal(["alpha", "beta"], saved.Offered);
    }

    [Fact]
    public void ABuiltInShippedLater_IsAddedOnce_AfterTheAdminsSkills()
    {
        var before = BuiltInSkillSync.OnStart(new SkillSettings { Library = [Own("mine")] }, [Alpha]).Settings;

        var after = BuiltInSkillSync.OnStart(before, [Alpha, Beta]);

        Assert.True(after.Changed);
        Assert.Equal(["mine", "alpha", "beta"], after.Settings.Library.Select(s => s.Name));
        Assert.Contains("Built-in skill \"beta\" added.", after.Notes);
    }

    [Fact]
    public void AnAdminsOwnSkillOfTheSameName_IsLeftAlone_AndTheBuiltInIsNotAdded()
    {
        var mine = Own("Alpha", "My alpha.");

        var result = BuiltInSkillSync.OnStart(new SkillSettings { Library = [mine] }, [Alpha]);

        Assert.Equal([mine], result.Settings.Library);
        Assert.Equal(["alpha"], result.Settings.Offered);
        Assert.Contains(result.Notes, n => n.Contains("has its own skill of that name"));
    }

    [Fact]
    public void ASkillAddedByHand_WithTheBuiltInsTextExactly_IsTakenAsThatBuiltIn()
    {
        var byHand = new LibrarySkill { Name = "alpha", Description = Alpha.Description, Body = Alpha.Body + "\n", DefaultFor = ["feature"], Enabled = false };

        var result = BuiltInSkillSync.OnStart(new SkillSettings { Library = [byHand] }, [Alpha]);

        var adopted = Assert.Single(result.Settings.Library);
        Assert.Equal(("alpha", 1, false), (adopted.BuiltIn, adopted.BuiltInVersion, adopted.Enabled));
        Assert.Equal(BuiltInState.AsShipped, BuiltInSkills.StateOf(adopted));
        Assert.Contains(result.Notes, n => n.Contains("marked as it"));
    }

    [Fact]
    public void AnUneditedBuiltIn_MovesToANewerShippedVersion_KeepingItsNameAndSwitch()
    {
        var v1 = BuiltInSkillSync.OnStart(new SkillSettings(), [Alpha]).Settings;
        var renamedAndOff = v1 with { Library = [v1.Library[0] with { Name = "alpha-renamed", Enabled = false }] };
        var v2 = Alpha with { Version = 2, Body = Alpha.Body + "\nAnd check it." };

        var result = BuiltInSkillSync.OnStart(renamedAndOff, [v2]);

        var skill = Assert.Single(result.Settings.Library);
        Assert.Equal(("alpha-renamed", false, 2, v2.Body), (skill.Name, skill.Enabled, skill.BuiltInVersion, skill.Body));
        Assert.Equal(BuiltInState.AsShipped, BuiltInSkills.StateOf(skill));
        Assert.Contains("Built-in skill \"alpha-renamed\" updated to version 2.", result.Notes);
    }

    [Fact]
    public void AnEditedBuiltIn_KeepsTheAdminsText_WhenANewerVersionShips()
    {
        var v1 = BuiltInSkillSync.OnStart(new SkillSettings(), [Alpha]).Settings;
        var edited = BuiltInSkillSync.OnSave(v1 with { Library = [v1.Library[0] with { Body = "Our own way." }] }, v1, [Alpha]);

        var result = BuiltInSkillSync.OnStart(edited, [Alpha with { Version = 2, Body = "New shipped text." }]);

        var skill = Assert.Single(result.Settings.Library);
        Assert.Equal(("Our own way.", 1), (skill.Body, skill.BuiltInVersion));
        Assert.Equal(BuiltInState.Modified, BuiltInSkills.StateOf(skill));
        Assert.False(result.Changed);
    }

    [Fact]
    public void ABuiltInsDefaults_ThatWouldPassATasksLimit_AreLeftOff_AndTheLibraryStaysValid()
    {
        var big = Own("big", new string('x', TaskSkillSelection.MaxChars - 10)) with { DefaultFor = ["feature"] };

        var result = BuiltInSkillSync.OnStart(new SkillSettings { Library = [big] }, [Beta]);

        var beta = result.Settings.Library.Single(s => s.Name == "beta");
        Assert.Equal(["bug"], beta.DefaultFor);
        Assert.Equal(BuiltInState.Modified, BuiltInSkills.StateOf(beta));
        Assert.DoesNotContain(result.Settings.Validate(), e => e.Contains("characters")); // "beta" is not a real built-in, so only the size rule is checked
        Assert.Contains(result.Notes, n => n.Contains("without its defaults for feature"));
    }

    [Fact]
    public void OnSave_AnEditedBuiltIn_IsModified_AtTheVersionItWas()
    {
        var stored = BuiltInSkillSync.OnStart(new SkillSettings(), [Alpha]).Settings;

        var saved = BuiltInSkillSync.OnSave(stored with { Library = [stored.Library[0] with { Body = "Edited." }] }, stored, [Alpha]);

        Assert.Equal(BuiltInState.Modified, BuiltInSkills.StateOf(saved.Library[0]));
        Assert.Equal(1, saved.Library[0].BuiltInVersion);
    }

    [Fact]
    public void OnSave_ResettingToTheShippedText_StampsItAsTheCurrentVersion()
    {
        var old = new SkillSettings { Library = [Alpha.ToLibrarySkill() with { Body = "Edited." }], Offered = ["alpha"] };
        var v3 = Alpha with { Version = 3, Body = "Current shipped." };

        var saved = BuiltInSkillSync.OnSave(old with { Library = [v3.Apply(old.Library[0])] }, old, [v3]);

        Assert.Equal((3, BuiltInState.AsShipped), (saved.Library[0].BuiltInVersion, BuiltInSkills.StateOf(saved.Library[0])));
    }

    [Fact]
    public void OnSave_TheVersionHashAndOfferedList_ComeFromTheHost_NotTheRequest()
    {
        var stored = BuiltInSkillSync.OnStart(new SkillSettings(), [Alpha]).Settings;
        var forged = stored with
        {
            Library = [stored.Library[0] with { Body = "Edited.", BuiltInVersion = 99, BuiltInHash = BuiltInSkills.ContentHash(Alpha.Description, "Edited.", Alpha.DefaultFor) }],
            Offered = [],
        };

        var saved = BuiltInSkillSync.OnSave(forged, stored, [Alpha]);

        Assert.Equal((1, BuiltInState.Modified), (saved.Library[0].BuiltInVersion, BuiltInSkills.StateOf(saved.Library[0])));
        Assert.Equal(["alpha"], saved.Offered);
    }

    [Fact]
    public void OnSave_AnAdminsOwnSkill_CarriesNoBuiltInVersionOrHash()
    {
        var saved = BuiltInSkillSync.OnSave(
            new SkillSettings { Library = [Own("mine") with { BuiltInVersion = 4, BuiltInHash = "sha256:x" }] }, new SkillSettings(), [Alpha]);

        Assert.Equal((null, null, null), (saved.Library[0].BuiltIn, saved.Library[0].BuiltInVersion, saved.Library[0].BuiltInHash));
    }

    [Fact]
    public void OnSave_ABuiltInAddedBack_IsAsShipped_OrModifiedIfEditedFirst()
    {
        var stored = new SkillSettings { Offered = ["alpha", "beta"] };

        var saved = BuiltInSkillSync.OnSave(
            stored with { Library = [Alpha.ToLibrarySkill() with { BuiltInHash = null, BuiltInVersion = null }, Beta.ToLibrarySkill() with { Body = "Edited first." }] },
            stored, [Alpha, Beta]);

        Assert.Equal(BuiltInState.AsShipped, BuiltInSkills.StateOf(saved.Library[0]));
        Assert.Equal((BuiltInState.Modified, 1), (BuiltInSkills.StateOf(saved.Library[1]), saved.Library[1].BuiltInVersion));
    }
}
