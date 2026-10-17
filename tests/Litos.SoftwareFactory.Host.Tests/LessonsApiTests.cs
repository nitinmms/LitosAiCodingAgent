using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Litos.SoftwareFactory.Host.Tests;

/// <summary>
/// Lessons through the API (m4-architecture.md §3.2, §3.3): everyone on a project reads its lessons
/// and writes suggestions; only an Admin approves, edits an approved lesson, dismisses, disables or
/// supersedes; outside the project nothing is found.
/// </summary>
public sealed class LessonsApiTests : IAsyncLifetime
{
    private TestHost _host = null!;

    public async Task InitializeAsync() => _host = await TestHost.StartAsync(startCoordinator: false);

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private static object Lesson(string rule = "Money is stored as integer cents, never as floating point.", bool approve = false) =>
        new { rule, topics = new[] { "Money", "cents" }, paths = new[] { "src/Money/**" }, reason = "The tester corrected it.", approve };

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    private async Task<(Guid ProjectId, HttpClient Maria)> ProjectWithMemberAsync()
    {
        var projectId = await _host.RegisterProjectAsync();
        await _host.CreateMemberAsync("maria", projectId);
        return (projectId, await _host.SignedInAsync("maria"));
    }

    [Fact]
    public async Task AMember_WritesASuggestion_ThatEveryoneOnTheProjectSees()
    {
        var (projectId, maria) = await ProjectWithMemberAsync();

        using var created = await maria.PostAsJsonAsync($"api/projects/{projectId}/lessons", Lesson());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var lesson = await BodyAsync(created);

        Assert.Equal(("Suggested", 1L, 1), (lesson.GetProperty("status").GetString(), lesson.GetProperty("number").GetInt64(), lesson.GetProperty("currentRevision").GetInt32()));
        Assert.Equal(["money", "cents"], lesson.GetProperty("current").GetProperty("topics").EnumerateArray().Select(t => t.GetString()));
        Assert.Equal("maria", lesson.GetProperty("current").GetProperty("author").GetString());
        Assert.Equal("Manual", lesson.GetProperty("current").GetProperty("source").GetString());
        // Acceptance scenario 18: saving a lesson by hand calls no model.
        Assert.Empty(_host.Provider.Requests);

        var listed = await _host.GetAsync($"api/projects/{projectId}/lessons");
        Assert.Equal(lesson.GetProperty("id").GetGuid(), Assert.Single(listed.EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Empty((await _host.GetAsync($"api/projects/{projectId}/lessons?status=approved")).EnumerateArray());
        using var badFilter = await maria.GetAsync($"api/projects/{projectId}/lessons?status=Pending");
        Assert.Equal(HttpStatusCode.BadRequest, badFilter.StatusCode);
    }

    [Fact]
    public async Task OnlyAnAdmin_Approves_AndAMemberCannotSaveAndApprove()
    {
        var (projectId, maria) = await ProjectWithMemberAsync();
        var lesson = await BodyAsync(await maria.PostAsJsonAsync($"api/projects/{projectId}/lessons", Lesson()));
        var id = lesson.GetProperty("id").GetGuid();

        using var memberApprove = await maria.PostAsJsonAsync($"api/lessons/{id}/approve", new { expectedRevision = 1 });
        using var memberSaveAndApprove = await maria.PostAsJsonAsync($"api/projects/{projectId}/lessons", Lesson("Dates are UTC.", approve: true));
        Assert.Equal((HttpStatusCode.Forbidden, HttpStatusCode.Forbidden), (memberApprove.StatusCode, memberSaveAndApprove.StatusCode));

        var approved = await _host.PostAsync($"api/lessons/{id}/approve", new { expectedRevision = 1, idempotencyKey = "k-1" }, HttpStatusCode.OK);
        Assert.Equal(("Approved", 1), (approved.GetProperty("status").GetString(), approved.GetProperty("approvedRevision").GetInt32()));
        Assert.Equal("admin", approved.GetProperty("current").GetProperty("approvedBy").GetString());
        // The same request again is safe.
        await _host.PostAsync($"api/lessons/{id}/approve", new { expectedRevision = 1, idempotencyKey = "k-1" }, HttpStatusCode.OK);

        var saved = await _host.PostAsync($"api/projects/{projectId}/lessons", Lesson("Dates are stored in UTC.", approve: true), HttpStatusCode.Created);
        Assert.Equal("Approved", saved.GetProperty("status").GetString());
    }

    [Fact]
    public async Task AMember_EditsASuggestion_ButOnlyAnAdmin_EditsAnApprovedLesson_WhichKeepsItsApprovedRevisionUntilApproved()
    {
        var (projectId, maria) = await ProjectWithMemberAsync();
        var id = (await BodyAsync(await maria.PostAsJsonAsync($"api/projects/{projectId}/lessons", Lesson()))).GetProperty("id").GetGuid();

        using (var edit = await maria.PostAsJsonAsync($"api/lessons/{id}/revisions", new { expectedRevision = 1, rule = "Money is integer cents.", topics = new[] { "money" } }))
            Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        await _host.PostAsync($"api/lessons/{id}/approve", new { expectedRevision = 2 }, HttpStatusCode.OK);

        using var memberEdit = await maria.PostAsJsonAsync($"api/lessons/{id}/revisions", new { expectedRevision = 2, rule = "Money is cents.", topics = new[] { "money" } });
        Assert.Equal(HttpStatusCode.Forbidden, memberEdit.StatusCode);

        var pending = await _host.PostAsync($"api/lessons/{id}/revisions", new { expectedRevision = 2, rule = "Money is cents in a long.", topics = new[] { "money" } }, HttpStatusCode.OK);
        Assert.True(pending.GetProperty("pendingEdit").GetBoolean());
        Assert.Equal("Money is integer cents.", pending.GetProperty("approved").GetProperty("rule").GetString());
        Assert.Equal("Money is cents in a long.", pending.GetProperty("current").GetProperty("rule").GetString());
    }

    [Fact]
    public async Task AStaleRevision_OrADuplicate_IsAConflict_AndAnUnusableLesson_IsRefusedSayingWhy()
    {
        var projectId = await _host.RegisterProjectAsync();
        var id = (await _host.PostAsync($"api/projects/{projectId}/lessons", Lesson(), HttpStatusCode.Created)).GetProperty("id").GetGuid();

        var duplicate = await _host.PostAsync($"api/projects/{projectId}/lessons", Lesson("MONEY is stored as integer cents never as floating point"), HttpStatusCode.Conflict);
        Assert.Contains("repeats L-1", duplicate.GetProperty("error").GetString());
        var stale = await _host.PostAsync($"api/lessons/{id}/approve", new { expectedRevision = 3 }, HttpStatusCode.Conflict);
        Assert.Contains("revision 1, not 3", stale.GetProperty("error").GetString());

        var secret = await _host.PostAsync($"api/projects/{projectId}/lessons", Lesson("Use the key sk-ant-api03-AbCdEfGhIjKlMnOpQrStUv."), HttpStatusCode.BadRequest);
        Assert.Contains("secret", secret.GetProperty("error").GetString());
        var noTopic = await _host.PostAsync($"api/projects/{projectId}/lessons", new { rule = "Use cents." }, HttpStatusCode.BadRequest);
        Assert.Contains("at least one topic", noTopic.GetProperty("error").GetString());
        var badSource = await _host.PostAsync($"api/projects/{projectId}/lessons", new { rule = "Use cents.", topics = new[] { "money" }, source = "Hunch" }, HttpStatusCode.BadRequest);
        Assert.Contains("source must be", badSource.GetProperty("error").GetString());
    }

    [Fact]
    public async Task DismissDisableEnableAndSupersede_AreForAnAdmin()
    {
        var (projectId, maria) = await ProjectWithMemberAsync();
        var suggestion = (await _host.PostAsync($"api/projects/{projectId}/lessons", Lesson(), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var old = (await _host.PostAsync($"api/projects/{projectId}/lessons", Lesson("Dates are UTC.", approve: true), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var replacement = (await _host.PostAsync($"api/projects/{projectId}/lessons", Lesson("Dates are UTC, with an offset.", approve: true), HttpStatusCode.Created)).GetProperty("id").GetGuid();

        foreach (var (path, body) in new (string, object)[]
                 {
                     ($"api/lessons/{suggestion}/dismiss", new { expectedRevision = 1 }),
                     ($"api/lessons/{old}/disable", new { expectedRevision = 1 }),
                     ($"api/lessons/{old}/supersede", new { expectedRevision = 1, replacementId = replacement }),
                 })
        {
            using var refused = await maria.PostAsJsonAsync(path, body);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        Assert.Equal("Dismissed", (await _host.PostAsync($"api/lessons/{suggestion}/dismiss", new { expectedRevision = 1 }, HttpStatusCode.OK)).GetProperty("status").GetString());
        Assert.False((await _host.PostAsync($"api/lessons/{old}/disable", new { expectedRevision = 1 }, HttpStatusCode.OK)).GetProperty("enabled").GetBoolean());
        Assert.True((await _host.PostAsync($"api/lessons/{old}/enable", new { expectedRevision = 1 }, HttpStatusCode.OK)).GetProperty("enabled").GetBoolean());
        var superseded = await _host.PostAsync($"api/lessons/{old}/supersede", new { expectedRevision = 1, replacementId = replacement }, HttpStatusCode.OK);
        Assert.Equal(replacement, superseded.GetProperty("supersededById").GetGuid());
        Assert.False(superseded.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task OutsideTheProject_ItsLessonsAreNotFound()
    {
        var projectId = await _host.RegisterProjectAsync();
        var id = (await _host.PostAsync($"api/projects/{projectId}/lessons", Lesson(), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await _host.CreateMemberAsync("omar", await _host.RegisterProjectAsync("billing"));
        var omar = await _host.SignedInAsync("omar");

        using var list = await omar.GetAsync($"api/projects/{projectId}/lessons");
        using var write = await omar.PostAsJsonAsync($"api/projects/{projectId}/lessons", Lesson("Other."));
        using var read = await omar.GetAsync($"api/lessons/{id}");
        using var edit = await omar.PostAsJsonAsync($"api/lessons/{id}/revisions", new { expectedRevision = 1, rule = "x", topics = new[] { "x" } });
        using var missing = await _host.Client.GetAsync($"api/lessons/{Guid.NewGuid()}");

        Assert.All(new[] { list, write, read, edit, missing }, r => Assert.Equal(HttpStatusCode.NotFound, r.StatusCode));
        Assert.Equal(1, (await _host.GetAsync($"api/lessons/{id}")).GetProperty("number").GetInt64());
    }
}
