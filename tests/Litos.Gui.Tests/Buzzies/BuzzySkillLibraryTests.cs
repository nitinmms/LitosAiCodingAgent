using Litos.Gui.Buzzies;
using Litos.Gui.Buzzies.Workflows;

namespace Litos.Gui.Tests.Buzzies;

/// <summary>
/// Editing a Buzzy's skills inside the app (Implement_Buzzies.md, Phase 3). The window is thin; what
/// is worth pinning is that it reads and writes the same SKILL.md the skill tool reads, that it
/// can't produce frontmatter it wouldn't parse back, and that editing does not disturb the saved
/// versions a routine may be pinned to.
/// </summary>
public class BuzzySkillLibraryTests : IDisposable
{
    private readonly TempDir _temp = new();
    private readonly BuzzyStore _store;
    private readonly BuzzySkillLibrary _skills;
    private readonly string _buzzyId;

    public BuzzySkillLibraryTests()
    {
        _store = new BuzzyStore(_temp.Path);
        _skills = new BuzzySkillLibrary(_store);
        _buzzyId = _store.Create(BuzzyTemplates.Scout, "Scout", "#2FB457", DateTimeOffset.UnixEpoch).Id;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _temp.Dispose();
    }

    [Fact]
    public void SaveThenRead_RoundTripsNameDescriptionAndBody()
    {
        _skills.Save(_buzzyId, new BuzzySkillFile("weekly-report", "Weekly report", "Summarise the week", "# Steps\n\n1. Read folders."));

        var read = _skills.Read(_buzzyId, "weekly-report");

        Assert.NotNull(read);
        Assert.Equal("Weekly report", read.Name);
        Assert.Equal("Summarise the week", read.Description);
        Assert.Contains("Read folders", read.Body);
    }

    [Fact]
    public void SavedSkill_IsFoundByTheRealSkillDiscovery()
    {
        // The point of writing the ordinary file: the skill tool finds it with no special casing.
        _skills.Save(_buzzyId, new BuzzySkillFile("weekly-report", "Weekly report", "Summarise the week", "Do the thing."));

        var discovered = Litos.Tools.Skills.SkillDiscovery.ScanRoot(_skills.RootOf(_buzzyId)).ToArray();

        var skill = Assert.Single(discovered);
        Assert.Equal("Weekly report", skill.Name);
        Assert.Equal("Summarise the week", skill.Description);
    }

    [Fact]
    public void Description_WithNewlinesOrDashes_CannotBreakTheFrontmatter()
    {
        // Written straight into "description: …", so a newline or "---" in it would corrupt the file
        // and make it unparseable — while still looking fine to whoever typed it.
        _skills.Save(_buzzyId, new BuzzySkillFile("tidy", "Tidy", "first line\nsecond line\n---\nname: hijacked", "Body."));

        var read = _skills.Read(_buzzyId, "tidy");

        Assert.NotNull(read);
        Assert.Equal("first line second line — name: hijacked", read.Description);
        Assert.Equal("Tidy", read.Name);
        Assert.Equal("Body.", read.Body);
    }

    [Fact]
    public void EditingAWorkflow_LeavesItsSavedVersionsAlone()
    {
        // A routine can be pinned to an approved version. Editing the skill is not a re-approval, so
        // the versions must stay exactly as they were.
        var workflows = new WorkflowStore(_store);
        var saved = workflows.Save(_buzzyId, "weekly-report", "Summarise the week", "Approved body.", DateTimeOffset.UnixEpoch);

        _skills.Save(_buzzyId, new BuzzySkillFile("weekly-report", "Weekly report", "Edited description", "Edited body."));

        var version = workflows.Read(_buzzyId, "weekly-report", saved.Version);
        Assert.NotNull(version);
        Assert.Equal("Approved body.", version.Body);
        Assert.Equal(1, workflows.LatestVersion(_buzzyId, "weekly-report"));

        // And the skill tool now sees the edited text, because SKILL.md is the newest version.
        Assert.Equal("Edited body.", _skills.Read(_buzzyId, "weekly-report")!.Body);
    }

    [Theory]
    [InlineData("Weekly report", "weekly-report")]
    [InlineData("Weekly AI model report!", "weekly-ai-model-report")]
    public void FolderFor_ProducesASafeFolder(string name, string expected)
    {
        var folder = BuzzySkillLibrary.FolderFor(name);

        Assert.Equal(expected, folder);
        Assert.True(BuzzySkillLibrary.IsSafeFolder(folder));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../escape")]
    [InlineData("Has Spaces")]
    [InlineData("UPPER")]
    [InlineData("trailing-")]
    public void IsSafeFolder_RefusesAnythingOutsideItsShape(string folder) =>
        Assert.False(BuzzySkillLibrary.IsSafeFolder(folder));

    [Theory]
    [InlineData("../escape")]
    [InlineData("nested/path")]
    public void Read_OutsideTheSkillsFolder_ReturnsNullRatherThanReadingIt(string folder) =>
        Assert.Null(_skills.Read(_buzzyId, folder));

    [Fact]
    public void Save_RefusesAnEmptyBody()
    {
        var threw = Assert.Throws<ArgumentException>(() =>
            _skills.Save(_buzzyId, new BuzzySkillFile("empty", "Empty", "desc", "   ")));

        Assert.Contains("what to do", threw.Message);
    }

    [Fact]
    public void ANewBuzzy_AlreadyHasASoulAndMemoryFile()
    {
        // BuzzyEditorWindow refuses to save until it has loaded these (an unreadable file must never
        // be overwritten by an empty box), which is only safe because Create always writes both.
        Assert.True(File.Exists(_store.SoulPathOf(_buzzyId)));
        Assert.True(File.Exists(_store.MemoryPathOf(_buzzyId)));
        Assert.Contains("Scout", File.ReadAllText(_store.SoulPathOf(_buzzyId)));
    }

    [Fact]
    public void List_IncludesSkillsWrittenOnDisk_AndSkipsFoldersWithoutSkillMd()
    {
        _skills.Save(_buzzyId, new BuzzySkillFile("from-app", "From app", "d", "b"));
        var handMade = Path.Combine(_skills.RootOf(_buzzyId), "by-hand");
        Directory.CreateDirectory(handMade);
        File.WriteAllText(Path.Combine(handMade, "SKILL.md"), "---\nname: By hand\ndescription: written on disk\n---\n\nbody\n");
        Directory.CreateDirectory(Path.Combine(_skills.RootOf(_buzzyId), "not-a-skill"));

        var listed = _skills.List(_buzzyId);

        Assert.Equal(["by-hand", "from-app"], listed.Select(s => s.Folder));
    }
}