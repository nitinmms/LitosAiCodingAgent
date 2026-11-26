using Litos.Gui.Buzzies;

namespace Litos.Gui.Tests.Buzzies;

public class BuzzyStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_WritesProfileSoulMemoryAndWorkspace()
    {
        using var temp = new TempDir();
        var store = new BuzzyStore(temp.Path);

        var profile = store.Create(BuzzyTemplates.Scout, "Scout", "#2FB457", Now);

        Assert.Equal("scout", profile.Id);
        Assert.True(Directory.Exists(store.WorkspaceOf("scout")));
        Assert.Contains("careful researcher", File.ReadAllText(store.SoulPathOf("scout")));
        Assert.StartsWith("# Memory", File.ReadAllText(store.MemoryPathOf("scout")));
        Assert.Equal(BuzzyTemplates.Scout.Tools, store.Get("scout")!.Tools);
    }

    [Fact]
    public void Create_SameNameTwice_GetsDistinctIds()
    {
        using var temp = new TempDir();
        var store = new BuzzyStore(temp.Path);

        var first = store.Create(BuzzyTemplates.Buddy, "Alex", "#FFC21A", Now);
        var second = store.Create(BuzzyTemplates.Buddy, "Alex", "#FF5CA8", Now.AddMinutes(1));

        Assert.Equal("alex", first.Id);
        Assert.Equal("alex-2", second.Id);
        Assert.Equal(["alex", "alex-2"], store.List().Select(p => p.Id));
    }

    [Fact]
    public void Save_PersistsLastSessionId_AcrossStoreInstances()
    {
        using var temp = new TempDir();
        var profile = new BuzzyStore(temp.Path).Create(BuzzyTemplates.Builder, "Builder", "#9B4DE0", Now);

        new BuzzyStore(temp.Path).Save(profile with { LastSessionId = "abc123" });

        Assert.Equal("abc123", new BuzzyStore(temp.Path).Get("builder")!.LastSessionId);
    }

    [Fact]
    public void List_SkipsDamagedProfiles_AndProfilesWhoseIdDoesNotMatchTheirFolder()
    {
        using var temp = new TempDir();
        var store = new BuzzyStore(temp.Path);
        store.Create(BuzzyTemplates.Scout, "Scout", "#2FB457", Now);

        Directory.CreateDirectory(Path.Combine(temp.Path, "broken"));
        File.WriteAllText(Path.Combine(temp.Path, "broken", BuzzyStore.ProfileFileName), "{ not json");

        // A copied buzzy.json claiming another Buzzy's id must not be loaded as that Buzzy.
        Directory.CreateDirectory(Path.Combine(temp.Path, "impostor"));
        File.Copy(Path.Combine(temp.Path, "scout", BuzzyStore.ProfileFileName), Path.Combine(temp.Path, "impostor", BuzzyStore.ProfileFileName));

        Assert.Equal(["scout"], store.List().Select(p => p.Id));
        Assert.Null(store.Get("impostor"));
    }

    [Fact]
    public void Delete_RemovesTheFolder()
    {
        using var temp = new TempDir();
        var store = new BuzzyStore(temp.Path);
        store.Create(BuzzyTemplates.Scout, "Scout", "#2FB457", Now);

        store.Delete("scout");

        Assert.False(Directory.Exists(store.FolderOf("scout")));
        Assert.Empty(store.List());
    }

    [Fact]
    public void Delete_IgnoresIdsThatAreNotPlainSlugs()
    {
        using var temp = new TempDir();
        var outside = Directory.CreateDirectory(Path.Combine(temp.Path, "keep")).FullName;
        var store = new BuzzyStore(Path.Combine(temp.Path, "buzzies"));

        store.Delete("../keep");

        Assert.True(Directory.Exists(outside));
    }

    [Fact]
    public void Delete_RemovesAJunctionInTheWorkspace_WithoutTouchingItsTarget()
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var temp = new TempDir();
        var store = new BuzzyStore(Path.Combine(temp.Path, "buzzies"));
        store.Create(BuzzyTemplates.Scout, "Scout", "#2FB457", Now);
        var outside = Directory.CreateDirectory(Path.Combine(temp.Path, "outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "keep");
        var link = Path.Combine(store.WorkspaceOf("scout"), "door");
        using (var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{outside}\"")
               { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!)
            mklink.WaitForExit();
        if (!Directory.Exists(link))
            return;

        store.Delete("scout");

        Assert.False(Directory.Exists(store.FolderOf("scout")));
        Assert.True(File.Exists(Path.Combine(outside, "keep.txt")));
    }

    [Theory]
    [InlineData("Scout", "scout")]
    [InlineData("  My Research Bee! ", "my-research-bee")]
    [InlineData("Ünïcødé", "n-c-d")]
    [InlineData("!!!", "buzzy")]
    [InlineData("../../etc", "etc")]
    public void Slugify_ProducesSafeIds(string name, string expected) =>
        Assert.Equal(expected, BuzzyStore.Slugify(name));

    [Fact]
    public void Owner_IsPerBuzzy_AndNotTheLocalOwner()
    {
        var profile = new BuzzyProfile("scout", "Scout", "scout", "#2FB457", [], Now);

        Assert.Equal("buzzy-scout", profile.Owner.Value);
        Assert.NotEqual(Litos.Agent.Session.SessionOwner.Local, profile.Owner);
    }
}
