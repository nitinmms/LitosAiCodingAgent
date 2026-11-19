using System.Text.Json;
using Litos.Probe.Web.Core;
using Litos.Probe.Web.Core.Contract;

namespace Litos.Probe.Web.Tests;

public class ContractYamlTests
{
    private const string Clinic = """
        version: 1
        application:
          stack: aspnet-blazor-server
          build:
            - command: [dotnet, build, src/Web, -c, Release]
              timeout_seconds: 900
          processes:
            - name: web
              command: [dotnet, run, --project, src/Web]
              start_timeout_seconds: 60
              environment:
                ASPNETCORE_URLS: "http://localhost:{port:web}"
                ConnectionStrings__Default: Data Source=app.db
                PORT_HINT: 3000
              ready: { http: /health }
          base_url: web
          reset:
            strategy: sqlite-snapshot
            file: data/app.db
            seed: [dotnet, run, --project, tools/Seed]
          allowed_origins: [https://cdn.example.com]
        testability:
          ready:
            selector: "[data-testid=app-interactive]"
          auth:
            type: form
            login_url: /Account/Login
            fields: { user: Email, password: Password }
            submit: Log in
            success_when: { url_not_contains: /Account/Login }
            roles:
              admin: { user: admin@clinic.test, password: "{secret:TEST_ADMIN_PASS}" }
          test_endpoints:
            clock: POST /__test/clock
            seed_Order: POST /__test/seed/order
          fail_on:
            - selector_visible: "#components-reconnect-modal"
            - log_pattern: "^crit:"
          console_errors: false
          routes:
            public: [/, /Account/Login]
            protected:
              admin: [/admin/users]
            denied:
              admin_Readonly: [/admin]
        """;

    [Fact]
    public void Parses_every_section_onto_the_model()
    {
        var c = ContractYaml.Parse(Clinic);
        var app = c.Application;
        var web = app.Processes.Single();

        Assert.Equal("aspnet-blazor-server", app.Stack);
        Assert.Equal(900, app.Build!.Single().TimeoutSeconds);
        Assert.Equal(60, web.StartTimeoutSeconds);
        Assert.Equal("/health", web.Ready!.Http);
        Assert.Equal(ResetStrategy.SqliteSnapshot, app.Reset!.Strategy);
        Assert.Equal(["dotnet", "run", "--project", "tools/Seed"], app.Reset.Seed);
        Assert.Equal(["https://cdn.example.com"], app.AllowedOrigins);
        Assert.Equal(AuthType.Form, c.Testability.Auth.Type);
        Assert.Equal("Email", c.Testability.Auth.Fields!.User);
        Assert.Equal("/Account/Login", c.Testability.Auth.SuccessWhen!.UrlNotContains);
        Assert.Equal("{secret:TEST_ADMIN_PASS}", c.Testability.Auth.Roles["admin"].Password);
        Assert.Equal("^crit:", c.Testability.FailOn[1].LogPattern);
        Assert.False(c.Testability.ConsoleErrors);
    }

    [Fact]
    public void Data_keys_are_kept_as_written_and_schema_keys_are_mapped()
    {
        var c = ContractYaml.Parse(Clinic);

        Assert.Contains("ConnectionStrings__Default", c.Application.Processes[0].Environment.Keys);
        Assert.Contains("seed_Order", c.Testability.TestEndpoints.Keys);
        Assert.Contains("admin_Readonly", c.Testability.Routes.Denied.Keys);
    }

    [Fact]
    public void Numbers_in_data_stay_text()
    {
        var c = ContractYaml.Parse(Clinic);
        Assert.Equal("3000", c.Application.Processes[0].Environment["PORT_HINT"]);
    }

    [Theory]
    [InlineData("version: 1\napplication:\n  base_urll: web\n", "base_urll")]
    [InlineData("version: 1\napplication:\n  processes:\n    - name: web\n      start_timeout_seconds: soon\n", "whole number")]
    [InlineData("version: 1\ntestability:\n  console_errors: maybe\n", "true or false")]
    [InlineData("version: 1\nversion: 2\n", "not valid YAML")]
    [InlineData("- just\n- a list\n", "must be a mapping")]
    [InlineData("application: [unclosed\n", "not valid YAML")]
    public void Bad_yaml_is_a_contract_format_error(string yaml, string fragment)
    {
        var ex = Assert.Throws<ContractFormatException>(() => ContractYaml.Parse(yaml));
        Assert.Contains(fragment, ex.Message);
    }

    [Fact]
    public void A_misspelt_key_is_reported_by_its_yaml_path()
    {
        var ex = Assert.Throws<ContractFormatException>(() => ContractYaml.Parse("application:\n  reset:\n    strategy: none\n    seed_command: [x]\n"));
        Assert.Contains("application.reset", ex.Message);
    }

    [Fact]
    public void Empty_values_are_empty_data_or_absent_settings_never_nulls()
    {
        var c = ContractYaml.Parse("""
            application:
              stack: node-express-spa
              build:
              processes:
                - name: web
                  command: [node, server.js, ""]
                  environment:
                    EMPTY:
              base_url: web
              reset: { strategy: none }
            testability:
              routes:
                public:
                  - /
            """);

        Assert.Null(c.Application.Build); // "not given": the profile's default build applies
        Assert.Equal("", c.Application.Processes[0].Environment["EMPTY"]);
        Assert.Empty(new ProbeWeb(Samples.Profiles).Validate(c));
    }

