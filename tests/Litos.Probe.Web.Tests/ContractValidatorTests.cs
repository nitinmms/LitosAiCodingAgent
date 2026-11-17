using Litos.Probe.Web.Core.Contract;

namespace Litos.Probe.Web.Tests;

public class ContractValidatorTests
{
    private static IReadOnlyList<string> Validate(WebAppContract c) =>
        ContractValidator.Validate(c, Samples.Profiles.ForContract(c.Application.Stack));

    private static void AssertError(WebAppContract c, string fragment)
    {
        var errors = Validate(c);
        Assert.True(errors.Any(e => e.Contains(fragment, StringComparison.Ordinal)), $"Expected an error containing '{fragment}', got: {string.Join(" | ", errors)}");
    }

    [Fact]
    public void A_minimal_contract_is_valid() => Assert.Empty(Validate(Samples.Minimal()));

    [Fact]
    public void The_clinic_contract_is_valid_with_its_serve_command_from_the_profile() => Assert.Empty(Validate(Samples.Clinic()));

    [Fact]
    public void Processes_are_required()
    {
        var c = Samples.Minimal();
        AssertError(c with { Application = c.Application with { Processes = [] } }, "at least one process is required");
    }

    [Fact]
    public void A_process_needs_a_command_when_its_profile_has_none()
    {
        var c = Samples.Minimal();
        AssertError(c with { Application = c.Application with { Processes = [new ProcessDefinition { Name = "web" }] } }, "the stack profile has no default serve command");
    }

    [Theory]
    [InlineData("Web", "must be lower-case")]
    [InlineData("", "must be lower-case")]
    public void Process_names_are_checked(string name, string fragment)
    {
        var c = Samples.Minimal();
        AssertError(c with { Application = c.Application with { Processes = [new ProcessDefinition { Name = name, Command = ["x"] }], BaseUrl = name } }, fragment);
    }

    [Fact]
    public void A_process_name_may_not_repeat()
    {
        var c = Samples.Minimal();
        var p = c.Application.Processes[0];
        AssertError(c with { Application = c.Application with { Processes = [p, p] } }, "declared twice");
    }

    [Fact]
    public void Base_url_must_name_a_process()
    {
        var c = Samples.Minimal();
        AssertError(c with { Application = c.Application with { BaseUrl = "api" } }, "there is no process named 'api'");
        AssertError(c with { Application = c.Application with { BaseUrl = "" } }, "name the process the browser opens");
    }

    [Fact]
    public void A_reset_is_required_and_none_must_be_said()
    {
        var c = Samples.Minimal();
        AssertError(c with { Application = c.Application with { Reset = null } }, "strategy: none");
    }

    [Theory]
    [InlineData(ResetStrategy.Command, "needs a command")]
    [InlineData(ResetStrategy.Endpoint, "POST /__test/reset")]
    [InlineData(ResetStrategy.SqliteSnapshot, "needs the database file")]
    public void Each_reset_strategy_needs_its_own_field(ResetStrategy strategy, string fragment)
    {
        var c = Samples.Minimal();
        AssertError(c with { Application = c.Application with { Reset = new ResetDefinition { Strategy = strategy } } }, fragment);
    }

    [Theory]
    [InlineData("../outside.db")]
    [InlineData("C:\\data\\app.db")]
    public void A_snapshot_file_must_be_inside_the_working_copy(string file)
    {
        var c = Samples.Minimal();
        AssertError(c with { Application = c.Application with { Reset = new ResetDefinition { Strategy = ResetStrategy.SqliteSnapshot, File = file } } }, "inside the working copy");
    }

    [Fact]
    public void Services_wait_for_W2()
    {
        var c = Samples.Minimal();
        AssertError(c with { Application = c.Application with { Services = [new ServiceDefinition { Name = "db", Image = "postgres:16" }] } }, "W2");
    }

    [Fact]
    public void A_service_placeholder_waits_for_W2()
    {
        var c = Samples.Minimal();
        var p = c.Application.Processes[0] with { Environment = new Dictionary<string, string> { ["DB"] = "{service:db:port}" } };
        AssertError(c with { Application = c.Application with { Processes = [p] } }, "services come with W2");
    }

    [Fact]
    public void A_port_placeholder_must_name_a_process()
    {
        var c = Samples.Minimal();
        var p = c.Application.Processes[0] with { Command = ["serve", "--port", "{port:api}"] };
        AssertError(c with { Application = c.Application with { Processes = [p] } }, "{port:api} names no process");
    }

