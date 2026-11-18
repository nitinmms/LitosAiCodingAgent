using Litos.Probe.Web.Core.Contract;

namespace Litos.Probe.Web.Tests;

public class PlaceholdersTests
{
    private sealed class Values : IPlaceholderValues
    {
        public Dictionary<string, string> Secrets { get; } = new() { ["PASS"] = "s3cret" };
        public int Generated_ { get; private set; }

        public int? Port(string process) => process == "web" ? 51873 : null;
        public string? Service(string name, string key) => name == "db" && key == "port" ? "5432" : null;
        public string Generated() => $"gen{++Generated_}";
        public string? Secret(string name) => Secrets.GetValueOrDefault(name);
    }

    [Fact]
    public void Find_reads_every_kind_with_its_parts()
    {
        var found = Placeholders.Find("http://localhost:{port:web}/x?{secret:PASS}&{service:db:POSTGRES_PASSWORD}&{generated}").ToList();

        Assert.Equal(
            [
                new PlaceholderRef(PlaceholderKind.Port, "{port:web}", "web", null),
                new PlaceholderRef(PlaceholderKind.Secret, "{secret:PASS}", "PASS", null),
                new PlaceholderRef(PlaceholderKind.Service, "{service:db:POSTGRES_PASSWORD}", "db", "POSTGRES_PASSWORD"),
                new PlaceholderRef(PlaceholderKind.Generated, "{generated}", null, null),
            ],
            found);
    }

    [Theory]
    [InlineData("{\"json\": true}")]
    [InlineData("^\\d{3}$")]
    [InlineData("{other:thing}")]
    [InlineData("")]
    public void Find_leaves_other_braces_alone(string text) => Assert.Empty(Placeholders.Find(text));

    [Theory]
    [InlineData("{port}", "{port:<process>}")]
    [InlineData("{port:web:extra}", "{port:<process>}")]
    [InlineData("{service:db}", "{service:<name>:port}")]
    [InlineData("{secret}", "{secret:NAME}")]
    [InlineData("{generated:x}", "{generated}")]
    public void Problem_names_the_right_form(string text, string expected)
    {
        var problem = Placeholders.Problem(Placeholders.Find(text).Single());
        Assert.NotNull(problem);
        Assert.Contains(expected, problem);
    }

    [Fact]
    public void Resolve_substitutes_every_placeholder()
    {
        var values = new Values();
        Assert.Equal("Host=127.0.0.1;Port=5432;Password=s3cret;Url=http://localhost:51873;Key=gen1",
            Placeholders.Resolve("Host=127.0.0.1;Port={service:db:port};Password={secret:PASS};Url=http://localhost:{port:web};Key={generated}", values));
    }

    [Fact]
    public void Each_generated_placeholder_gets_its_own_value()
    {
        Assert.Equal("gen1-gen2", Placeholders.Resolve("{generated}-{generated}", new Values()));
    }

    [Theory]
    [InlineData("{port:api}", "no process named 'api'")]
    [InlineData("{secret:MISSING}", "the secret MISSING is not set")]
    [InlineData("{service:cache:port}", "no such service value")]
    [InlineData("{port}", "write {port:<process>}")]
    public void Resolve_throws_with_a_reason(string text, string reason)
    {
        var ex = Assert.Throws<PlaceholderException>(() => Placeholders.Resolve(text, new Values()));
        Assert.Contains(reason, ex.Message);
    }

    [Theory]
    [InlineData("{port:api}", PlaceholderKind.Port)]
    [InlineData("{secret:MISSING}", PlaceholderKind.Secret)]
    [InlineData("{service:cache:port}", PlaceholderKind.Service)]
    public void A_missing_value_says_which_kind_was_missing(string text, PlaceholderKind kind) =>
        Assert.Equal(kind, Assert.Throws<PlaceholderException>(() => Placeholders.Resolve(text, new Values())).Kind);

    [Fact]
    public void A_malformed_placeholder_has_no_kind() =>
        Assert.Null(Assert.Throws<PlaceholderException>(() => Placeholders.Resolve("{port}", new Values())).Kind);

    [Fact]
    public void Resolve_resolves_an_environment_and_keeps_its_names()
    {
        var resolved = Placeholders.Resolve(new Dictionary<string, string> { ["PORT"] = "{port:web}", ["ConnectionStrings__Default"] = "x" }, new Values());
        Assert.Equal("51873", resolved["PORT"]);
        Assert.Equal("x", resolved["ConnectionStrings__Default"]);
    }
}