    [Fact]
    public void An_empty_list_item_is_an_empty_string()
    {
        var c = ContractYaml.Parse("application:\n  processes:\n    - name: web\n      command:\n        - node\n        -\n");
        Assert.Equal(["node", ""], c.Application.Processes[0].Command);
    }

    [Fact]
    public void Serialize_round_trips()
    {
        var original = ContractYaml.Parse(Clinic);
        var yaml = ContractYaml.Serialize(original);
        var again = ContractYaml.Parse(yaml);

        Assert.Equal(JsonSerializer.Serialize(original, ProbeWebJson.Options), JsonSerializer.Serialize(again, ProbeWebJson.Options));
        Assert.Contains("base_url: web", yaml);
        Assert.Contains("ConnectionStrings__Default:", yaml);
    }

    /// <summary>The clinic contract with every data map's keys reversed, as a jsonb column may hand them back.</summary>
    private static WebAppContract WithDataMapsReversed(WebAppContract c) => c with
    {
        Application = c.Application with
        {
            Processes = [.. c.Application.Processes.Select(p => p with { Environment = p.Environment.Reverse().ToDictionary() })],
        },
        Testability = c.Testability with
        {
            Auth = c.Testability.Auth is { } auth ? auth with { Roles = auth.Roles.Reverse().ToDictionary() } : null,
            Routes = c.Testability.Routes with
            {
                Protected = c.Testability.Routes.Protected.Reverse().ToDictionary(),
                Denied = c.Testability.Routes.Denied.Reverse().ToDictionary(),
            },
        },
    };

    [Fact]
    public void Canonical_ignores_the_order_of_data_maps()
    {
        var original = ContractYaml.Parse(Clinic);
        var reordered = WithDataMapsReversed(original);

        Assert.NotEqual(ContractYaml.Serialize(original), ContractYaml.Serialize(reordered));
        Assert.Equal(ContractYaml.Canonical(original), ContractYaml.Canonical(reordered));
    }

    [Fact]
    public void Canonical_keeps_the_schemas_own_order_and_still_round_trips()
    {
        var original = ContractYaml.Parse(Clinic);
        var canonical = ContractYaml.Canonical(original);

        // Only data maps are sorted: version still comes first, and application before testability.
        Assert.StartsWith("version: 1", canonical);
        Assert.True(canonical.IndexOf("application:", StringComparison.Ordinal) < canonical.IndexOf("testability:", StringComparison.Ordinal));
        var again = ContractYaml.Parse(canonical);
        Assert.Equal(ContractYaml.Canonical(original), ContractYaml.Canonical(again));
    }

    [Fact]
    public void Canonical_still_tells_a_real_change_apart()
    {
        var original = ContractYaml.Parse(Clinic);
        var process = original.Application.Processes[0];
        var changedValue = original with
        {
            Application = original.Application with
            {
                Processes = [process with { Environment = new Dictionary<string, string>(process.Environment) { ["PORT_HINT"] = "4000" } }],
            },
        };
        // The order of a list, unlike a map, means something (build steps run in turn).
        var reversedArguments = original with
        {
            Application = original.Application with { Processes = [process with { Command = [.. process.Command.Reverse()] }] },
        };

        Assert.NotEqual(ContractYaml.Canonical(original), ContractYaml.Canonical(changedValue));
        Assert.NotEqual(ContractYaml.Canonical(original), ContractYaml.Canonical(reversedArguments));
    }

    [Fact]
    public void The_clinic_sample_survives_the_json_used_by_profile_v3()
    {
        var c = Samples.Clinic();
        var json = JsonSerializer.Serialize(c, ProbeWebJson.Options);
        var back = JsonSerializer.Deserialize<WebAppContract>(json, ProbeWebJson.Options)!;

        Assert.Equal(json, JsonSerializer.Serialize(back, ProbeWebJson.Options));
        Assert.Contains("\"strategy\":\"endpoint\"", json);
        Assert.Contains("\"type\":\"form\"", json);
    }

    [Theory]
    [InlineData("snake_case_key", "snakeCaseKey")]
    [InlineData("base_url", "baseUrl")]
    [InlineData("version", "version")]
    public void Key_names_map_both_ways(string snake, string camel)
    {
        Assert.Equal(camel, ContractYaml.CamelCase(snake));
        Assert.Equal(snake, ContractYaml.SnakeCase(camel));
    }

    public static TheoryData<string> TestAppContracts() =>
    [
        .. Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "TestApps"), "*.yaml", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(Path.Combine(AppContext.BaseDirectory, "TestApps"), f)),
    ];

    [Theory]
    [MemberData(nameof(TestAppContracts))]
    public void Every_test_app_contract_parses_and_validates(string relative)
    {
        var contract = ContractYaml.Load(Samples.TestAppPath(relative));
        var errors = new ProbeWeb(Samples.Profiles).Validate(contract);
        Assert.True(errors.Count == 0, $"{relative}: {string.Join(" | ", errors)}");
    }
}
