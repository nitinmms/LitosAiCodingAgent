using System.Text.RegularExpressions;
using Litos.Probe.Web.Core.Contract;
using Litos.Probe.Web.Core.Profiles;
using Litos.Probe.Web.Profiles;

namespace Litos.Probe.Web.Tests;

public class StackProfilesTests
{
    [Fact]
    public void The_embedded_profiles_are_the_W1_set()
    {
        Assert.Equal(["aspnet-blazor-server", "generic-http", "node-express-spa", "unsupported-web"], Samples.Profiles.All.Select(p => p.Id).Order());
    }

    public static TheoryData<string> ProfileIds() => [.. StackProfiles.Embedded().All.Select(p => p.Id)];

    [Theory]
    [MemberData(nameof(ProfileIds))]
    public void Every_profile_is_well_formed(string id)
    {
        var p = Samples.Profiles.Find(id)!;

        if (p.LogErrorPattern is { } pattern)
            _ = new Regex(pattern);
        foreach (var marker in p.Detect.All.Concat(p.Detect.Any).Concat(p.Detect.None))
        {
            Assert.False(string.IsNullOrWhiteSpace(marker.Glob));
            if (marker.Pattern is { } m)
                _ = new Regex(m);
        }

        if (p.Readiness.Mode == ReadinessMode.NeedsMarker)
            Assert.NotNull(p.Readiness.DefaultSelector);
        foreach (var signal in p.ErrorSignals)
            Assert.Equal(1, new[] { signal.SelectorVisible, signal.TextVisible, signal.LogPattern }.Count(v => v is not null));
        Assert.All(p.PortTemplate.Environment.Values.Concat(p.PortTemplate.Arguments.Where(a => a.Contains('{'))), v => Assert.Contains("{port}", v));
    }

    [Fact]
    public void Tier_one_profiles_have_everything_a_dry_run_needs()
    {
        foreach (var p in Samples.Profiles.All.Where(p => p.Tier == 1))
        {
            Assert.NotEmpty(p.Defaults.Processes);
            Assert.NotEmpty(p.ProductionEnvironment);
            Assert.NotEmpty(p.TestEnvironment);
            Assert.NotNull(p.RetrofitSkill);
            Assert.Empty(ContractValidator.Validate(ContractDefaults.FromProfile(p), p));
        }
    }

    [Fact]
    public void For_contract_falls_back_to_generic_and_refuses_unknown_stacks()
    {
        Assert.Equal("generic-http", Samples.Profiles.ForContract(null).Id);
        Assert.Throws<KeyNotFoundException>(() => Samples.Profiles.ForContract("rails"));
    }

    [Fact]
    public void An_override_folder_replaces_a_profile_and_adds_new_ones()
    {
        using var folder = new TempFolder();
        folder.Write("node.json", """{ "id": "node-express-spa", "toolchain": "node", "tier": 2, "description": "edited by an Admin" }""");
        folder.Write("go.json", """{ "id": "go-http", "toolchain": "go", "portTemplate": { "environment": { "PORT": "{port}" } } }""");

        var profiles = StackProfiles.WithOverrides(folder.Path);

        Assert.Equal("edited by an Admin", profiles.Find("node-express-spa")!.Description);
        Assert.Equal("{port}", profiles.Find("go-http")!.PortTemplate.Environment["PORT"]);
        Assert.NotNull(profiles.Find("aspnet-blazor-server"));
    }

    [Fact]
    public void A_broken_override_names_its_file()
    {
        using var folder = new TempFolder();
        var file = folder.Write("bad.json", """{ "id": "x", "tierr": 2 }""");

        var ex = Assert.Throws<InvalidDataException>(() => StackProfiles.WithOverrides(folder.Path));
        Assert.Contains(file, ex.Message);
    }

    [Fact]
    public void A_missing_override_folder_is_just_the_embedded_set() =>
        Assert.Equal(4, StackProfiles.WithOverrides(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())).All.Count);
}

/// <summary>Finding 9: known framework console noise is ignored by the profile, never hidden in the app.</summary>
public class IgnoredConsoleTests
{
    private static readonly IReadOnlyList<IgnoredConsole> Passkey = [new("PasskeySubmit*.razor.js", "headless passkey autofill")];

    [Theory]
    [InlineData("http://localhost:5123/Components/Account/Shared/PasskeySubmit.razor.js:102:20")]
    [InlineData("http://localhost:5123/_content/App/Components/Account/Shared/PasskeySubmit.razor.js?v=abc:14:9")]
    [InlineData("http://localhost:5123/Components/Account/Shared/passkeysubmit.razor.js")]
    // The live check (2026-11-21): MapStaticAssets serves the module under a fingerprinted name.
    [InlineData("http://localhost:55093/Components/Account/Shared/PasskeySubmit.pr21mg9rrw.razor.js:103:21")]
    public void A_console_error_from_the_named_script_is_ignored(string location) =>
        Assert.Same(Passkey[0], Litos.Probe.Web.Browser.PageWatcher.IgnoredBy(location, Passkey));

    [Theory]
    [InlineData("http://localhost:5123/js/app.js:10:2")]
    [InlineData("http://localhost:5123/PasskeySubmit.razor.js.map:1:1")]
    [InlineData("http://localhost:5123/js/NotPasskeySubmit/other.razor.js:1:1")]
    [InlineData("http://localhost:5123/js/MyPasskeySubmit.razor.js:1:1")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_still_counts(string? location) =>
        Assert.Null(Litos.Probe.Web.Browser.PageWatcher.IgnoredBy(location, Passkey));

    [Fact]
    public void The_blazor_profile_ignores_the_identity_passkey_script_and_no_longer_says_to_edit_it()
    {
        var blazor = Samples.Profiles.Find("aspnet-blazor-server")!;

        Assert.Equal("PasskeySubmit*.razor.js", Assert.Single(blazor.IgnoreConsole).Source);
        Assert.DoesNotContain(blazor.Quirks, q => q.Contains("PasskeySubmit", StringComparison.Ordinal));
        Assert.All(StackProfiles.Embedded().All.Where(p => p.Id != blazor.Id), p => Assert.Empty(p.IgnoreConsole));
    }
}
