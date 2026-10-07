using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Litos.SoftwareFactory.Core.Store;

namespace Litos.SoftwareFactory.Host.Tests;

/// <summary>
/// What the board needs from the API (§7.1, m2-architecture.md §6): each thread's turn label and
/// owner, the names of the owners a person can see, and renaming or re-filing a thread.
/// </summary>
public sealed class BoardApiTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private HttpClient _ben = null!, _cara = null!;
    private Guid _sales, _filedb, _benId, _caraId;

    public async Task InitializeAsync()
    {
        _host = await TestHost.StartAsync(startCoordinator: false);
        _sales = await _host.RegisterProjectAsync("salesapp");
        _filedb = await _host.RegisterProjectAsync("filedb-sharp");
        _benId = await _host.CreateMemberAsync("ben", _sales);
        _caraId = await _host.CreateMemberAsync("cara", _sales);
        _ben = await _host.SignedInAsync("ben");
        _cara = await _host.SignedInAsync("cara");
    }

    public async Task DisposeAsync()
    {
        _ben.Dispose();
        _cara.Dispose();
        await _host.DisposeAsync();
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private async Task<Guid> BensThreadAsync(string title = "Add CSV export")
    {
        using var created = await _ben.PostAsJsonAsync("api/threads", new { projectId = _sales, title, typeLabel = "feature" });
        return (await JsonAsync(created)).GetProperty("id").GetGuid();
    }

    private Task<HttpResponseMessage> PatchAsync(HttpClient client, Guid threadId, object body) =>
        client.PatchAsJsonAsync($"api/threads/{threadId}", body);

    [Fact]
    public async Task EveryThread_SaysWhoOwnsIt_AndWhoseMoveItIs()
    {
        var threadId = await BensThreadAsync();
        await _host.DelegateAsync(threadId);

        var thread = (await _host.GetAsync($"api/threads/{threadId}")).GetProperty("thread");
        var listed = Assert.Single((await _host.GetAsync("api/threads")).EnumerateArray());

        Assert.Equal(_benId, thread.GetProperty("ownerId").GetGuid());
        Assert.Equal("AwaitingAgent", thread.GetProperty("turn").GetString());
        Assert.Equal("AwaitingAgent", listed.GetProperty("turn").GetString());
    }

    [Fact]
    public async Task ADraft_IsNotStarted()
    {
        var threadId = await BensThreadAsync();

        Assert.Equal("NotStarted", (await _host.GetAsync($"api/threads/{threadId}")).GetProperty("thread").GetProperty("turn").GetString());
    }

    [Fact]
    public async Task Settings_ListTheTaskTypes()
    {
        Assert.Equal(["bug", "feature", "refactor", "chore"],
            (await _host.GetAsync("api/settings")).GetProperty("taskTypes").EnumerateArray().Select(t => t.GetString()));
    }

    // ---- Owners' names ----

    /// <summary>Only the owners of threads you can see, and yourself: names from other projects are not shown.</summary>
    [Fact]
    public async Task TheDirectory_NamesTheOwnersOfVisibleThreads_AndNoOneElse()
    {
        await BensThreadAsync();
        await _host.CreateThreadAsync(_filedb, "Compact the file"); // the Admin's, in a project Cara is not in

        var names = (await JsonAsync(await _cara.GetAsync("api/directory"))).EnumerateArray()
            .ToDictionary(e => e.GetProperty("id").GetGuid(), e => e.GetProperty("name").GetString());

        Assert.Equal(new[] { _benId, _caraId }.Order(), names.Keys.Order());
        Assert.Equal("ben", names[_benId]);
        Assert.DoesNotContain(await _host.AdminIdAsync(), names.Keys);
    }

    // ---- Renaming and re-filing ----

    [Fact]
    public async Task TheOwner_CanRenameAThread_AndFileItUnderAnotherType()
    {
        var threadId = await BensThreadAsync();

        using var response = await PatchAsync(_ben, threadId, new { title = "  Export orders as CSV ", typeLabel = "bug" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal(("Export orders as CSV", "bug"), (body.GetProperty("title").GetString(), body.GetProperty("typeLabel").GetString()));
        Assert.Equal(_benId, Assert.Single(await _host.Store.ListAuditAsync(null, 50, default), a => a.Action == AuditActions.ThreadEdit).ActorId);
    }

    [Fact]
    public async Task OnlyTheTypeChanges_WhenOnlyTheTypeIsSent()
    {
        var threadId = await BensThreadAsync();

        using var response = await PatchAsync(_ben, threadId, new { typeLabel = "chore" });

        Assert.Equal("Add CSV export", (await JsonAsync(response)).GetProperty("title").GetString());
    }

    [Fact]
    public async Task AnAdmin_CanChangeSomeoneElsesThread()
    {
        var threadId = await BensThreadAsync();

        using var response = await PatchAsync(_host.Client, threadId, new { typeLabel = "refactor" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>Another member can see the thread, so it is not hidden: they are told why not.</summary>
    [Fact]
    public async Task AnotherMember_CannotChangeIt()
    {
        var threadId = await BensThreadAsync();

        using var response = await PatchAsync(_cara, threadId, new { title = "Mine now" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("Add CSV export", (await _host.ThreadAsync(threadId)).Thread.Title);
    }

    [Fact]
    public async Task SomeoneOutsideTheProject_IsToldItDoesNotExist()
    {
        var threadId = await _host.CreateThreadAsync(_filedb);

        using var response = await PatchAsync(_ben, threadId, new { title = "x" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("", null, "title must be")]
    [InlineData(null, "epic", "typeLabel must be one of")]
    public async Task AnEmptyTitleOrAnUnknownType_IsRefused(string? title, string? typeLabel, string error)
    {
        var threadId = await BensThreadAsync();

        using var response = await PatchAsync(_ben, threadId, new { title, typeLabel });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(error, (await JsonAsync(response)).GetProperty("error").GetString());
    }
}