    [Fact]
    public void Secrets_are_refused_in_build_commands_process_arguments_and_resets()
    {
        var c = Samples.Minimal();
        var withSecrets = c with
        {
            Application = c.Application with
            {
                Build = [new CommandStep { Command = ["npm", "run", "build"], Environment = new Dictionary<string, string> { ["NPM_TOKEN"] = "{secret:NPM}" } }],
                Processes = [c.Application.Processes[0] with { Command = ["node", "server.js", "--password={secret:PASS}"] }],
                Reset = new ResetDefinition { Strategy = ResetStrategy.Command, Command = ["seed", "{secret:SEED}"] },
            },
        };

        var errors = Validate(withSecrets);
        Assert.Contains(errors, e => e.StartsWith("application.build[0].environment.NPM_TOKEN", StringComparison.Ordinal) && e.Contains("not allowed here"));
        Assert.Contains(errors, e => e.StartsWith("application.processes[0].command", StringComparison.Ordinal) && e.Contains("not allowed here"));
        Assert.Contains(errors, e => e.StartsWith("application.reset", StringComparison.Ordinal) && e.Contains("not allowed here"));
    }

    [Fact]
    public void Form_login_needs_every_part()
    {
        var c = Samples.Minimal();
        var errors = Validate(c with { Testability = c.Testability with { Auth = new AuthDefinition { Type = AuthType.Form } } });

        Assert.Contains(errors, e => e.StartsWith("testability.auth.login_url", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("testability.auth.fields", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("testability.auth.submit", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("testability.auth.success_when", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("testability.auth.roles", StringComparison.Ordinal));
    }

    [Fact]
    public void A_role_needs_a_user_and_a_password()
    {
        var c = Samples.Clinic();
        var auth = c.Testability.Auth with { Roles = new Dictionary<string, RoleCredentials> { ["admin"] = new() { User = "a@b" } } };
        AssertError(c with { Testability = c.Testability with { Auth = auth } }, "testability.auth.roles.admin: needs a user and a password");
    }

    [Fact]
    public void Roles_without_a_login_are_an_error()
    {
        var c = Samples.Clinic();
        AssertError(c with { Testability = c.Testability with { Auth = c.Testability.Auth with { Type = AuthType.None } } }, "roles need a login");
    }

    [Fact]
    public void Protected_and_denied_routes_must_name_known_roles()
    {
        var c = Samples.Clinic();
        var routes = c.Testability.Routes with
        {
            Protected = new Dictionary<string, IReadOnlyList<string>> { ["staff"] = ["/appointments"] },
            Denied = new Dictionary<string, IReadOnlyList<string>> { ["nurse"] = ["/admin"] },
        };
        var errors = Validate(c with { Testability = c.Testability with { Routes = routes } });

        Assert.Contains(errors, e => e.Contains("routes.protected.staff: there is no role 'staff'"));
        Assert.Contains(errors, e => e.Contains("routes.denied.nurse: there is no role 'nurse'"));
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("//evil.example/")]
    [InlineData("http://elsewhere/")]
    public void Routes_must_be_paths_on_the_app(string route)
    {
        var c = Samples.Minimal();
        AssertError(c with { Testability = c.Testability with { Routes = new RouteSet { Public = [route] } } }, "must be a path on the app");
    }

    [Fact]
    public void Test_endpoints_and_fail_on_signals_are_checked()
    {
        var c = Samples.Minimal();
        var errors = Validate(c with
        {
            Testability = c.Testability with
            {
                TestEndpoints = new Dictionary<string, string> { ["clock"] = "clock please" },
                FailOn = [new FailOnSignal(), new FailOnSignal { SelectorVisible = "#a", TextVisible = "b" }, new FailOnSignal { LogPattern = "([" }],
            },
        });

        Assert.Contains(errors, e => e.StartsWith("testability.test_endpoints.clock", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("testability.fail_on[0]", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("testability.fail_on[1]", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("testability.fail_on[2].log_pattern", StringComparison.Ordinal));
    }

    [Fact]
    public void A_stack_that_disagrees_with_the_profile_is_an_error()
    {
        var errors = ContractValidator.Validate(Samples.Minimal("node-express-spa"), Samples.Generic);
        Assert.Contains(errors, e => e.Contains("names 'node-express-spa' but was checked against the profile 'generic-http'"));
    }

    [Fact]
    public void Only_version_1_is_supported() => AssertError(Samples.Minimal() with { Version = 2 }, "version: 2 is not supported");

    [Fact]
    public void Secret_names_lists_each_secret_once()
    {
        Assert.Equal(["TEST_ADMIN_PASS", "TEST_PATIENT_USER", "TEST_PATIENT_PASS"], ContractValidator.SecretNames(Samples.Clinic()));
    }
}
