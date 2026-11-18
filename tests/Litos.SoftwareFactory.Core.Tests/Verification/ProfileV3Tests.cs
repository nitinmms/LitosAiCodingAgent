using Litos.Probe.Web.Core.Contract;
using Litos.SoftwareFactory.Core.Verification;

namespace Litos.SoftwareFactory.Core.Tests.Verification;

/// <summary>Verification profile v3: the web-app contract it embeds (testability-architecture.md §5).</summary>
public class ProfileV3Tests
{
    private static readonly VerificationStep Step =
        new("app", UnitTests: [new UnitTestCommand("dotnet", ["test"], 300, new TestReportSpec(TestReportFormat.Trx, "r/*.trx"))]);

    private static WebAppContract Contract() => new()
    {
        Application = new AppDefinition
        {
            Stack = "aspnet-blazor-server",
            Processes = [new ProcessDefinition { Name = "web", Environment = new Dictionary<string, string> { ["TEST_ADMIN_PASS"] = "{secret:TEST_ADMIN_PASS}" } }],
            BaseUrl = "web",
            Reset = new ResetDefinition { Strategy = ResetStrategy.SqliteSnapshot, File = "Data/app.db" },
        },
        Testability = new TestabilityDefinition
        {
            Auth = new AuthDefinition
            {
                Type = AuthType.Form,
                LoginUrl = "/Account/Login",
                Fields = new LoginFields { User = "Email", Password = "Password" },
                Submit = "Log in",
                SuccessWhen = new SuccessCondition { UrlNotContains = "/Account/Login" },
                Roles = new Dictionary<string, RoleCredentials> { ["admin"] = new() { User = "a@b.test", Password = "{secret:TEST_ADMIN_PASS}" } },
            },
            Routes = new RouteSet { Public = ["/"], Protected = new Dictionary<string, IReadOnlyList<string>> { ["admin"] = ["/admin"] } },
        },
    };

    [Fact]
    public void A_version_2_profile_reads_as_it_is_with_no_web_app()
    {
        var profile = VerificationProfile.Parse("""{"profileVersion":2,"steps":[{"name":"app","unitTests":[{"executable":"dotnet","arguments":["test"],"testReport":{"format":"trx","path":"r/*.trx"}}]}]}""");

        Assert.Equal(2, profile.ProfileVersion);
        Assert.Null(profile.WebApp());
        Assert.Empty(profile.Validate());
    }

    [Fact]
    public void New_profiles_are_written_as_version_3()
    {
        Assert.Equal(3, VerificationProfile.CurrentVersion);
        Assert.Empty(new VerificationProfile(VerificationProfile.CurrentVersion, [Step]).Validate());
    }

    [Fact]
    public void A_contract_round_trips_through_the_profile_json()
    {
        var profile = new VerificationProfile(2, [Step]).WithWebApp(Contract());

        var json = profile.ToJson();
        var back = VerificationProfile.Parse(json);

        Assert.Equal(3, back.ProfileVersion);
        Assert.Contains("\"strategy\": \"sqlite-snapshot\"", json);
        Assert.Contains("\"type\": \"form\"", json);
        Assert.Equal("Data/app.db", back.Application!.Reset!.File);
        Assert.Equal("{secret:TEST_ADMIN_PASS}", back.WebApp()!.Testability.Auth.Roles["admin"].Password);
        Assert.Equal(["/admin"], back.Testability!.Routes.Protected["admin"]);
        Assert.Equal(json, back.ToJson());
        Assert.Empty(back.Validate());
    }

    [Fact]
    public void Removing_the_contract_keeps_the_version_and_the_commands()
    {
        var profile = new VerificationProfile(3, [Step]).WithWebApp(Contract()).WithWebApp(null);

        Assert.Null(profile.WebApp());
        Assert.Equal(3, profile.ProfileVersion);
        Assert.Single(profile.Steps);
    }

    [Fact]
    public void Web_app_blocks_need_version_3()
    {
        var problems = (new VerificationProfile(2, [Step]) with { Application = Contract().Application }).Validate();
        Assert.Contains(problems, p => p.Contains("need profileVersion 3"));
    }

    [Fact]
    public void Testability_without_an_application_is_refused()
    {
        var problems = (new VerificationProfile(3, [Step]) with { Testability = new TestabilityDefinition() }).Validate();
        Assert.Contains(problems, p => p.Contains("testability needs an application block"));
    }

    [Theory]
    [InlineData("command")]
    [InlineData("environment")]
    public void Secrets_never_reach_a_verification_command(string where)
    {
        var profile = where == "command"
            ? new VerificationProfile(3, [Step with { UnitTests = [new UnitTestCommand("dotnet", ["test", "--password={secret:PASS}"], 300, new TestReportSpec(TestReportFormat.Trx, "r/*.trx"))] }])
            : new VerificationProfile(3, [Step], Environment: new Dictionary<string, string> { ["DB_PASSWORD"] = "{secret:PASS}" });

        Assert.Contains(profile.Validate(), p => p.Contains("{secret:…} may be used only in the application block"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void Unknown_versions_are_refused(int version) =>
        Assert.Contains(new VerificationProfile(version, [Step]).Validate(), p => p.Contains("profileVersion must be 2 or 3"));
}
