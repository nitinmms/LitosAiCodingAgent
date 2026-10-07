using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Litos.SoftwareFactory.Core.Store;

namespace Litos.SoftwareFactory.Host.Tests;

/// <summary>
/// The Admin's people screen (§13.3, m2-architecture.md §4). Sessions are checked against their
/// account on every request here (SessionCheckInterval zero; one minute in a real host), so what
/// disabling or a role change does to an open session shows on the next call.
/// </summary>
public sealed class UsersApiTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private Guid _sales, _filedb, _ben, _admin;

    public async Task InitializeAsync()
    {
        _host = await TestHost.StartAsync(o => o.SessionCheckInterval = TimeSpan.Zero, startCoordinator: false);
        _sales = await _host.RegisterProjectAsync("salesapp");
        _filedb = await _host.RegisterProjectAsync("filedb-sharp");
        _ben = await _host.CreateMemberAsync("ben", _sales);
        _admin = await _host.AdminIdAsync();
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private async Task<AuditEvent[]> RowsAsync(string action) =>
        [.. (await _host.Store.ListAuditAsync(null, 500, default)).Where(r => r.Action == action)];

    [Fact]
    public async Task List_ShowsEachPersonsRoleStateAndProjects()
    {
        var people = (await _host.GetAsync("api/users")).EnumerateArray().ToDictionary(p => p.GetProperty("userName").GetString()!);

        Assert.Equal(("Admin", false), (people["admin"].GetProperty("role").GetString(), people["admin"].GetProperty("disabled").GetBoolean()));
        Assert.Equal("Member", people["ben"].GetProperty("role").GetString());
        Assert.Equal([_sales], people["ben"].GetProperty("projectIds").EnumerateArray().Select(p => p.GetGuid()));
    }

    // ---- Disabling ----

    /// <summary>Today a disabled account's cookie kept working for up to 12 hours.</summary>
    [Fact]
    public async Task Disabling_EndsTheirOpenSession_AndStopsThemSigningIn()
    {
        using var ben = await _host.SignedInAsync("ben");
        Assert.Equal(HttpStatusCode.OK, (await ben.GetAsync("api/projects")).StatusCode);

        var view = await _host.PostAsync($"api/users/{_ben}/disable", null, HttpStatusCode.OK);

        Assert.True(view.GetProperty("disabled").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await ben.GetAsync("api/projects")).StatusCode);
        using var again = await _host.NewClient().PostAsJsonAsync("api/auth/login", new { userName = "ben", password = TestHost.MemberPassword });
        Assert.Equal(HttpStatusCode.Unauthorized, again.StatusCode);
        var row = Assert.Single(await RowsAsync(AuditActions.UserDisable));
        Assert.Equal((_admin, (Guid?)_ben), (row.ActorId, row.TargetId));
    }

    [Fact]
    public async Task ReEnabling_LetsThemSignInAgain()
    {
        await _host.PostAsync($"api/users/{_ben}/disable", null, HttpStatusCode.OK);

        await _host.PostAsync($"api/users/{_ben}/enable", null, HttpStatusCode.OK);

        using var ben = await _host.SignedInAsync("ben");
        Assert.Equal(HttpStatusCode.OK, (await ben.GetAsync("api/projects")).StatusCode);
        Assert.Single(await RowsAsync(AuditActions.UserEnable));
    }

    [Fact]
    public async Task DisablingTwice_OrEnablingSomeoneEnabled_IsAConflict()
    {
        await _host.PostAsync($"api/users/{_ben}/enable", null, HttpStatusCode.Conflict);
        await _host.PostAsync($"api/users/{_ben}/disable", null, HttpStatusCode.OK);
        await _host.PostAsync($"api/users/{_ben}/disable", null, HttpStatusCode.Conflict);
        await _host.PostAsync($"api/users/{Guid.NewGuid()}/disable", null, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AnAdmin_CannotDisableThemselves()
    {
        var refused = await _host.PostAsync($"api/users/{_admin}/disable", null, HttpStatusCode.Conflict);

        Assert.Contains("your own account", refused.GetProperty("error").GetString());
    }

    // ---- Roles ----

    /// <summary>A role change takes effect at the session's next check, without signing them out.</summary>
    [Fact]
    public async Task Promoting_TakesEffectInTheirOpenSession_AndDemotingTakesItAway()
    {
        using var ben = await _host.SignedInAsync("ben");
        Assert.Equal(HttpStatusCode.Forbidden, (await ben.GetAsync("api/users")).StatusCode);

        await _host.PostAsync($"api/users/{_ben}/role", new { role = "Admin" }, HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.OK, (await ben.GetAsync("api/users")).StatusCode);
        Assert.Equal(2, (await JsonAsync(await ben.GetAsync("api/projects"))).GetArrayLength());

        await _host.PostAsync($"api/users/{_ben}/role", new { role = "Member" }, HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.Forbidden, (await ben.GetAsync("api/users")).StatusCode);

        var rows = await RowsAsync(AuditActions.UserRole);
        Assert.Equal(2, rows.Length);
        Assert.All(rows, r => Assert.Equal((Guid?)_ben, r.TargetId));
    }

    [Fact]
    public async Task SettingTheRoleTheyAlreadyHave_ChangesNothing()
    {
        await _host.PostAsync($"api/users/{_ben}/role", new { role = "member" }, HttpStatusCode.OK);

        Assert.Empty(await RowsAsync(AuditActions.UserRole));
    }

    [Fact]
    public async Task AnUnknownRole_IsRefused()
    {
        await _host.PostAsync($"api/users/{_ben}/role", new { role = "Owner" }, HttpStatusCode.BadRequest);
    }

    /// <summary>The factory always keeps someone who can run it.</summary>
    [Fact]
    public async Task TheLastEnabledAdmin_CannotBeDemoted()
    {
        var refused = await _host.PostAsync($"api/users/{_admin}/role", new { role = "Member" }, HttpStatusCode.Conflict);

        Assert.Contains("last enabled Admin", refused.GetProperty("error").GetString());
    }

    [Fact]
    public async Task AnAdminWhoIsDisabled_DoesNotCountAsTheOneLeft()
    {
        await _host.PostAsync($"api/users/{_ben}/role", new { role = "Admin" }, HttpStatusCode.OK);
        await _host.PostAsync($"api/users/{_ben}/disable", null, HttpStatusCode.OK);

        await _host.PostAsync($"api/users/{_admin}/role", new { role = "Member" }, HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task WithASecondAdmin_EitherCanBeDemoted()
    {
        await _host.PostAsync($"api/users/{_ben}/role", new { role = "Admin" }, HttpStatusCode.OK);

        await _host.PostAsync($"api/users/{_admin}/role", new { role = "Member" }, HttpStatusCode.OK);
    }

    // ---- Project membership ----

    [Fact]
    public async Task AddingToAProject_ShowsItToThem_AndRemovingHidesItAgain()
    {
        using var ben = await _host.SignedInAsync("ben");

        await _host.PostAsync($"api/projects/{_filedb}/members", new { userId = _ben }, HttpStatusCode.Created);
        Assert.Equal(2, (await JsonAsync(await ben.GetAsync("api/projects"))).GetArrayLength());
        var members = await _host.GetAsync($"api/projects/{_filedb}/members");
        Assert.Contains(members.EnumerateArray(), m => m.GetProperty("userId").GetGuid() == _ben);

        using var removed = await _host.Client.DeleteAsync($"api/projects/{_filedb}/members/{_ben}");
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal(1, (await JsonAsync(await ben.GetAsync("api/projects"))).GetArrayLength());

        Assert.Single(await RowsAsync(AuditActions.MemberRemove));
    }

    [Fact]
    public async Task AddingSomeoneAlreadyThere_ChangesNothing()
    {
        await _host.PostAsync($"api/projects/{_sales}/members", new { userId = _ben }, HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Membership_OfSomeoneOrSomewhereThatDoesNotExist_IsNotFound()
    {
        await _host.PostAsync($"api/projects/{_sales}/members", new { userId = Guid.NewGuid() }, HttpStatusCode.NotFound);
        await _host.PostAsync($"api/projects/{Guid.NewGuid()}/members", new { userId = _ben }, HttpStatusCode.NotFound);
        using var removed = await _host.Client.DeleteAsync($"api/projects/{_filedb}/members/{_ben}");
        Assert.Equal(HttpStatusCode.NotFound, removed.StatusCode);
    }

    [Fact]
    public async Task AMember_CannotManagePeopleOrMembership()
    {
        using var ben = await _host.SignedInAsync("ben");

        using var list = await ben.GetAsync("api/users");
        using var disable = await ben.PostAsJsonAsync($"api/users/{_admin}/disable", new { });
        using var join = await ben.PostAsJsonAsync($"api/projects/{_filedb}/members", new { userId = _ben });

        Assert.Equal((HttpStatusCode.Forbidden, HttpStatusCode.Forbidden, HttpStatusCode.Forbidden), (list.StatusCode, disable.StatusCode, join.StatusCode));
        Assert.False(await _host.Store.IsMemberAsync(_filedb, _ben, default));
    }
}

/// <summary>The session check's interval in a real host.</summary>
public sealed class SessionCheckTests
{
    [Fact]
    public void ARealHost_ChecksSessionsEveryMinute()
    {
        Assert.Equal(TimeSpan.FromMinutes(1), new FactoryOptions().SessionCheckInterval);
    }

    [Fact]
    public async Task TheIntervalReachesIdentity()
    {
        await using var host = await TestHost.StartAsync(o => o.SessionCheckInterval = TimeSpan.FromSeconds(42), startCoordinator: false);

        var options = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Identity.SecurityStampValidatorOptions>>(host.App.Services);

        Assert.Equal(TimeSpan.FromSeconds(42), options.Value.ValidationInterval);
    }
}
