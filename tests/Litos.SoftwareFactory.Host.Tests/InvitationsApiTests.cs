using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Litos.SoftwareFactory.Core.Store;

namespace Litos.SoftwareFactory.Host.Tests;

/// <summary>
/// Invitations through the API (§13.3, m2-architecture.md §4): the Admin creates one and is shown
/// its link once; the invitee, not signed in, opens it, chooses a password and is signed in with
/// the invitation's role and projects. A link works once, can be revoked, and expires.
/// </summary>
public sealed class InvitationsApiTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private Guid _sales, _filedb;

    public async Task InitializeAsync()
    {
        _host = await TestHost.StartAsync(startCoordinator: false);
        _sales = await _host.RegisterProjectAsync("salesapp");
        _filedb = await _host.RegisterProjectAsync("filedb-sharp");
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private const string Password = "erins-long-password";

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    /// <summary>Creates an invitation as the Admin and returns its id and the token from its link.</summary>
    private async Task<(Guid Id, string Token)> InviteAsync(string userName = "erin", string role = "Member", params Guid[] projects)
    {
        var created = await _host.PostAsync("api/invitations", new { userName, role, projectIds = projects }, HttpStatusCode.Created);
        var link = created.GetProperty("link").GetString()!;
        Assert.StartsWith("#/invite/", link);
        return (created.GetProperty("invitation").GetProperty("id").GetGuid(), link["#/invite/".Length..]);
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string path, object body) => client.PostAsJsonAsync(path, body);

    // ---- The Admin's side ----

    [Fact]
    public async Task Creating_ShowsTheLinkOnce_AndTheListNeverShowsItsTokenOrHash()
    {
        var (id, token) = await InviteAsync(projects: _sales);

        var list = await _host.GetAsync("api/invitations");
        var entry = Assert.Single(list.EnumerateArray());
        Assert.Equal(id, entry.GetProperty("id").GetGuid());
        Assert.Equal("Pending", entry.GetProperty("status").GetString());
        Assert.Equal([_sales], entry.GetProperty("projectIds").EnumerateArray().Select(p => p.GetGuid()));
        var text = list.GetRawText();
        Assert.DoesNotContain(token, text);
        Assert.DoesNotContain(Auth.InvitationsApi.Hash(token), text);
        Assert.False(entry.TryGetProperty("tokenHash", out _));

        var audit = (await _host.Store.ListAuditAsync(null, 50, default)).Single(a => a.Action == AuditActions.InvitationCreate);
        Assert.DoesNotContain(token, audit.DetailsJson);
    }

    [Theory]
    [InlineData("", "Member", "userName is required")]
    [InlineData("erin smith", "Member", "userName is required")]
    [InlineData("erin", "Owner", "role must be Member or Admin")]
    public async Task Creating_WithAnUnusableNameOrRole_IsRefused(string userName, string role, string error)
    {
        var refused = await _host.PostAsync("api/invitations", new { userName, role }, HttpStatusCode.BadRequest);

        Assert.Contains(error, refused.GetProperty("error").GetString());
        Assert.Empty((await _host.GetAsync("api/invitations")).EnumerateArray());
    }

    [Fact]
    public async Task Creating_ForAProjectThatDoesNotExist_IsRefused()
    {
        await _host.PostAsync("api/invitations", new { userName = "erin", projectIds = new[] { Guid.NewGuid() } }, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Creating_ForSomeoneWhoAlreadyHasAnAccount_IsAConflict()
    {
        await _host.CreateMemberAsync("ben", _sales);

        var conflict = await _host.PostAsync("api/invitations", new { userName = "ben" }, HttpStatusCode.Conflict);

        Assert.Contains("already signs in as ben", conflict.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ASecondInvitationForTheSameName_WaitsUntilTheFirstIsRevoked()
    {
        var (first, _) = await InviteAsync();
        await _host.PostAsync("api/invitations", new { userName = "ERIN" }, HttpStatusCode.Conflict);

        await _host.PostAsync($"api/invitations/{first}/revoke", null, HttpStatusCode.NoContent);

        await InviteAsync();
    }

    [Fact]
    public async Task Revoking_IsRecorded_AndCannotBeRepeated()
    {
        var (id, _) = await InviteAsync();

        await _host.PostAsync($"api/invitations/{id}/revoke", null, HttpStatusCode.NoContent);
        await _host.PostAsync($"api/invitations/{id}/revoke", null, HttpStatusCode.Conflict);
        await _host.PostAsync($"api/invitations/{Guid.NewGuid()}/revoke", null, HttpStatusCode.NotFound);

        Assert.Equal("Revoked", Assert.Single((await _host.GetAsync("api/invitations")).EnumerateArray()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task AMember_CannotCreateListOrRevokeInvitations()
    {
        var (id, _) = await InviteAsync();
        await _host.CreateMemberAsync("ben", _sales);
        using var ben = await _host.SignedInAsync("ben");

        using var create = await PostAsync(ben, "api/invitations", new { userName = "mallory" });
        using var list = await ben.GetAsync("api/invitations");
        using var revoke = await PostAsync(ben, $"api/invitations/{id}/revoke", new { });

        Assert.Equal((HttpStatusCode.Forbidden, HttpStatusCode.Forbidden, HttpStatusCode.Forbidden), (create.StatusCode, list.StatusCode, revoke.StatusCode));
    }

    [Fact]
    public async Task NobodySignedIn_CannotManageInvitations()
    {
        using var stranger = _host.NewClient();

        using var list = await stranger.GetAsync("api/invitations");

        Assert.Equal(HttpStatusCode.Unauthorized, list.StatusCode);
    }

    // ---- The invitee's side ----

    [Fact]
    public async Task Lookup_ShowsWhoTheLinkIsFor_WithoutSigningIn()
    {
        var (_, token) = await InviteAsync(role: "Admin");
        using var invitee = _host.NewClient();

        using var response = await PostAsync(invitee, "api/invitations/lookup", new { token });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal(("erin", "Admin"), (body.GetProperty("userName").GetString(), body.GetProperty("role").GetString()));
    }

    [Fact]
    public async Task Lookup_OfALinkThatMatchesNothing_IsNotFound()
    {
        using var invitee = _host.NewClient();

        using var response = await PostAsync(invitee, "api/invitations/lookup", new { token = "not-a-real-token" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("not valid", (await JsonAsync(response)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Accepting_CreatesTheAccount_SignsItIn_AndJoinsItsProjects()
    {
        var (_, token) = await InviteAsync(projects: _sales);
        using var invitee = _host.NewClient();

        using var accepted = await PostAsync(invitee, "api/invitations/accept", new { token, password = Password, displayName = "Erin Mwangi" });

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var me = await JsonAsync(await invitee.GetAsync("api/auth/me"));
        Assert.Equal(("erin", "Erin Mwangi"), (me.GetProperty("userName").GetString(), me.GetProperty("displayName").GetString()));
        Assert.Equal(["Member"], me.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
        var projects = await JsonAsync(await invitee.GetAsync("api/projects"));
        Assert.Equal([_sales], projects.EnumerateArray().Select(p => p.GetProperty("id").GetGuid()));
        Assert.Equal("Accepted", Assert.Single((await _host.GetAsync("api/invitations")).EnumerateArray()).GetProperty("status").GetString());

        // And from now on, with the password they chose.
        using var later = _host.NewClient();
        await _host.SignInAsync(later, "erin", Password);
    }

    [Fact]
    public async Task AnInvitedAdmin_CanManageTheFactory()
    {
        var (_, token) = await InviteAsync(role: "Admin");
        using var invitee = _host.NewClient();
        (await PostAsync(invitee, "api/invitations/accept", new { token, password = Password })).Dispose();

        using var list = await invitee.GetAsync("api/invitations");
        using var projects = await invitee.GetAsync("api/projects");

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal(2, (await JsonAsync(projects)).GetArrayLength());
    }

    [Fact]
    public async Task ALinkWorksOnce()
    {
        var (_, token) = await InviteAsync();
        using var first = _host.NewClient();
        using var second = _host.NewClient();
        (await PostAsync(first, "api/invitations/accept", new { token, password = Password })).Dispose();

        using var again = await PostAsync(second, "api/invitations/accept", new { token, password = "another-long-password" });
        using var lookup = await PostAsync(second, "api/invitations/lookup", new { token });

        Assert.Equal(HttpStatusCode.Gone, again.StatusCode);
        Assert.Equal("This invitation has already been used.", (await JsonAsync(again)).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.Gone, lookup.StatusCode);
    }

    [Fact]
    public async Task ARevokedLink_CreatesNoAccount()
    {
        var (id, token) = await InviteAsync();
        await _host.PostAsync($"api/invitations/{id}/revoke", null, HttpStatusCode.NoContent);
        using var invitee = _host.NewClient();

        using var refused = await PostAsync(invitee, "api/invitations/accept", new { token, password = Password });

        Assert.Equal(HttpStatusCode.Gone, refused.StatusCode);
        Assert.Contains("revoked", (await JsonAsync(refused)).GetProperty("error").GetString());
        using var signIn = await PostAsync(_host.NewClient(), "api/auth/login", new { userName = "erin", password = Password });
        Assert.Equal(HttpStatusCode.Unauthorized, signIn.StatusCode);
    }

    /// <summary>A password the policy refuses leaves the link usable: nothing was used up.</summary>
    [Fact]
    public async Task ATooShortPassword_IsRefused_AndTheLinkStillWorks()
    {
        var (_, token) = await InviteAsync();
        using var invitee = _host.NewClient();

        using var refused = await PostAsync(invitee, "api/invitations/accept", new { token, password = "short" });
        using var accepted = await PostAsync(invitee, "api/invitations/accept", new { token, password = Password });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("12", (await JsonAsync(refused)).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    [Fact]
    public async Task Accepting_IsRecorded_AsTheNewPerson()
    {
        var (id, token) = await InviteAsync(projects: [_sales, _filedb]);
        using var invitee = _host.NewClient();
        var me = await JsonAsync(await PostAsync(invitee, "api/invitations/accept", new { token, password = Password }));
        var erin = me.GetProperty("id").GetGuid();

        var rows = (await _host.Store.ListAuditAsync(null, 50, default)).Where(a => a.ActorId == erin).ToList();

        Assert.Equal((Guid?)id, Assert.Single(rows, r => r.Action == AuditActions.InvitationAccept).TargetId);
        Assert.Equal(2, rows.Count(r => r.Action == AuditActions.MemberAdd));
    }

    /// <summary>Accepting changes state, so it needs the header the app always sends.</summary>
    [Fact]
    public async Task Accepting_WithoutTheCsrfHeader_IsRefused()
    {
        var (_, token) = await InviteAsync();
        using var forged = _host.NewClient(csrfHeader: false);

        using var refused = await PostAsync(forged, "api/invitations/accept", new { token, password = Password });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("Pending", Assert.Single((await _host.GetAsync("api/invitations")).EnumerateArray()).GetProperty("status").GetString());
    }

    /// <summary>Anonymous, so rate limited against guessing tokens, separately from sign-in.</summary>
    [Fact]
    public async Task GuessingTokens_IsRateLimited()
    {
        using var guesser = _host.NewClient();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 11; i++)
        {
            using var response = await PostAsync(guesser, "api/invitations/lookup", new { token = $"guess-{i}" });
            statuses.Add(response.StatusCode);
        }

        Assert.All(statuses.Take(10), s => Assert.Equal(HttpStatusCode.NotFound, s));
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[10]);
    }
}

/// <summary>Links that expire: a host whose invitations last no time at all.</summary>
public sealed class ExpiredInvitationTests
{
    [Fact]
    public async Task AnExpiredLink_IsGone_AndSaysSo()
    {
        await using var host = await TestHost.StartAsync(o => o.InvitationLifetime = TimeSpan.Zero, startCoordinator: false);
        var created = await host.PostAsync("api/invitations", new { userName = "erin" }, HttpStatusCode.Created);
        var token = created.GetProperty("link").GetString()!["#/invite/".Length..];
        using var invitee = host.NewClient();

        using var refused = await invitee.PostAsJsonAsync("api/invitations/accept", new { token, password = "erins-long-password" });

        Assert.Equal(HttpStatusCode.Gone, refused.StatusCode);
        Assert.Contains("expired", await refused.Content.ReadAsStringAsync());
        Assert.Equal("Expired", Assert.Single((await host.GetAsync("api/invitations")).EnumerateArray()).GetProperty("status").GetString());
    }
}
