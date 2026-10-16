using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Settings;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Host.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace Litos.SoftwareFactory.Host.Tests;

/// <summary>Skills in the factory (m3-architecture.md §7.3): the tab, repository approvals, and what a run is given.</summary>
public sealed class SkillsTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private FactorySettings _settings = null!;

    public async Task InitializeAsync() => await StartAsync(startCoordinator: false);

    private async Task StartAsync(bool startCoordinator)
    {
        _host = await TestHost.StartAsync(startCoordinator: startCoordinator);
        _settings = _host.App.Services.GetRequiredService<FactorySettings>();
    }

    private async Task RestartWithCoordinatorAsync()
    {
        await _host.DisposeAsync();
        await StartAsync(startCoordinator: true);
    }

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    private static LibrarySkill Notes(string body = "Group the changes by area.") =>
        new() { Name = "release-notes", Description = "Write release notes from the merged changes.", Body = body };

    private async Task SaveSkillsAsync(SkillSettings skills) =>
        await _settings.SaveAsync(SettingsSections.Skills, skills, _settings.RevisionOf(SettingsSections.Skills), null, default);

    private async Task<HttpResponseMessage> PutSkillsAsync(object settings, HttpClient? client = null) =>
        await (client ?? _host.Client).PutAsJsonAsync("api/admin/settings/skills", new { revision = _settings.RevisionOf(SettingsSections.Skills), settings });

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    /// <summary>Puts a skill in a project's working copy, as its repository would have it.</summary>
    private static string WriteRepositorySkill(string workingCopy, string name, string instructions = "Run deploy.ps1.")
    {
        var folder = Path.Combine(RepositorySkillFolder.In(workingCopy), name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "SKILL.md"), $"---\nname: {name}\ndescription: How to {name}.\n---\n{instructions}\n");
        File.WriteAllText(Path.Combine(folder, "deploy.ps1"), "echo staging");
        return folder;
    }

    // ---- The tab ----

    [Fact]
    public async Task TheSection_StartsWithNoLibrary_AndRepositorySkillsWaitingForApproval()
    {
        var skills = (await _host.GetAsync("api/admin/settings")).GetProperty("skills");

        Assert.Equal(0, skills.GetProperty("revision").GetInt64());
        Assert.Empty(skills.GetProperty("settings").GetProperty("library").EnumerateArray());
        Assert.Equal("ApproveEach", skills.GetProperty("settings").GetProperty("repositoryPolicy").GetString());
        // Only what the tab edits: a field it does not know reads to it as an unsaved change.
        Assert.Equal(["repositoryPolicy", "library"], skills.GetProperty("settings").EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task AnAdmin_SavesTheLibraryAndPolicy_AsWords_AndTheHostRefusesASkillThatCannotWork()
    {
        using var refused = await PutSkillsAsync(new { library = new[] { new { name = "notes", description = "two\nlines", body = "x" } } });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("single line", (await BodyAsync(refused)).GetProperty("error").GetString());

        using var saved = await PutSkillsAsync(new { repositoryPolicy = "Always", library = new[] { Notes() } });

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal((RepositorySkillPolicy.Always, "release-notes"), (_settings.Skills.RepositoryPolicy, Assert.Single(_settings.Skills.Library).Name));
        var shown = (await _host.GetAsync("api/admin/settings")).GetProperty("skills").GetProperty("settings");
        Assert.Equal("Always", shown.GetProperty("repositoryPolicy").GetString());
    }

    [Fact]
    public async Task ASaveFromAStaleRevision_IsAConflict()
    {
        await SaveSkillsAsync(new SkillSettings { Library = [Notes()] });

        using var stale = await _host.Client.PutAsJsonAsync("api/admin/settings/skills", new { revision = 0, settings = new SkillSettings() });

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Single(_settings.Skills.Library);
    }

    [Fact]
    public async Task OnlyAnAdmin_ChangesSkills_OrSeesAndApprovesRepositorySkills()
    {
        await _host.CreateMemberAsync("maria");
        var maria = await _host.SignedInAsync("maria");

        using var save = await PutSkillsAsync(new SkillSettings { Library = [Notes()] }, maria);
        using var list = await maria.GetAsync("api/admin/skills/repository");
        using var approve = await maria.PutAsJsonAsync("api/admin/skills/repository/approval", new { projectId = Guid.NewGuid(), name = "x", hash = "h", approved = true });

        Assert.Equal((HttpStatusCode.Forbidden, HttpStatusCode.Forbidden, HttpStatusCode.Forbidden), (save.StatusCode, list.StatusCode, approve.StatusCode));
        Assert.Empty(_settings.Skills.Library);
    }

    [Fact]
    public async Task ApprovingASkillNeverFound_OrWithoutItsName_IsRefused()
    {
        using var missing = await _host.Client.PutAsJsonAsync("api/admin/skills/repository/approval",
            new { projectId = Guid.NewGuid(), name = "deploy", hash = "sha256:x", approved = true });
        using var unnamed = await _host.Client.PutAsJsonAsync("api/admin/skills/repository/approval",
            new { projectId = Guid.NewGuid(), hash = "sha256:x", approved = true });

        Assert.Equal((HttpStatusCode.NotFound, HttpStatusCode.BadRequest), (missing.StatusCode, unnamed.StatusCode));
    }

    // ---- The library on disk ----

    [Fact]
    public async Task ALibraryRevision_IsWrittenOnce_WithTheFrontmatterTheEngineReads_AndOnlyItsEnabledSkills()
    {
        await SaveSkillsAsync(new SkillSettings { Library = [Notes(), Notes() with { Name = "off", Enabled = false }] });
        var skills = _host.App.Services.GetRequiredService<FactorySkills>();

        var root = skills.LibraryRoot(1)!;

        Assert.Equal(Path.Combine(_host.Options.DataDirectory, "skills", "1"), root);
        var found = Assert.Single(Litos.Tools.Skills.SkillDiscovery.ScanRoot(root));
        Assert.Equal(("release-notes", "Write release notes from the merged changes."), (found.Name, found.Description));
        Assert.EndsWith("Group the changes by area.\n", File.ReadAllText(Path.Combine(found.DirectoryPath, "SKILL.md")));

        // Written once: a later change makes a new revision, never a change to this folder.
        await SaveSkillsAsync(new SkillSettings { Library = [Notes("Changed.")] });
        Assert.Equal(root, skills.LibraryRoot(1));
        Assert.EndsWith("Group the changes by area.\n", File.ReadAllText(Path.Combine(found.DirectoryPath, "SKILL.md")));
        Assert.Equal(Path.Combine(_host.Options.DataDirectory, "skills", "2"), skills.LibraryRoot(2));
    }

    [Fact]
    public async Task ALibraryWithNothingEnabled_HasNoFolder()
    {
        await SaveSkillsAsync(new SkillSettings { Library = [Notes() with { Enabled = false }] });

        Assert.Null(_host.App.Services.GetRequiredService<FactorySkills>().LibraryRoot(1));
    }

    [Fact]
    public void ARepositorysSkills_AreFoundWithTheirHashFolderAndOtherFiles_AndAFolderWithoutASkillIsNot()
    {
        var workingCopy = Directory.CreateTempSubdirectory("litos-skills-repo-").FullName;
        try
        {
            var folder = WriteRepositorySkill(workingCopy, "deploy");
            Directory.CreateDirectory(Path.Combine(RepositorySkillFolder.In(workingCopy), "not-a-skill"));

            var found = Assert.Single(FactorySkills.Find(workingCopy));

            Assert.Equal(("deploy", "How to deploy.", ".litos/skills/deploy", SkillHash.Of(folder)), (found.Name, found.Description, found.Folder, found.Hash));
            Assert.Contains("Run deploy.ps1.", found.Content);
            Assert.Equal("""["deploy.ps1"]""", found.OtherFilesJson);
            Assert.Empty(FactorySkills.Find(Path.Combine(workingCopy, "nowhere")));
        }
        finally
        {
            Directory.Delete(workingCopy, recursive: true);
        }
    }

    // ---- What a run is given ----

    private async Task<FakeWorker> WaitForWorkerAsync(int count = 1)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (_host.Workers.Workers.Count < count)
        {
            Assert.True(DateTime.UtcNow < deadline, "No worker was launched.");
            await Task.Delay(25);
        }

        return _host.Workers.Workers.ElementAt(count - 1);
    }

    private async Task<RunCapabilities> CapabilitiesAsync(Guid threadId)
    {
        var json = (await _host.ThreadAsync(threadId)).LatestRun!.CapabilitiesJson!;
        return JsonSerializer.Deserialize<RunCapabilities>(json, FactoryWire.Json)!;
    }

    private static RunSkills SkillsOf(FakeWorker worker) =>
        JsonSerializer.Deserialize<RunSkills>(File.ReadAllText(worker.Launch.SkillsPath!), FactoryWire.Json)!;

    private async Task<string[]> NotesAsync(Guid threadId) =>
        [.. (await _host.ThreadAsync(threadId)).Messages.Where(m => m.Kind == MessageKind.Status).Select(m => m.Text)];

    /// <summary>A project whose working copy has a "deploy" skill, and a thread on it delegated and handed off.</summary>
    private async Task<(Guid ProjectId, Guid ThreadId, string Folder)> HandedOffWithARepositorySkillAsync()
    {
        var projectId = await _host.RegisterProjectAsync();
        var folder = WriteRepositorySkill(_host.Workspaces.Of(projectId).Path, "deploy");
        var threadId = await _host.CreateThreadAsync(projectId);
        await _host.DelegateAsync(threadId);
        await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);
        return (projectId, threadId, folder);
    }

    [Fact]
    public async Task ARunWithNoSkillAnywhere_IsGivenNoSkillsFile()
    {
        await RestartWithCoordinatorAsync();
        var threadId = await _host.CreateThreadAsync(await _host.RegisterProjectAsync());

        await _host.DelegateAsync(threadId);
        await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        Assert.Null((await WaitForWorkerAsync()).Launch.SkillsPath);
        var capabilities = await CapabilitiesAsync(threadId);
        Assert.Equal((0L, 0), (capabilities.SkillLibraryRevision, capabilities.LibrarySkills.Count));
        Assert.Empty(capabilities.RepositorySkills!);
        Assert.DoesNotContain(await NotesAsync(threadId), n => n.Contains("skill", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ARun_IsGivenTheLibrary_AndRecordsWhichRevision()
    {
        await RestartWithCoordinatorAsync();
        await SaveSkillsAsync(new SkillSettings { Library = [Notes()] });
        var threadId = await _host.CreateThreadAsync(await _host.RegisterProjectAsync());

        await _host.DelegateAsync(threadId);
        await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        var skills = SkillsOf(await WaitForWorkerAsync());
        Assert.Equal(Path.Combine(_host.Options.DataDirectory, "skills", "1"), skills.LibraryRoot);
        Assert.Empty(skills.Repository);
        var capabilities = await CapabilitiesAsync(threadId);
        Assert.Equal(1, capabilities.SkillLibraryRevision);
        Assert.Equal(["release-notes"], capabilities.LibrarySkills.Select(s => s.Name));
        var shown = (await _host.GetAsync($"api/threads/{threadId}")).GetProperty("run").GetProperty("capabilities");
        Assert.Equal("release-notes", shown.GetProperty("librarySkills")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task AnUnapprovedRepositorySkill_IsSkipped_AndTheThreadSaysSo_ThenOnceApproved_ARework_UsesIt()
    {
        await RestartWithCoordinatorAsync();
        var (projectId, threadId, folder) = await HandedOffWithARepositorySkillAsync();

        Assert.Null((await WaitForWorkerAsync()).Launch.SkillsPath);
        Assert.Contains("Skipped repository skills waiting for an Admin's approval: deploy.", await NotesAsync(threadId));
        var use = Assert.Single((await CapabilitiesAsync(threadId)).RepositorySkills!);
        Assert.Equal((RepositorySkillStatus.WaitingForApproval, SkillHash.Of(folder)), (use.Status, use.Hash));

        // The Skills tab lists it with what the Admin reads, and the Admin approves it.
        var listed = (await _host.GetAsync("api/admin/skills/repository")).GetProperty("skills")[0];
        Assert.Equal(("salesapp", "deploy", ".litos/skills/deploy", true), (listed.GetProperty("project").GetString(),
            listed.GetProperty("name").GetString(), listed.GetProperty("folder").GetString(), listed.GetProperty("latest").GetBoolean()));
        Assert.Contains("Run deploy.ps1.", listed.GetProperty("content").GetString());
        Assert.Equal("deploy.ps1", listed.GetProperty("otherFiles")[0].GetString());
        Assert.Equal(JsonValueKind.Null, listed.GetProperty("approvedAt").ValueKind);
        using (var approve = await _host.Client.PutAsJsonAsync("api/admin/skills/repository/approval",
            new { projectId, name = "deploy", hash = SkillHash.Of(folder), approved = true }))
            Assert.Equal(HttpStatusCode.OK, approve.StatusCode);

        await _host.DelegateAsync(threadId, "@factory CSV values containing commas are wrong.");
        var rework = await WaitForWorkerAsync(2);
        await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        Assert.Equal([new AllowedRepositorySkill("deploy", SkillHash.Of(folder))], SkillsOf(rework).Repository);
        Assert.Equal(RepositorySkillStatus.Used, Assert.Single((await CapabilitiesAsync(threadId)).RepositorySkills!).Status);
        Assert.Single(await NotesAsync(threadId), n => n.Contains("Skipped repository skills"));
    }

    [Fact]
    public async Task ASkillChangedSinceItWasApproved_WaitsForItsOwnApproval()
    {
        await RestartWithCoordinatorAsync();
        var (projectId, threadId, folder) = await HandedOffWithARepositorySkillAsync();
        await _host.Store.SetRepositorySkillApprovalAsync(projectId, "deploy", SkillHash.Of(folder), true, await _host.AdminIdAsync(), DateTimeOffset.UtcNow, default);

        File.WriteAllText(Path.Combine(folder, "deploy.ps1"), "git push origin main");
        await _host.DelegateAsync(threadId, "@factory CSV values containing commas are wrong.");
        var rework = await WaitForWorkerAsync(2);
        await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        Assert.Null(rework.Launch.SkillsPath);
        Assert.Equal(RepositorySkillStatus.ChangedSinceApproved, Assert.Single((await CapabilitiesAsync(threadId)).RepositorySkills!).Status);
        Assert.Contains(await NotesAsync(threadId), n => n.Contains("deploy (changed since it was approved)"));
        var versions = (await _host.GetAsync("api/admin/skills/repository")).GetProperty("skills").EnumerateArray().ToList();
        Assert.Equal(2, versions.Count);
        Assert.Single(versions, v => v.GetProperty("latest").GetBoolean() && v.GetProperty("approvedAt").ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task UnderAlways_ARepositorySkillIsUsedUnapproved_ReplacingTheLibrarys_AndTheThreadSaysSo()
    {
        await RestartWithCoordinatorAsync();
        await SaveSkillsAsync(new SkillSettings { RepositoryPolicy = RepositorySkillPolicy.Always, Library = [Notes() with { Name = "deploy" }] });

        var (_, threadId, folder) = await HandedOffWithARepositorySkillAsync();

        var skills = SkillsOf(await WaitForWorkerAsync());
        Assert.NotNull(skills.LibraryRoot);
        Assert.Equal([new AllowedRepositorySkill("deploy", SkillHash.Of(folder))], skills.Repository);
        Assert.Contains("The repository's skill \"deploy\" is used in place of the factory library's.", await NotesAsync(threadId));
    }

    [Fact]
    public async Task UnderNever_NoRepositorySkillIsUsed_EvenAnApprovedOne()
    {
        await RestartWithCoordinatorAsync();
        await SaveSkillsAsync(new SkillSettings { RepositoryPolicy = RepositorySkillPolicy.Never });

        var (_, threadId, _) = await HandedOffWithARepositorySkillAsync();

        Assert.Null((await WaitForWorkerAsync()).Launch.SkillsPath);
        Assert.Contains("The repository's skills are not used, by the factory's settings: deploy.", await NotesAsync(threadId));
    }

    [Fact]
    public async Task ARework_KeepsItsTasksLibraryRevision_NotTheLibraryAsItIsNow()
    {
        await RestartWithCoordinatorAsync();
        await SaveSkillsAsync(new SkillSettings { Library = [Notes()] });
        var threadId = await _host.CreateThreadAsync(await _host.RegisterProjectAsync());
        await _host.DelegateAsync(threadId);
        await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        // An Admin changes the library after the handoff.
        await SaveSkillsAsync(new SkillSettings { Library = [Notes("Changed.")] });
        await _host.DelegateAsync(threadId, "@factory CSV values containing commas are wrong.");
        var rework = await WaitForWorkerAsync(2);
        await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        Assert.Equal(Path.Combine(_host.Options.DataDirectory, "skills", "1"), SkillsOf(rework).LibraryRoot);
        Assert.Equal(1, (await CapabilitiesAsync(threadId)).SkillLibraryRevision);
    }

    // ---- Skills chosen for the task (m3-architecture.md §13) ----

    private static LibrarySkill Tdd(params string[] defaultFor) =>
        new() { Name = "tdd", Description = "Test first.", Body = "Write a failing test before the code.", DefaultFor = defaultFor };

    private static LibrarySkill Prove(params string[] defaultFor) =>
        new() { Name = "prove-it-works", Description = "Show it working.", Body = "Run it and show the output.", DefaultFor = defaultFor };

    private async Task<JsonElement> NewThreadAsync(Guid projectId, object? skills = null, string typeLabel = "feature", HttpStatusCode expected = HttpStatusCode.Created) =>
        await _host.PostAsync("api/threads", new { projectId, title = "Add CSV export", typeLabel, skills }, expected);

    private static string[] SkillNames(JsonElement thread) => [.. thread.GetProperty("skills").EnumerateArray().Select(s => s.GetString()!)];

    [Fact]
    public async Task NewThread_ListsTheSkillsATaskCanTake_WithEachTypesDefaults()
    {
        await SaveSkillsAsync(new SkillSettings { Library = [Tdd("bug", "feature"), Prove() with { Enabled = false }] });

        var settings = await _host.GetAsync("api/settings");

        var skill = Assert.Single(settings.GetProperty("skills").EnumerateArray());
        Assert.Equal(("tdd", "Test first.", "Write a failing test before the code.".Length), (skill.GetProperty("name").GetString(),
            skill.GetProperty("description").GetString(), skill.GetProperty("chars").GetInt32()));
        Assert.Equal(["bug", "feature"], skill.GetProperty("defaultFor").EnumerateArray().Select(t => t.GetString()));
        Assert.Equal(12_000, settings.GetProperty("maxTaskSkillChars").GetInt32());
    }

    [Fact]
    public async Task ANewThread_TakesItsTypesDefaults_UnlessItsCreatorChoseOtherwise()
    {
        await SaveSkillsAsync(new SkillSettings { Library = [Tdd("feature"), Prove("bug")] });
        var projectId = await _host.RegisterProjectAsync();

        Assert.Equal(["tdd"], SkillNames(await NewThreadAsync(projectId)));
        Assert.Equal(["prove-it-works"], SkillNames(await NewThreadAsync(projectId, typeLabel: "bug")));
        Assert.Empty(SkillNames(await NewThreadAsync(projectId, skills: Array.Empty<string>())));
        Assert.Equal(["tdd", "prove-it-works"], SkillNames(await NewThreadAsync(projectId, skills: new[] { "prove-it-works", "TDD" })));
    }

    [Fact]
    public async Task ANewThread_WithSkillsThatCannotBeGiven_IsRefused()
    {
        await SaveSkillsAsync(new SkillSettings { Library = [Tdd(), Prove() with { Body = new string('x', 12_000) }] });
        var projectId = await _host.RegisterProjectAsync();

        var unknown = await NewThreadAsync(projectId, skills: new[] { "deploy" }, expected: HttpStatusCode.BadRequest);
        var tooBig = await NewThreadAsync(projectId, skills: new[] { "tdd", "prove-it-works" }, expected: HttpStatusCode.BadRequest);

        Assert.Contains("Not a skill in the factory's library, or turned off: deploy.", unknown.GetProperty("error").GetString());
        Assert.Contains("a task can take at most 12,000", tooBig.GetProperty("error").GetString());
        Assert.Empty(await _host.Store.ListThreadsAsync(projectId, default));
    }

    [Fact]
    public async Task ARun_IsGivenItsTasksSkillsWhole_AndRecordsThem_AndSaysWhichCouldNotBeApplied()
    {
        await RestartWithCoordinatorAsync();
        await SaveSkillsAsync(new SkillSettings { Library = [Tdd(), Prove()] });
        var threadId = (await NewThreadAsync(await _host.RegisterProjectAsync(), skills: new[] { "tdd", "prove-it-works" })).GetProperty("id").GetGuid();
        // An Admin turns one off after the task was created: the run goes on without it.
        await SaveSkillsAsync(new SkillSettings { Library = [Tdd(), Prove() with { Enabled = false }] });

        await _host.DelegateAsync(threadId);
        await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        Assert.Equal([new TaskSkill("tdd", "Write a failing test before the code.")], SkillsOf(await WaitForWorkerAsync()).TaskSkills);
        Assert.Equal(["tdd"], (await CapabilitiesAsync(threadId)).TaskSkills.Select(s => s.Name));
        Assert.Contains(
            "Skills chosen for this task that are no longer in the factory's library, or are turned off, are not applied: prove-it-works.",
            await NotesAsync(threadId));
    }

    [Fact]
    public async Task SkillsChangedWhileTheTaskWorks_WaitForAPauseAndResume_AndTheThreadSaysSo()
    {
        await RestartWithCoordinatorAsync();
        await SaveSkillsAsync(new SkillSettings { Library = [Tdd(), Prove()] });
        var threadId = (await NewThreadAsync(await _host.RegisterProjectAsync(), skills: new[] { "tdd" })).GetProperty("id").GetGuid();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _host.Workers.Script.Enqueue(async call =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, call.Token);
            return FakeWorkerLauncher.Done();
        });
        await _host.DelegateAsync(threadId);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(20));

        var changed = await _host.PostAsync($"api/threads/{threadId}/skills", new { skills = new[] { "tdd", "prove-it-works" } }, HttpStatusCode.OK);

        Assert.Equal(["tdd", "prove-it-works"], SkillNames(changed));
        Assert.Contains("The skills for this task changed to: tdd, prove-it-works. They will be applied after the task is paused and resumed.", await NotesAsync(threadId));
        Assert.Equal(["tdd"], (await CapabilitiesAsync(threadId)).TaskSkills.Select(s => s.Name)); // the working run keeps its own

        await _host.PostAsync($"api/threads/{threadId}/pause", null, HttpStatusCode.Accepted);
        await _host.WaitForStateAsync(threadId, LifecycleState.PausedUser);
        await _host.PostAsync($"api/threads/{threadId}/resume", null, HttpStatusCode.OK);
        var resumed = await WaitForWorkerAsync(2);
        await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        Assert.Equal(["tdd", "prove-it-works"], SkillsOf(resumed).TaskSkills.Select(s => s.Name));
        Assert.Equal(["tdd", "prove-it-works"], (await CapabilitiesAsync(threadId)).TaskSkills.Select(s => s.Name));
        Assert.Contains("The task's skills changed while it was stopped; it now continues with: tdd, prove-it-works.", await NotesAsync(threadId));
    }

    [Fact]
    public async Task AMember_ChangesATasksSkills_TheChangeIsAudited_AndAnUnchangedChoiceSaysNothing()
    {
        await SaveSkillsAsync(new SkillSettings { Library = [Tdd(), Prove()] });
        var projectId = await _host.RegisterProjectAsync();
        var threadId = (await NewThreadAsync(projectId, skills: new[] { "tdd" })).GetProperty("id").GetGuid();
        await _host.CreateMemberAsync("maria", projectId);
        var maria = await _host.SignedInAsync("maria");

        using (var changed = await maria.PostAsJsonAsync($"api/threads/{threadId}/skills", new { skills = new[] { "prove-it-works" } }))
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        await _host.PostAsync($"api/threads/{threadId}/skills", new { skills = new[] { "prove-it-works" } }, HttpStatusCode.OK);
        var refused = await _host.PostAsync($"api/threads/{threadId}/skills", new { skills = new[] { "deploy" } }, HttpStatusCode.BadRequest);

        Assert.Contains("deploy", refused.GetProperty("error").GetString());
        Assert.Equal(["The skills for this task changed to: prove-it-works. They apply from the task's next run."], await NotesAsync(threadId));
        var audit = Assert.Single(await _host.Store.ListAuditAsync(projectId, 100, default), a => a.Action == AuditActions.ThreadSkills);
        Assert.Contains("\"from\":[\"tdd\"]", audit.DetailsJson);
        Assert.Contains("\"to\":[\"prove-it-works\"]", audit.DetailsJson);
    }

    [Fact]
    public async Task AChat_IsGivenItsTasksSkills()
    {
        await RestartWithCoordinatorAsync();
        await SaveSkillsAsync(new SkillSettings { Library = [Tdd()] });
        var threadId = (await NewThreadAsync(await _host.RegisterProjectAsync(), skills: new[] { "tdd" })).GetProperty("id").GetGuid();

        await _host.DelegateAsync(threadId, "Where are orders exported?");

        Assert.Equal(["tdd"], SkillsOf(await WaitForWorkerAsync()).TaskSkills.Select(s => s.Name));
    }

    [Fact]
    public async Task AChat_IsGivenTheLibraryAndItsReadingCopysApprovedSkills_AndRecordsWhatItFound_WithoutANote()
    {
        await RestartWithCoordinatorAsync();
        await SaveSkillsAsync(new SkillSettings { Library = [Notes()] });
        var projectId = await _host.RegisterProjectAsync();
        WriteRepositorySkill(_host.Workspaces.ReadingOf(projectId).Path, "deploy");
        var threadId = await _host.CreateThreadAsync(projectId);

        await _host.DelegateAsync(threadId, "Where are orders exported?");

        var worker = await WaitForWorkerAsync();
        Assert.Equal(_host.Workspaces.ReadingOf(projectId).Path, worker.Launch.WorkingCopy);
        var skills = SkillsOf(worker);
        Assert.Equal((Path.Combine(_host.Options.DataDirectory, "skills", "1"), 0), (skills.LibraryRoot, skills.Repository.Count));
        Assert.Equal("deploy", Assert.Single(await _host.Store.ListRepositorySkillsAsync(default)).Name);
        Assert.DoesNotContain(await NotesAsync(threadId), n => n.Contains("skill", StringComparison.OrdinalIgnoreCase));
    }
}
