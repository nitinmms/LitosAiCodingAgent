using Litos.Probe.Web.Core.Contract;

namespace Litos.Probe.Web.Tests;

public class ContractDefaultsTests
{
    [Fact]
    public void A_left_out_build_and_serve_command_come_from_the_profile()
    {
        var effective = ContractDefaults.Apply(Samples.Clinic(), Samples.Blazor);

        Assert.Equal(["dotnet", "build", "-c", "Release"], effective.Application.Build!.Single().Command);
        Assert.Equal(["dotnet", "run", "-c", "Release", "--no-build", "--no-launch-profile"], effective.Application.Processes[0].Command);
    }

    [Fact]
    public void An_empty_build_list_means_no_build()
    {
        var c = Samples.Minimal("node-express-spa");
        var effective = ContractDefaults.Apply(c with { Application = c.Application with { Build = [] } }, Samples.Node);
        Assert.Empty(effective.Application.Build!);
    }

    [Fact]
    public void The_port_template_is_added_when_the_contract_does_not_place_the_port()
    {
        var effective = ContractDefaults.Apply(Samples.Clinic(), Samples.Blazor);
        Assert.Equal("http://localhost:{port:web}", effective.Application.Processes[0].Environment["ASPNETCORE_URLS"]);
    }

    [Fact]
    public void The_port_template_is_skipped_when_the_contract_places_the_port()
    {
        var c = Samples.Minimal("aspnet-blazor-server");
        var effective = ContractDefaults.Apply(c, Samples.Blazor);
        Assert.False(effective.Application.Processes[0].Environment.ContainsKey("ASPNETCORE_URLS"));
        Assert.Equal("{port:web}", effective.Application.Processes[0].Environment["PORT"]);
    }

    [Fact]
    public void Port_arguments_from_the_template_are_appended_to_the_command()
    {
        var profile = Samples.Generic with { PortTemplate = new() { Arguments = ["--port", "{port}", "--strictPort"] } };
        var c = Samples.Minimal() with { };
        var process = c.Application.Processes[0] with { Environment = new Dictionary<string, string>() };
        var effective = ContractDefaults.Apply(c with { Application = c.Application with { Processes = [process] } }, profile);

        Assert.Equal(["node", "server.js", "--port", "{port:web}", "--strictPort"], effective.Application.Processes[0].Command);
    }

    [Fact]
    public void The_test_environment_goes_under_the_contracts_own_variables()
    {
        var c = Samples.Clinic();
        var process = c.Application.Processes[0] with { Environment = new Dictionary<string, string> { ["ASPNETCORE_ENVIRONMENT"] = "Staging" } };
        var effective = ContractDefaults.Apply(c with { Application = c.Application with { Processes = [process] } }, Samples.Blazor);
        Assert.Equal("Staging", effective.Application.Processes[0].Environment["ASPNETCORE_ENVIRONMENT"]);

        var defaulted = ContractDefaults.Apply(Samples.Clinic(), Samples.Blazor);
        Assert.Equal("Testing", defaulted.Application.Processes[0].Environment["ASPNETCORE_ENVIRONMENT"]);
    }

    [Fact]
    public void A_profile_that_needs_a_marker_supplies_its_default_selector()
    {
        var effective = ContractDefaults.Apply(Samples.Clinic(), Samples.Blazor);
        Assert.Equal("[data-testid=app-interactive]", effective.Testability.Ready!.Selector);
    }

    [Fact]
    public void A_load_profile_adds_no_marker() =>
        Assert.Null(ContractDefaults.Apply(Samples.Minimal("node-express-spa"), Samples.Node).Testability.Ready);

    [Fact]
    public void The_profiles_error_signals_come_before_the_contracts_own()
    {
        var c = Samples.Clinic();
        var effective = ContractDefaults.Apply(c with { Testability = c.Testability with { FailOn = [new FailOnSignal { TextVisible = "Oops" }] } }, Samples.Blazor);

        Assert.Equal("#blazor-error-ui", effective.Testability.FailOn[0].SelectorVisible);
        Assert.Equal("Oops", effective.Testability.FailOn[^1].TextVisible);
    }

    [Fact]
    public void A_dry_run_contract_is_the_profiles_defaults_with_no_login_and_no_reset()
    {
        var c = ContractDefaults.FromProfile(Samples.Node, "web");

        Assert.Equal("web", c.Application.Processes[0].WorkingDirectory);
        Assert.Equal("web", c.Application.Build![0].WorkingDirectory);
        Assert.Equal(["npm", "start"], c.Application.Processes[0].Command);
        Assert.Equal("{port:web}", c.Application.Processes[0].Environment["PORT"]);
        Assert.Equal(ResetStrategy.None, c.Application.Reset!.Strategy);
        Assert.Equal(AuthType.None, c.Testability.Auth.Type);
        Assert.Equal(["/"], c.Testability.Routes.Public);
        Assert.Empty(ContractValidator.Validate(c, Samples.Node));
    }
}
