using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Settings;
using Litos.SoftwareFactory.Core.Store;

namespace Litos.SoftwareFactory.Host.Tests;

/// <summary>
/// What runs are given of a project's lessons (m4-architecture.md §4): approved, enabled, matching
/// lessons, chosen when a run starts and recorded once; never a suggestion or another project's
/// (acceptance scenario 16); a rework chooses afresh; a person can pick one, and judge whether one helped.
/// </summary>
public sealed class RunLessonsTests : IAsyncLifetime
{
    private TestHost _host = null!;

    public async Task InitializeAsync() => _host = await TestHost.StartAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private async Task<Guid> LessonAsync(Guid projectId, string rule, string topic, bool approve = true) =>
        (await _host.PostAsync($"api/projects/{projectId}/lessons", new { rule, topics = new[] { topic }, approve }, HttpStatusCode.Created)).GetProperty("id").GetGuid();

    private async Task<FakeWorker> WorkerAsync(int count = 1)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (_host.Workers.Workers.Count < count)
        {
            Assert.True(DateTime.UtcNow < deadline, "No worker was launched.");
            await Task.Delay(25);
        }

        return _host.Workers.Workers.ElementAt(count - 1);
    }

    private static RunSkills SkillsOf(FakeWorker worker) =>
        worker.Launch.SkillsPath is { } path ? JsonSerializer.Deserialize<RunSkills>(File.ReadAllText(path), FactoryWire.Json)! : new RunSkills(null, []);

    private async Task<RunCapabilities> CapabilitiesAsync(Guid threadId) =>
        JsonSerializer.Deserialize<RunCapabilities>((await _host.ThreadAsync(threadId)).LatestRun!.CapabilitiesJson!, FactoryWire.Json)!;

    private async Task<string[]> NotesAsync(Guid threadId) =>
        [.. (await _host.ThreadAsync(threadId)).Messages.Where(m => m.Kind == MessageKind.Status).Select(m => m.Text)];

    [Fact]
    public async Task ARun_IsGivenTheApprovedLessonsMatchingItsTask_NeverASuggestionOrAnotherProjects()
    {
        var projectId = await _host.RegisterProjectAsync();
        await LessonAsync(projectId, "Quote every CSV value.", "csv");
        await LessonAsync(projectId, "CSV headers are lower case.", "csv", approve: false);
        await LessonAsync(projectId, "Dates are UTC.", "dates");
        await LessonAsync(await _host.RegisterProjectAsync("billing"), "CSV files use semicolons.", "csv");
        var threadId = await _host.CreateThreadAsync(projectId);

        await _host.DelegateAsync(threadId);
        await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        var given = Assert.Single(SkillsOf(await WorkerAsync()).Lessons);
        Assert.Equal((1L, 1, "Quote every CSV value."), (given.Number, given.Revision, given.Rule));
        Assert.Equal([given], (await CapabilitiesAsync(threadId)).Lessons);
        Assert.Contains("Project lessons given to this run: L-1.", await NotesAsync(threadId));

        var runId = (await _host.ThreadAsync(threadId)).LatestRun!.Id;
        var snapshot = Assert.Single((await _host.GetAsync($"api/runs/{runId}/lessons")).EnumerateArray());
        Assert.Equal(("Retrieved", 2, "Quote every CSV value."), (snapshot.GetProperty("inclusion").GetString(), snapshot.GetProperty("score").GetInt32(), snapshot.GetProperty("rule").GetString()));
        Assert.Single((await _host.GetAsync($"api/threads/{threadId}")).GetProperty("runLessons").EnumerateArray());
    }

    [Fact]
    public async Task ARunWithNoMatchingLesson_IsGivenNone_AndTheThreadSaysNothingAboutLessons()
    {
        var projectId = await _host.RegisterProjectAsync();
        await LessonAsync(projectId, "Dates are UTC.", "dates");
        var threadId = await _host.CreateThreadAsync(projectId);

        await _host.DelegateAsync(threadId);
        await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        Assert.Empty(SkillsOf(await WorkerAsync()).Lessons);
        Assert.DoesNotContain(await NotesAsync(threadId), n => n.Contains("lesson", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AReworkChoosesAfresh_AndTheFirstRunsSnapshotKeepsWhatItWasGiven()
    {
        var projectId = await _host.RegisterProjectAsync();
        var lessonId = await LessonAsync(projectId, "Quote every CSV value.", "csv");
        var threadId = await _host.CreateThreadAsync(projectId);
        await _host.DelegateAsync(threadId);
        await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);
        var firstRun = (await _host.ThreadAsync(threadId)).LatestRun!.Id;

        // An Admin edits and approves the lesson after the handoff.
        await _host.PostAsync($"api/lessons/{lessonId}/revisions", new { expectedRevision = 1, rule = "Quote every CSV value, even numbers.", topics = new[] { "csv" }, approve = true }, HttpStatusCode.OK);
        await _host.DelegateAsync(threadId, "@factory CSV values containing commas are wrong.");
        var rework = await WorkerAsync(2);
        await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        Assert.Equal(new RunLesson(1, 2, "Quote every CSV value, even numbers."), Assert.Single(SkillsOf(rework).Lessons));
        var first = Assert.Single((await _host.GetAsync($"api/runs/{firstRun}/lessons")).EnumerateArray());
        Assert.Equal((1, "Quote every CSV value."), (first.GetProperty("revision").GetInt32(), first.GetProperty("rule").GetString()));
    }

    [Fact]
    public async Task ALessonPickedForTheTask_IsGivenFirst_EvenWithoutAMatch()
    {
        var projectId = await _host.RegisterProjectAsync();
        var picked = await LessonAsync(projectId, "Dates are UTC.", "dates");
        var threadId = await _host.CreateThreadAsync(projectId);

        var thread = await _host.PostAsync($"api/threads/{threadId}/lessons", new { lessonIds = new[] { picked } }, HttpStatusCode.OK);
        Assert.Equal(picked, Assert.Single(thread.GetProperty("manualLessons").EnumerateArray()).GetGuid());
        await _host.DelegateAsync(threadId);
        await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        Assert.Equal("Dates are UTC.", Assert.Single(SkillsOf(await WorkerAsync()).Lessons).Rule);
        var runId = (await _host.ThreadAsync(threadId)).LatestRun!.Id;
        var snapshot = Assert.Single((await _host.GetAsync($"api/runs/{runId}/lessons")).EnumerateArray());
        Assert.Equal(("Manual", true), (snapshot.GetProperty("inclusion").GetString(), snapshot.GetProperty("manual").GetBoolean()));
        var refused = await _host.PostAsync($"api/threads/{threadId}/lessons", new { lessonIds = new[] { Guid.NewGuid() } }, HttpStatusCode.Conflict);
        Assert.Contains("approved, enabled lesson", refused.GetProperty("error").GetString());
    }

    [Fact]
    public async Task APerson_SaysWhetherAGivenLessonHelped_AndOutsideTheProject_TheRunsLessonsAreNotFound()
    {
        var projectId = await _host.RegisterProjectAsync();
        var lessonId = await LessonAsync(projectId, "Quote every CSV value.", "csv");
        var threadId = await _host.CreateThreadAsync(projectId);
        await _host.DelegateAsync(threadId);
        await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);
        var runId = (await _host.ThreadAsync(threadId)).LatestRun!.Id;

        var judged = await _host.PostAsync($"api/runs/{runId}/lessons/{lessonId}/verdict", new { verdict = "helped" }, HttpStatusCode.OK);
        Assert.Equal("Helped", judged.GetProperty("verdict").GetString());
        await _host.PostAsync($"api/runs/{runId}/lessons/{lessonId}/verdict", new { verdict = "Maybe" }, HttpStatusCode.BadRequest);
        await _host.PostAsync($"api/runs/{runId}/lessons/{Guid.NewGuid()}/verdict", new { verdict = "Helped" }, HttpStatusCode.NotFound);

        await _host.CreateMemberAsync("omar", await _host.RegisterProjectAsync("billing"));
        var omar = await _host.SignedInAsync("omar");
        using var list = await omar.GetAsync($"api/runs/{runId}/lessons");
        using var verdict = await omar.PostAsJsonAsync($"api/runs/{runId}/lessons/{lessonId}/verdict", new { verdict = "Misled" });
        using var pick = await omar.PostAsJsonAsync($"api/threads/{threadId}/lessons", new { lessonIds = Array.Empty<Guid>() });
        Assert.All(new[] { list, verdict, pick }, r => Assert.Equal(HttpStatusCode.NotFound, r.StatusCode));
    }
}
