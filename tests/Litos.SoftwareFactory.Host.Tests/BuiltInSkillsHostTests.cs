using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Litos.SoftwareFactory.Core.Settings;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Host.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace Litos.SoftwareFactory.Host.Tests;

/// <summary>The built-in skills on a running host (docs/software-factory/built-in-skills.md).</summary>
public sealed class BuiltInSkillsHostTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private FactorySettings _settings = null!;

    public async Task InitializeAsync()
    {
        _host = await TestHost.StartAsync(o => o.BuiltInSkills = true, startCoordinator: false);
        _settings = _host.App.Services.GetRequiredService<FactorySettings>();
    }

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    private async Task<HttpResponseMessage> PutSkillsAsync(SkillSettings settings) =>
        await _host.Client.PutAsJsonAsync("api/admin/settings/skills", new { revision = _settings.RevisionOf(SettingsSections.Skills), settings });

    [Fact]
    public void ANewFactory_StartsWithEveryBuiltIn_AsShipped()
    {
        var skills = _settings.Skills;

        Assert.Equal(BuiltInSkills.All.Select(s => s.Name), skills.Library.Select(s => s.Name));
        Assert.All(skills.Library, s => Assert.Equal(BuiltInState.AsShipped, BuiltInSkills.StateOf(s)));
        Assert.Equal(BuiltInSkills.All.Select(s => s.Name), skills.Offered);
        Assert.Equal(
            new[] { "address-feedback", "grill-the-request", "review-rubric", "root-cause-debugging", "tdd", "verify-before-submit" },
            TaskSkillSelection.DefaultsFor(skills, "bug").Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ARestart_WritesNothing_AndARemovedBuiltInStaysRemoved()
    {
        var revision = _settings.RevisionOf(SettingsSections.Skills);
        Assert.Empty(await SettingsSeeding.SyncBuiltInSkillsAsync(_settings, default));
        Assert.Equal(revision, _settings.RevisionOf(SettingsSections.Skills));

        using (var removed = await PutSkillsAsync(_settings.Skills with { Library = [.. _settings.Skills.Library.Where(s => s.Name != "safe-refactor")] }))
            Assert.Equal(HttpStatusCode.OK, removed.StatusCode);

        Assert.Empty(await SettingsSeeding.SyncBuiltInSkillsAsync(_settings, default));
        Assert.DoesNotContain(_settings.Skills.Library, s => s.Name == "safe-refactor");
        Assert.Contains("safe-refactor", _settings.Skills.Offered);
    }

    [Fact]
    public async Task SavingAnEditedBuiltIn_KeepsItMarked_AndModified_AndResetBringsItBack()
    {
        var tdd = BuiltInSkills.Find("tdd")!;
        var library = _settings.Skills.Library.Select(s => s.Name == "tdd" ? s with { Body = s.Body + "\nUse xUnit." } : s).ToList();

        using (var edited = await PutSkillsAsync(_settings.Skills with { Library = library }))
            Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var saved = _settings.Skills.Library.Single(s => s.Name == "tdd");
        Assert.Equal(("tdd", tdd.Version, BuiltInState.Modified), (saved.BuiltIn, saved.BuiltInVersion, BuiltInSkills.StateOf(saved)));

        using (var reset = await PutSkillsAsync(_settings.Skills with { Library = [.. _settings.Skills.Library.Select(s => s.Name == "tdd" ? tdd.Apply(s) with { BuiltInHash = "sha256:forged" } : s)] }))
            Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.Equal(BuiltInState.AsShipped, BuiltInSkills.StateOf(_settings.Skills.Library.Single(s => s.Name == "tdd")));
    }

    [Fact]
    public async Task ASaveCannotForgetWhatWasOffered_OrClaimAnUnknownBuiltIn()
    {
        using (var forgetful = await PutSkillsAsync(_settings.Skills with { Offered = [] }))
            Assert.Equal(HttpStatusCode.OK, forgetful.StatusCode);
        Assert.Equal(BuiltInSkills.All.Select(s => s.Name), _settings.Skills.Offered);

        using var unknown = await PutSkillsAsync(new SkillSettings
        {
            Library = [new LibrarySkill { Name = "made-up", Description = "Use never.", Body = "x", BuiltIn = "made-up" }],
        });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Contains("not one of the factory's built-in skills", (await unknown.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task TheSettingsApi_SendsTheShippedTextOfEachBuiltIn()
    {
        var builtIns = (await _host.GetAsync("api/admin/settings")).GetProperty("builtInSkills").EnumerateArray().ToList();

        Assert.Equal(BuiltInSkills.All.Select(s => s.Name), builtIns.Select(b => b.GetProperty("name").GetString()));
        var tdd = builtIns.Single(b => b.GetProperty("name").GetString() == "tdd");
        Assert.Equal(BuiltInSkills.Find("tdd")!.Body, tdd.GetProperty("body").GetString());
        Assert.Equal(["bug", "feature"], tdd.GetProperty("defaultFor").EnumerateArray().Select(t => t.GetString()));
        Assert.True(tdd.GetProperty("version").GetInt32() >= 1);
    }
}
