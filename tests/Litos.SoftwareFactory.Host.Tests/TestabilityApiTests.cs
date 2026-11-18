using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Litos.Probe.Web;
using Litos.Probe.Web.Core.Contract;
using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Core.Settings;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Core.Verification;
using Litos.SoftwareFactory.Host.Settings;
using Litos.SoftwareFactory.Host.Testability;
using Microsoft.Extensions.DependencyInjection;

namespace Litos.SoftwareFactory.Host.Tests;

/// <summary>
/// W2.1 of README_Litos.Probe.Web.md: profile v3 with its web-app contract, a project's role
/// secrets, and the browser checks' files.
/// </summary>
public sealed class TestabilityApiTests : IAsyncLifetime
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13];

    private TestHost _host = null!;
    private Guid _projectId;
    private FactorySettings _settings = null!;

    public async Task InitializeAsync()
    {
        _host = await TestHost.StartAsync(o => o.BaselineOnRegistration = false, startCoordinator: false);
        _projectId = await _host.RegisterProjectAsync();
        _settings = _host.App.Services.GetRequiredService<FactorySettings>();
    }

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    private static readonly VerificationStep Step =
        new("app", UnitTests: [new UnitTestCommand("dotnet", ["test"], 300, new TestReportSpec(TestReportFormat.Trx, "r/*.trx"))]);

    /// <summary>A contract for a stateless app with a login, using the secret TEST_ADMIN_PASS.</summary>
    private static WebAppContract Contract(string password = "{secret:TEST_ADMIN_PASS}") => new()
    {
        Application = new AppDefinition
        {
            Stack = "node-express-spa",
            Processes = [new ProcessDefinition { Name = "web", Command = ["npm", "start"] }],
            BaseUrl = "web",
            Reset = new ResetDefinition { Strategy = ResetStrategy.None },
        },
        Testability = new TestabilityDefinition
        {
            Auth = new AuthDefinition
            {
                Type = AuthType.Form,
                LoginUrl = "/login",
                Fields = new LoginFields { User = "Email", Password = "Password" },
                Submit = "Log in",
                SuccessWhen = new SuccessCondition { UrlNotContains = "/login" },
                Roles = new Dictionary<string, RoleCredentials> { ["admin"] = new() { User = "admin@test", Password = password } },
            },
            Routes = new RouteSet { Public = ["/"], Protected = new Dictionary<string, IReadOnlyList<string>> { ["admin"] = ["/admin"] } },
        },
    };

    private static string ProfileJson(WebAppContract? contract) => new VerificationProfile(3, [Step]).WithWebApp(contract).ToJson();

    private async Task<HttpResponseMessage> PatchAsync(object body, HttpClient? client = null) =>
        await (client ?? _host.Client).PatchAsJsonAsync($"api/projects/{_projectId}", body);

    private static async Task<string> ErrorOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()!;

    private async Task<HttpResponseMessage> PutSecretAsync(string name, string value, HttpClient? client = null, Guid? projectId = null) =>
        await (client ?? _host.Client).PutAsJsonAsync($"api/projects/{projectId ?? _projectId}/secrets/{name}", new { value });

    private async Task<VerificationProfile> StoredProfileAsync() =>
        VerificationProfile.Parse((await _host.Store.GetProjectAsync(_projectId, default))!.VerificationProfileJson);

    // ---- Role secrets ----

    [Fact]
    public async Task A_member_sets_a_role_secret_which_is_listed_by_name_and_never_shown()
    {
        await _host.CreateMemberAsync("max", _projectId);
        var max = await _host.SignedInAsync("max");

        using (var put = await PutSecretAsync("TEST_ADMIN_PASS", "  hunter2 ", max))
            Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        using var listed = await max.GetAsync($"api/projects/{_projectId}/secrets");
        var text = await listed.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        Assert.Contains("TEST_ADMIN_PASS", text);
        Assert.DoesNotContain("hunter2", text);
        // Kept exactly as given: a password may start or end with a space.
        Assert.Equal("  hunter2 ", _settings.Secret(SecretNames.Project(_projectId, "TEST_ADMIN_PASS")));
    }

    [Fact]
    public async Task A_role_secret_can_be_cleared_once()
    {
        using (await PutSecretAsync("TEST_ADMIN_PASS", "hunter2")) { }

        using var first = await _host.Client.DeleteAsync($"api/projects/{_projectId}/secrets/TEST_ADMIN_PASS");
        using var second = await _host.Client.DeleteAsync($"api/projects/{_projectId}/secrets/TEST_ADMIN_PASS");

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
        Assert.False(_settings.IsSet(SecretNames.Project(_projectId, "TEST_ADMIN_PASS")));
    }

    [Theory]
    [InlineData("9LIVES", "hunter2", "is not a secret name")]
    [InlineData("TEST_ADMIN_PASS", "   ", "The value is empty")]
    public async Task A_bad_secret_is_refused(string name, string value, string error)
    {
        using var response = await PutSecretAsync(name, value);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(error, await ErrorOf(response));
    }

    [Fact]
    public async Task Another_projects_secrets_are_not_found()
    {
        var other = await _host.RegisterProjectAsync("other");
        await _host.CreateMemberAsync("mia", other);
        var mia = await _host.SignedInAsync("mia");

        using var put = await PutSecretAsync("TEST_ADMIN_PASS", "x", mia);
        using var list = await mia.GetAsync($"api/projects/{_projectId}/secrets");

        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, list.StatusCode);
        Assert.False(_settings.IsSet(SecretNames.Project(_projectId, "TEST_ADMIN_PASS")));
    }

    [Fact]
    public async Task Project_secrets_stay_off_the_admin_settings_and_are_audited_by_name()
    {
        using (await PutSecretAsync("TEST_ADMIN_PASS", "hunter2")) { }

        var admin = await _host.GetAsync("api/admin/settings");

        Assert.DoesNotContain("TEST_ADMIN_PASS", admin.GetProperty("secrets").ToString());
        var rows = (await _host.Store.ListAuditAsync(null, 500, default)).Where(r => r.Action == AuditActions.SecretSet).ToList();
        var text = JsonSerializer.Serialize(rows);
        Assert.Contains(SecretNames.Project(_projectId, "TEST_ADMIN_PASS"), text);
        Assert.DoesNotContain("hunter2", text);
    }

    // ---- Profile v3 ----

    [Fact]
    public async Task A_contract_whose_secrets_are_not_set_is_refused_and_names_them()
    {
        using var response = await PatchAsync(new { profileJson = ProfileJson(Contract()) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("TEST_ADMIN_PASS", await ErrorOf(response));
        using var listed = await _host.Client.GetAsync($"api/projects/{_projectId}/secrets");
        Assert.Empty((await listed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("missing").EnumerateArray());
    }

    [Fact]
    public async Task Once_its_secrets_are_set_a_contract_is_saved_and_the_secrets_list_shows_nothing_missing()
    {
        using (await PutSecretAsync("TEST_ADMIN_PASS", "hunter2")) { }

        using var response = await PatchAsync(new { profileJson = ProfileJson(Contract()) });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await StoredProfileAsync();
        Assert.Equal(3, stored.ProfileVersion);
        Assert.Equal("node-express-spa", stored.WebApp()!.Application.Stack);

        // Cleared later, the secret shows as missing again.
        using (await _host.Client.DeleteAsync($"api/projects/{_projectId}/secrets/TEST_ADMIN_PASS")) { }
        using var listed = await _host.Client.GetAsync($"api/projects/{_projectId}/secrets");
        Assert.Equal(["TEST_ADMIN_PASS"], (await listed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("missing").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task A_contract_that_does_not_validate_is_refused_with_its_problems()
    {
        var broken = Contract("{secret:TEST_ADMIN_PASS}") with
        {
            Application = Contract().Application with { BaseUrl = "api" },
        };

        using var response = await PatchAsync(new { profileJson = ProfileJson(broken) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("there is no process named 'api'", await ErrorOf(response));
    }

    [Fact]
    public async Task A_preset_change_keeps_the_adopted_contract()
    {
        using (await PutSecretAsync("TEST_ADMIN_PASS", "hunter2")) { }
        using (var saved = await PatchAsync(new { profileJson = ProfileJson(Contract()) }))
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var preset = _settings.Presets.Presets.First().Name;

        using var response = await PatchAsync(new { preset });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull((await StoredProfileAsync()).WebApp());
    }

    [Fact]
    public async Task Profile_json_without_a_contract_removes_it()
    {
        using (await PutSecretAsync("TEST_ADMIN_PASS", "hunter2")) { }
        using (await PatchAsync(new { profileJson = ProfileJson(Contract()) })) { }

        using var response = await PatchAsync(new { profileJson = ProfileJson(null) });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null((await StoredProfileAsync()).WebApp());
    }

    [Fact]
    public async Task Registering_with_a_contract_that_needs_secrets_is_refused()
    {
        using var response = await _host.Client.PostAsJsonAsync("api/projects", new
        {
            gitHubUrl = "https://github.com/acme/clinic",
            defaultBranch = "main",
            profileJson = ProfileJson(Contract()),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("set its secrets, then add the contract", await ErrorOf(response));
    }

    [Fact]
    public async Task Secrets_in_a_verification_command_are_refused()
    {
        var profile = new VerificationProfile(3,
            [Step with { UnitTests = [new UnitTestCommand("dotnet", ["test", "{secret:TEST_ADMIN_PASS}"], 300, new TestReportSpec(TestReportFormat.Trx, "r/*.trx"))] }]);

        using var response = await PatchAsync(new { profileJson = profile.ToJson() });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("may be used only in the application block", await ErrorOf(response));
    }

    // ---- The browser checks' files ----

    private async Task<string> SaveAsync(ArtifactKind kind, string fileName, byte[] bytes, Guid? projectId = null)
    {
        var sink = new FactoryArtifactSink(_host.Store, _host.App.Services.GetRequiredService<RunArtifactFiles>(),
            _host.App.Services.GetRequiredService<IClock>(), projectId ?? _projectId, runId: null);
        return await sink.SaveAsync(kind, fileName, new MemoryStream(bytes), default);
    }

    [Fact]
    public async Task A_screenshot_is_served_as_an_image_and_anything_else_as_a_download_neither_sniffed()
    {
        var shot = await SaveAsync(ArtifactKind.Screenshot, "visit-admin.png", Png);
        var log = await SaveAsync(ArtifactKind.Log, "web.log", "<script>alert(1)</script>"u8.ToArray());

        using var shown = await _host.Client.GetAsync($"api/run-artifacts/{shot}");
        using var saved = await _host.Client.GetAsync($"api/run-artifacts/{log}");

        Assert.Equal("image/png", shown.Content.Headers.ContentType!.MediaType);
        Assert.Equal(Png, await shown.Content.ReadAsByteArrayAsync());
        Assert.Equal("application/octet-stream", saved.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", saved.Content.Headers.ContentDisposition!.DispositionType);
        Assert.All([shown, saved], r =>
        {
            Assert.Equal("nosniff", r.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Contains("sandbox", r.Headers.GetValues("Content-Security-Policy").Single());
        });
    }

    [Fact]
    public async Task A_file_is_recorded_with_its_kind_and_type_and_kept_in_the_data_folder()
    {
        var id = Guid.Parse(await SaveAsync(ArtifactKind.Trace, "trace-admin.zip", [1, 2, 3]));

        var row = (await _host.Store.GetRunArtifactAsync(id, default))!;
        Assert.Equal((RunArtifactKind.Trace, "application/zip", 3L), (row.Kind, row.MediaType, row.SizeBytes));
        Assert.True(File.Exists(_host.App.Services.GetRequiredService<RunArtifactFiles>().PathOf(row)));
    }

    [Fact]
    public async Task Another_projects_files_are_not_found()
    {
        var shot = await SaveAsync(ArtifactKind.Screenshot, "a.png", Png);
        var other = await _host.RegisterProjectAsync("other");
        await _host.CreateMemberAsync("mia", other);
        var mia = await _host.SignedInAsync("mia");

        using var response = await mia.GetAsync($"api/run-artifacts/{shot}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public void The_factory_resolver_reads_only_this_projects_secrets()
    {
        _settings.SetSecretAsync(SecretNames.Project(_projectId, "TEST_ADMIN_PASS"), "mine", null, default).GetAwaiter().GetResult();
        _settings.SetSecretAsync(SecretNames.Project(Guid.NewGuid(), "OTHER"), "theirs", null, default).GetAwaiter().GetResult();
        var resolver = new ProjectSecretResolver(_settings, _projectId);

        Assert.Equal("mine", resolver.Resolve("TEST_ADMIN_PASS"));
        Assert.Null(resolver.Resolve("OTHER"));
        Assert.Null(resolver.Resolve("github"));
    }

    [Theory]
    [InlineData(ArtifactKind.Screenshot, "a.png", RunArtifactKind.Screenshot, "image/png")]
    [InlineData(ArtifactKind.Trace, "t.zip", RunArtifactKind.Trace, "application/zip")]
    [InlineData(ArtifactKind.Log, "web.log", RunArtifactKind.Log, "text/plain")]
    [InlineData(ArtifactKind.Log, "welcome.eml", RunArtifactKind.Email, "message/rfc822")]
    [InlineData(ArtifactKind.Report, "report.json", RunArtifactKind.Report, "application/json")]
    public void Kinds_and_media_types_follow_the_file(ArtifactKind kind, string file, RunArtifactKind expected, string media)
    {
        Assert.Equal(expected, FactoryArtifactSink.KindOf(kind, file));
        Assert.Equal(media, FactoryArtifactSink.MediaTypeOf(file));
    }
}
