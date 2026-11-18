using Litos.Probe.Web.Core.Contract;
using Litos.Probe.Web.Core.Profiles;
using Litos.Probe.Web.Core.Reports;
using Litos.Probe.Web.Running;

namespace Litos.Probe.Web.Tests;

/// <summary>The contract's <c>services</c>: the image allowlist and the rules around it (testability-architecture.md §6.7).</summary>
public class ServicesTests
{
    private static WebAppContract WithServices(IReadOnlyList<ServiceDefinition> services, string connection = "Port={service:db:port};Password={service:db:POSTGRES_PASSWORD}")
    {
        var c = Samples.Minimal();
        var web = c.Application.Processes[0] with
        {
            Environment = new Dictionary<string, string> { ["PORT"] = "{port:web}", ["DB"] = connection },
        };
        return c with { Application = c.Application with { Services = services, Processes = [web] } };
    }

    private static readonly ServiceDefinition Postgres = new()
    {
        Name = "db",
        Image = "postgres:16-alpine",
        Environment = new Dictionary<string, string> { ["POSTGRES_PASSWORD"] = "{generated}" },
    };

    private static IReadOnlyList<string> Validate(WebAppContract c) => ContractValidator.Validate(c, Samples.Generic);

    [Fact]
    public void The_default_catalog_lists_the_allowlist()
    {
        Assert.Equal(
            ["axllent/mailpit", "mariadb", "mcr.microsoft.com/mssql/server", "mongo", "mysql", "postgres", "redis"],
            ServiceCatalog.Default.All.Select(i => i.Repository).Order());
        Assert.All(ServiceCatalog.Default.All, i => Assert.NotEmpty(i.Ports));
        Assert.True(ServiceCatalog.Default.Find("axllent/mailpit:v1.27")!.CapturesEmail);
    }

    [Theory]
    [InlineData("postgres", "postgres")]
    [InlineData("postgres:16", "postgres")]
    [InlineData("postgres@sha256:abc", "postgres")]
    [InlineData("docker.io/library/postgres:16", "postgres")]
    [InlineData("axllent/mailpit:v1.27", "axllent/mailpit")]
    [InlineData("mcr.microsoft.com/mssql/server:2022-latest", "mcr.microsoft.com/mssql/server")]
    [InlineData("localhost:5000/postgres", "localhost:5000/postgres")]
    public void An_image_reference_names_its_repository(string image, string repository) =>
        Assert.Equal(repository, ServiceCatalog.RepositoryOf(image));

    [Fact]
    public void A_postgres_service_with_its_password_is_valid() => Assert.Empty(Validate(WithServices([Postgres])));

    [Fact]
    public void An_image_off_the_allowlist_is_refused()
    {
        var errors = Validate(WithServices([Postgres with { Image = "evil/miner:latest" }]));
        Assert.Contains(errors, e => e.Contains("'evil/miner:latest' is not an allowed service image"));
    }

    [Fact]
    public void An_image_needs_its_required_variables()
    {
        var errors = Validate(WithServices([Postgres with { Environment = new Dictionary<string, string>() }], "x"));
        Assert.Contains(errors, e => e.Contains("postgres needs POSTGRES_PASSWORD"));
    }

    [Theory]
    [InlineData("{secret:DB_PASS}")]
    [InlineData("{port:web}")]
    public void A_service_value_is_literal_or_generated(string value)
    {
        var errors = Validate(WithServices([Postgres with { Environment = new Dictionary<string, string> { ["POSTGRES_PASSWORD"] = value } }], "x"));
        Assert.Contains(errors, e => e.Contains("a service's values are literal or {generated}"));
    }

    [Fact]
    public void Service_names_are_checked_and_unique()
    {
        var errors = Validate(WithServices([Postgres, Postgres], "x"));
        Assert.Contains(errors, e => e.Contains("'db' is declared twice"));
    }

    [Theory]
    [InlineData("{service:cache:port}", "names no service")]
    [InlineData("{service:db:smtp_port}", "the service 'db' has no 'smtp_port'")]
    [InlineData("{service:db:POSTGRES_USER}", "has no 'POSTGRES_USER'")]
    public void A_service_placeholder_must_name_a_real_value(string connection, string error) =>
        Assert.Contains(Validate(WithServices([Postgres], connection)), e => e.Contains(error));

    [Fact]
    public void Mailpit_offers_both_of_its_ports()
    {
        var mail = new ServiceDefinition { Name = "mail", Image = "axllent/mailpit" };
        Assert.Empty(Validate(WithServices([mail], "smtp={service:mail:smtp_port} ui={service:mail:http_port} first={service:mail:port}")));
    }

    [Fact]
    public void A_host_can_extend_the_allowlist()
    {
        var catalog = new ServiceCatalog([.. ServiceCatalog.Default.All, new ServiceImage { Repository = "acme/queue", Ports = new Dictionary<string, int> { ["port"] = 5672 } }]);
        var c = WithServices([new ServiceDefinition { Name = "queue", Image = "acme/queue:3" }], "{service:queue:port}");

        Assert.Empty(ContractValidator.Validate(c, Samples.Generic, catalog));
        Assert.NotEmpty(ContractValidator.Validate(c, Samples.Generic));
    }

    [Fact]
    public void A_readiness_command_reads_the_services_own_values() =>
        Assert.Equal("-P s3cret", DockerServices.ExpandEnvironment("-P {env:MSSQL_SA_PASSWORD}", new Dictionary<string, string> { ["MSSQL_SA_PASSWORD"] = "s3cret" }));

    [Fact]
    public void Container_names_carry_the_run()
    {
        Assert.Equal("litos-probe-abc123-db", DockerServices.ContainerName("abc123", "db"));
    }

    [Theory]
    [InlineData(FailureCause.DockerUnavailable)]
    [InlineData(FailureCause.ServiceNotReady)]
    public void A_service_that_cannot_start_is_an_environment_failure(FailureCause cause) =>
        Assert.Equal(Classification.EnvironmentFailure, Classifier.Classify(cause));

    [Fact]
    public void Services_parse_from_yaml()
    {
        var c = ContractYaml.Parse("""
            application:
              services:
                - name: db
                  image: postgres:16
                  environment:
                    POSTGRES_PASSWORD: "{generated}"
            """);

        Assert.Equal(("db", "postgres:16", "{generated}"), (c.Application.Services[0].Name, c.Application.Services[0].Image, c.Application.Services[0].Environment["POSTGRES_PASSWORD"]));
    }
}
