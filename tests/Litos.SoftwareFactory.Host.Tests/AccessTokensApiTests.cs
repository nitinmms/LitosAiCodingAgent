using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Litos.SoftwareFactory.Host.Tests;

/// <summary>
/// Personal access tokens for the tray app (ReadMe_LitosSoftwareFactory_V1.md §26.3): made in a
/// browser session, shown once, and good only for reading who you are, your task list and the
/// board's event stream.
/// </summary>
public sealed class AccessTokensApiTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private HttpClient _ben = null!;
    private Guid _sales, _filedb, _benId;

    public async Task InitializeAsync()
    {
        _host = await TestHost.StartAsync(startCoordinator: false);
        _sales = await _host.RegisterProjectAsync("salesapp");
        _filedb = await _host.RegisterProjectAsync("filedb-sharp");
        _benId = await _host.CreateMemberAsync("ben", _sales);
        _ben = await _host.SignedInAsync("ben");
    }

    public async Task DisposeAsync()
    {
        _ben.Dispose();
        await _host.DisposeAsync();
    }

    private static async Task<(Guid Id, string Secret)> CreateAsync(HttpClient client, string name = "Work laptop")
    {
        using var response = await client.PostAsJsonAsync("api/access-tokens", new { name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("token").GetProperty("id").GetGuid(), body.GetProperty("secret").GetString()!);
    }

    /// <summary>The tray app: no cookies, no CSRF header, only the token.</summary>
    private HttpClient TokenClient(string secret)
    {
        var client = new HttpClient { BaseAddress = _host.Url };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        return client;
    }

    private static async Task<JsonElement> ListAsync(HttpClient client, string query = "") =>
        await client.GetFromJsonAsync<JsonElement>("api/access-tokens" + query);

    [Fact]
    public async Task AToken_IsShownOnce_AndSignsInAsItsOwner()
    {
        var (id, secret) = await CreateAsync(_ben);

        Assert.StartsWith("lsf_", secret);
        var listed = (await ListAsync(_ben)).EnumerateArray().Single();
        Assert.Equal((id, "Work laptop", "Active"), (listed.GetProperty("id").GetGuid(), listed.GetProperty("name").GetString(), listed.GetProperty("status").GetString()));
        Assert.DoesNotContain(secret, listed.GetRawText());

        using var tray = TokenClient(secret);
        var me = await tray.GetFromJsonAsync<JsonElement>("api/auth/me");
        Assert.Equal((_benId, "ben"), (me.GetProperty("id").GetGuid(), me.GetProperty("userName").GetString()));
    }

    [Fact]
    public async Task AToken_SeesOnlyItsOwnersProjects()
    {
        await _host.CreateThreadAsync(_sales, "Add CSV export");
        await _host.CreateThreadAsync(_filedb, "Compact the file");
        var (_, secret) = await CreateAsync(_ben);
        using var tray = TokenClient(secret);

        using var list = await tray.GetAsync("api/threads");

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.True(list.Headers.Contains("X-Event-Cursor"));
        var titles = (await list.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().Select(t => t.GetProperty("title").GetString());
        Assert.Equal(["Add CSV export"], titles);
    }

    [Theory]
    [InlineData("GET", "api/projects")]
    [InlineData("GET", "api/access-tokens")]
    [InlineData("GET", "api/settings")]
    [InlineData("POST", "api/access-tokens")]
    [InlineData("POST", "api/threads")]
    [InlineData("POST", "api/auth/logout")]
    public async Task AToken_CanDoNothingElse(string method, string path)
    {
        var (_, secret) = await CreateAsync(_host.Client);
        using var tray = TokenClient(secret);
        // Even with the header the web app sends: the refusal is the token's scope, not CSRF.
        tray.DefaultRequestHeaders.Add("X-Factory-Request", "1");

        using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = method == "POST" ? JsonContent.Create(new { name = "x" }) : null };
        using var response = await tray.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("only read your tasks", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("lsf_not-a-token-anyone-made")]
    [InlineData("not-even-the-prefix")]
    [InlineData("")]
    public async Task AnUnknownToken_IsRefused_WithAFix(string secret)
    {
        using var tray = new HttpClient { BaseAddress = _host.Url };
        tray.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + secret);

        using var response = await tray.GetAsync("api/threads");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.Single().Scheme);
        Assert.Contains("Settings → Tray app", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task ABearerToken_WinsOverASessionCookie()
    {
        var (_, secret) = await CreateAsync(_ben);
        // The Admin's browser, sending Ben's token as well.
        _host.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        try
        {
            var me = await _host.Client.GetFromJsonAsync<JsonElement>("api/auth/me");
            Assert.Equal("ben", me.GetProperty("userName").GetString());
        }
        finally
        {
            _host.Client.DefaultRequestHeaders.Authorization = null;
        }
    }

    [Fact]
    public async Task ARevokedToken_StopsWorkingAtOnce()
    {
        var (id, secret) = await CreateAsync(_ben);
        using var tray = TokenClient(secret);
        using (var before = await tray.GetAsync("api/threads"))
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        using (var revoke = await _ben.PostAsync($"api/access-tokens/{id}/revoke", null))
            Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        using (var after = await tray.GetAsync("api/threads"))
            Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
        using (var again = await _ben.PostAsync($"api/access-tokens/{id}/revoke", null))
            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("Revoked", (await ListAsync(_ben)).EnumerateArray().Single().GetProperty("status").GetString());
    }

    [Fact]
    public async Task RevokingAToken_EndsItsOpenStream()
    {
        var (id, secret) = await CreateAsync(_ben);
        using var tray = TokenClient(secret);
        using var stream = await tray.GetAsync("api/events", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
        using var reader = new StreamReader(await stream.Content.ReadAsStreamAsync());

        using (var revoke = await _ben.PostAsync($"api/access-tokens/{id}/revoke", null))
            Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        // Any change wakes the stream for its next pass, which finds the token revoked.
        await _host.CreateThreadAsync(_sales, "Wake the stream");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (await reader.ReadLineAsync(timeout.Token) is not null)
        {
        }
    }

    [Fact]
    public async Task ADisabledAccountsTokens_StopWorking()
    {
        var (_, secret) = await CreateAsync(_ben);
        using var tray = TokenClient(secret);

        await _host.PostAsync($"api/users/{_benId}/disable", null, HttpStatusCode.OK);

        using var response = await tray.GetAsync("api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task OnlyTheOwnerOrAnAdmin_RevokesAToken()
    {
        var (bens, _) = await CreateAsync(_ben);
        var (admins, _) = await CreateAsync(_host.Client, "Admin's PC");

        // Someone else's token is not even confirmed to exist.
        using (var refused = await _ben.PostAsync($"api/access-tokens/{admins}/revoke", null))
            Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        await _host.PostAsync($"api/access-tokens/{bens}/revoke", null, HttpStatusCode.NoContent);

        var bensList = (await ListAsync(_ben)).EnumerateArray().Single();
        Assert.Equal((bens, "Revoked"), (bensList.GetProperty("id").GetGuid(), bensList.GetProperty("status").GetString()));
    }

    [Fact]
    public async Task AnAdmin_SeesEveryonesTokens_AndAMemberOnlyTheirOwn()
    {
        await CreateAsync(_ben);
        await CreateAsync(_host.Client, "Admin's PC");

        var all = (await ListAsync(_host.Client, "?all=true")).EnumerateArray().Select(t => t.GetProperty("userName").GetString()).Order();
        Assert.Equal(["admin", "ben"], all);
        Assert.Single((await ListAsync(_ben)).EnumerateArray());
        using var refused = await _ben.GetAsync("api/access-tokens?all=true");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ATokenNeedsAName(string name)
    {
        using var response = await _ben.PostAsJsonAsync("api/access-tokens", new { name });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task OneAccount_HasAtMostTwentyWorkingTokens()
    {
        var first = default(Guid);
        for (var i = 0; i < 20; i++)
        {
            var (id, _) = await CreateAsync(_ben, $"PC {i}");
            if (i == 0)
                first = id;
        }

        using (var refused = await _ben.PostAsJsonAsync("api/access-tokens", new { name = "one more" }))
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        // A revoked one no longer counts.
        using (var revoke = await _ben.PostAsync($"api/access-tokens/{first}/revoke", null))
            Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        await CreateAsync(_ben, "one more");
    }

    [Fact]
    public async Task UsingAToken_IsNotedOnIt()
    {
        var (_, secret) = await CreateAsync(_ben);
        Assert.Equal(JsonValueKind.Null, (await ListAsync(_ben)).EnumerateArray().Single().GetProperty("lastUsedAt").ValueKind);

        using var tray = TokenClient(secret);
        using (await tray.GetAsync("api/auth/me"))
        {
        }

        Assert.Equal(JsonValueKind.String, (await ListAsync(_ben)).EnumerateArray().Single().GetProperty("lastUsedAt").ValueKind);
    }

    [Fact]
    public async Task MakingAndRevokingATokenIsAudited()
    {
        var (id, _) = await CreateAsync(_ben);
        using (var revoke = await _ben.PostAsync($"api/access-tokens/{id}/revoke", null))
            Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        var actions = (await _host.Store.ListAuditAsync(null, 50, default)).Where(e => e.TargetId == id).Select(e => e.Action).Order();
        Assert.Equal(["access_token.create", "access_token.revoke"], actions);
    }
}
