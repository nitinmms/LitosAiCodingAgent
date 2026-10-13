using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Litos.SoftwareFactory.Core.Settings;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Core.Verification;
using Litos.SoftwareFactory.Host.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace Litos.SoftwareFactory.Host.Tests;

/// <summary>
/// Verification presets and project profiles (m3-architecture.md §8): presets edited on their tab,
/// registration checks, replacing a registered project's profile, and the baseline measured as
/// soon as a project is registered or its profile replaced.
/// </summary>
public sealed class ProjectProfileTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private FactorySettings _settings = null!;

    public async Task InitializeAsync() => await StartAsync(baselineOnRegistration: false);

    private async Task StartAsync(bool baselineOnRegistration)
    {
        _host = await TestHost.StartAsync(o => o.BaselineOnRegistration = baselineOnRegistration, startCoordinator: false);
        _settings = _host.App.Services.GetRequiredService<FactorySettings>();
    }

    private async Task RestartMeasuringBaselinesAsync()
    {
        await _host.DisposeAsync();
        await StartAsync(baselineOnRegistration: true);
    }

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    /// <summary>A profile whose only program is dotnet, which every machine running these tests has.</summary>
    private static string Profile(string testArgument = "test", double threshold = 80) => new VerificationProfile(
        VerificationProfile.CurrentVersion,
        [new VerificationStep("app", UnitTests: [new UnitTestCommand("dotnet", [testArgument], 300, new TestReportSpec(TestReportFormat.Trx, "r/*.trx"))])],
        new CoverageRule(threshold)).ToJson();

    private async Task<HttpResponseMessage> PutPresetsAsync(object presets, HttpClient? client = null) =>
        await (client ?? _host.Client).PutAsJsonAsync("api/admin/settings/presets", new { revision = _settings.RevisionOf(SettingsSections.Presets), settings = new { presets } });

    private async Task<HttpResponseMessage> PatchAsync(Guid projectId, object body, HttpClient? client = null) =>
        await (client ?? _host.Client).PatchAsJsonAsync($"api/projects/{projectId}", body);

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    private async Task<JsonElement> ProjectAsync(Guid projectId, HttpClient? client = null)
    {
        using var response = await (client ?? _host.Client).GetAsync("api/projects");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await BodyAsync(response)).EnumerateArray().Single(p => p.GetProperty("id").GetGuid() == projectId);
    }

    /// <summary>The project's baseline once it is no longer being measured.</summary>
    private async Task<JsonElement> SettledBaselineAsync(Guid projectId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            var baseline = (await ProjectAsync(projectId)).GetProperty("baseline");
            if (baseline.GetProperty("state").GetString() != "Measuring")
                return baseline;
            Assert.True(DateTime.UtcNow < deadline, "The baseline was still being measured after 20 seconds.");
            await Task.Delay(50);
        }
    }

    // ---- The Presets tab ----

    [Fact]
    public async Task ThePresets_AreSeededFromTheShippedOnes_WithTheirProfilesAsWritten()
    {
        var presets = (await _host.GetAsync("api/admin/settings")).GetProperty("presets");

        Assert.Equal(1, presets.GetProperty("revision").GetInt64());
        var list = presets.GetProperty("settings").GetProperty("presets").EnumerateArray().ToList();
        Assert.Equal(["dotnet", "node-react"], list.Select(p => p.GetProperty("name").GetString()));
        Assert.Equal([".NET", "Node / React"], list.Select(p => p.GetProperty("displayName").GetString()));
        // The profile is the shipped JSON as text, its formats spelled as the profile spells them.
        var dotnet = list[0].GetProperty("profile").GetString()!;
        Assert.Contains("\"format\": \"trx\"", dotnet);
        Assert.Empty(VerificationProfile.Parse(dotnet).Validate());
    }

    [Fact]
    public async Task AnAdmin_EditsThePresets_AndRegistrationOffersThemInTheirOrder()
    {
        using var saved = await PutPresetsAsync(new object[]
        {
            new { name = "dotnet-fast", displayName = ".NET without coverage", profile = Profile("test-fast") },
            new { name = "dotnet", displayName = ".NET", profile = Profile() },
        });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var offered = (await _host.GetAsync("api/settings")).GetProperty("presets").EnumerateArray()
            .Select(p => (p.GetProperty("name").GetString(), p.GetProperty("displayName").GetString()));
        Assert.Equal([("dotnet-fast", ".NET without coverage"), ("dotnet", ".NET")], offered);

        // Registration without a preset takes the first.
        var project = await _host.PostAsync("api/projects", new { gitHubUrl = "https://github.com/acme/fast" }, HttpStatusCode.Created);
        Assert.Contains("test-fast", project.GetProperty("profile").GetString());
    }

    [Theory]
    [InlineData("Bad Name", "not a usable preset name")]
    [InlineData("good", "not valid JSON")]
    public async Task APresetThatCannotBeUsed_IsRefused_WithTheReason(string name, string expected)
    {
        using var refused = await PutPresetsAsync(new object[] { new { name, displayName = "X", profile = name == "good" ? "{ not json" : Profile() } });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains(expected, (await BodyAsync(refused)).GetProperty("error").GetString());
        Assert.Equal(1, _settings.RevisionOf(SettingsSections.Presets));
    }

    [Fact]
    public async Task APresetWithoutUnitTests_OrNoPresetAtAll_IsRefused()
    {
        var noTests = new VerificationProfile(VerificationProfile.CurrentVersion, [new VerificationStep("app")]).ToJson();
        using var withoutTests = await PutPresetsAsync(new object[] { new { name = "empty", displayName = "Empty", profile = noTests } });
        Assert.Contains("has no unit-test command", (await BodyAsync(withoutTests)).GetProperty("error").GetString());

        using var none = await PutPresetsAsync(Array.Empty<object>());
        Assert.Contains("at least one preset", (await BodyAsync(none)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task OnlyAnAdmin_EditsPresets_OrReplacesAProfile()
    {
        var projectId = await _host.RegisterProjectAsync();
        await _host.CreateMemberAsync("ben", projectId);
        var ben = await _host.SignedInAsync("ben");

        using var presets = await PutPresetsAsync(new object[] { new { name = "dotnet", displayName = ".NET", profile = Profile() } }, ben);
        Assert.Equal(HttpStatusCode.Forbidden, presets.StatusCode);
        using var patch = await PatchAsync(projectId, new { preset = "node-react" }, ben);
        Assert.Equal(HttpStatusCode.Forbidden, patch.StatusCode);

        // A Member sees the project, but not its profile.
        Assert.Equal(JsonValueKind.Null, (await ProjectAsync(projectId, ben)).GetProperty("profile").ValueKind);
        Assert.Equal(JsonValueKind.String, (await ProjectAsync(projectId)).GetProperty("profile").ValueKind);
    }

    // ---- Registration ----

    [Fact]
    public async Task EditingAPreset_NeverChangesAProjectRegisteredWithIt()
    {
        var projectId = await _host.RegisterProjectAsync();
        var before = (await _host.Store.GetProjectAsync(projectId, default))!.VerificationProfileJson;

        using var saved = await PutPresetsAsync(new object[] { new { name = "dotnet", displayName = ".NET", profile = Profile("changed") } });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var project = (await _host.Store.GetProjectAsync(projectId, default))!;
        Assert.Equal(before, project.VerificationProfileJson);
        Assert.Equal(1, project.ProfileRevision);
    }

    [Fact]
    public async Task AProfileWhoseProgramIsNotOnTheHostsPath_IsRefusedAtRegistration_NamingIt()
    {
        var profile = Profile().Replace("\"dotnet\"", "\"no-such-toolchain-xyz\"");

        var error = await _host.PostAsync("api/projects", new { gitHubUrl = "https://github.com/acme/tools", profileJson = profile }, HttpStatusCode.BadRequest);

        var message = error.GetProperty("error").GetString();
        Assert.Contains("no-such-toolchain-xyz", message);
        Assert.Contains("PATH", message);
        Assert.Empty(await _host.Store.ListProjectsAsync(default));
    }

    // ---- Replacing a profile ----

    [Fact]
    public async Task ReplacingAProfile_WithAPreset_MovesItsRevisionOn_AndIsAudited()
    {
        var projectId = await _host.RegisterProjectAsync();

        using var response = await PatchAsync(projectId, new { preset = "node-react", coverageThresholdPercent = 60 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await BodyAsync(response);
        Assert.Equal(2, body.GetProperty("profileRevision").GetInt32());
        Assert.Equal(60, body.GetProperty("coverageThresholdPercent").GetDouble());
        var profile = VerificationProfile.Parse((await _host.Store.GetProjectAsync(projectId, default))!.VerificationProfileJson);
        Assert.Equal("npm", profile.Steps.Single().UnitTests!.Single().Executable);

        var audit = Assert.Single(await _host.Store.ListAuditAsync(null, 50, default), a => a.Action == AuditActions.ProjectProfileUpdate);
        Assert.Equal(projectId, audit.ProjectId);
    }

    [Fact]
    public async Task ReplacingOnlyTheThreshold_KeepsTheProjectsOwnCommands()
    {
        var projectId = await _host.PostAsync("api/projects", new { gitHubUrl = "https://github.com/acme/own", profileJson = Profile("own-tests") }, HttpStatusCode.Created);
        var id = projectId.GetProperty("id").GetGuid();

        using var response = await PatchAsync(id, new { coverageThresholdPercent = 95 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var profile = VerificationProfile.Parse((await _host.Store.GetProjectAsync(id, default))!.VerificationProfileJson);
        Assert.Equal(["own-tests"], profile.Steps.Single().UnitTests!.Single().Arguments);
        Assert.Equal(95, profile.Coverage!.ChangedLinesThresholdPercent);
    }

    [Fact]
    public async Task AProfileThatCannotBeUsed_LeavesTheProjectAsItWas()
    {
        var projectId = await _host.RegisterProjectAsync();

        using var badJson = await PatchAsync(projectId, new { profileJson = "{ nope" });
        using var unknown = await PatchAsync(projectId, new { preset = "cobol" });
        using var missing = await PatchAsync(projectId, new { profileJson = Profile().Replace("\"dotnet\"", "\"no-such-toolchain-xyz\"") });
        using var noProject = await PatchAsync(Guid.NewGuid(), new { preset = "dotnet" });

        Assert.Equal(HttpStatusCode.BadRequest, badJson.StatusCode);
        Assert.Contains("There is no preset named \"cobol\"", (await BodyAsync(unknown)).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, noProject.StatusCode);
        Assert.Equal(1, (await _host.Store.GetProjectAsync(projectId, default))!.ProfileRevision);
    }

    // ---- The baseline measured at once ----

    [Fact]
    public async Task WithBaselinesOff_AProjectSaysNotMeasured()
    {
        var projectId = await _host.RegisterProjectAsync();

        Assert.Equal("NotMeasured", (await ProjectAsync(projectId)).GetProperty("baseline").GetProperty("state").GetString());
        Assert.Empty(_host.Verifier.Requests);
    }

    [Fact]
    public async Task ARegisteredProject_HasItsBaselineMeasuredAtOnce_OnItsReadingCopy_ForItsFirstTaskToReuse()
    {
        await RestartMeasuringBaselinesAsync();
        var release = new TaskCompletionSource();
        _host.Verifier.Hold = (_, ct) => release.Task.WaitAsync(ct);

        var projectId = await _host.RegisterProjectAsync();
        Assert.Equal("Measuring", (await ProjectAsync(projectId)).GetProperty("baseline").GetProperty("state").GetString());
        release.SetResult();

        var baseline = await SettledBaselineAsync(projectId);
        Assert.Equal("Measured", baseline.GetProperty("state").GetString());
        Assert.Equal("Passed", baseline.GetProperty("build").GetString());
        Assert.Equal(1, baseline.GetProperty("passedCount").GetInt32());

        var reading = _host.Workspaces.ReadingOf(projectId);
        var request = Assert.Single(_host.Verifier.Requests);
        Assert.Equal(reading.Path, request.WorkingCopy);
        Assert.Null(request.ChangedFiles);
        Assert.Contains("read main", reading.Calls);

        // Saved as the baseline of that commit and revision, which a task starting there reuses.
        Assert.NotNull(await _host.Store.FindBaselineAsync(projectId, reading.Head, 1, default));
    }

    [Fact]
    public async Task ANewProfile_HasItsBaselineMeasuredAgain_AtItsOwnRevision()
    {
        await RestartMeasuringBaselinesAsync();
        var projectId = await _host.RegisterProjectAsync();
        await SettledBaselineAsync(projectId);
        _host.Verifier.Baseline = ScriptedVerifier.Failing("Broken.Test");

        using var response = await PatchAsync(projectId, new { preset = "node-react" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var baseline = await SettledBaselineAsync(projectId);
        Assert.Equal("Measured", baseline.GetProperty("state").GetString());
        Assert.Equal("Failed", baseline.GetProperty("unitTests").GetString());
        Assert.Equal(1, baseline.GetProperty("failedCount").GetInt32());
        Assert.Equal(2, _host.Verifier.Requests.Count);
        Assert.NotNull(await _host.Store.FindBaselineAsync(projectId, _host.Workspaces.ReadingOf(projectId).Head, 2, default));
    }

    [Fact]
    public async Task ARepositoryThatCannotBeFetched_SaysWhy_AndANewProfileTriesAgain()
    {
        await RestartMeasuringBaselinesAsync();
        var projectId = await _host.RegisterProjectAsync("gone");
        await SettledBaselineAsync(projectId);
        // The repository goes away; a new profile's baseline cannot fetch it.
        _host.Workspaces.ReadingOf(projectId).FailFetch = new InvalidOperationException("Repository not found.");

        using var response = await PatchAsync(projectId, new { coverageThresholdPercent = 70 });
        var baseline = await SettledBaselineAsync(projectId);
        Assert.Equal("CouldNotMeasure", baseline.GetProperty("state").GetString());
        Assert.Equal("Repository not found.", baseline.GetProperty("problem").GetString());

        _host.Workspaces.ReadingOf(projectId).FailFetch = null;
        using var again = await PatchAsync(projectId, new { coverageThresholdPercent = 75 });
        var settled = await SettledBaselineAsync(projectId);
        Assert.Equal("Measured", settled.GetProperty("state").GetString());
    }
}
