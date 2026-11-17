using Litos.Probe.Web.Core.Contract;
using Litos.Probe.Web.Core.Profiles;
using Litos.Probe.Web.Profiles;

namespace Litos.Probe.Web.Tests;

/// <summary>Contracts and profiles shared by the tests.</summary>
internal static class Samples
{
    public static StackProfiles Profiles { get; } = StackProfiles.Embedded();

    public static StackProfile Node => Profiles.Find("node-express-spa")!;
    public static StackProfile Blazor => Profiles.Find("aspnet-blazor-server")!;
    public static StackProfile Generic => Profiles.Find("generic-http")!;

    /// <summary>The smallest valid contract: one process, no login, nothing to reset.</summary>
    public static WebAppContract Minimal(string stack = "generic-http") => new()
    {
        Application = new AppDefinition
        {
            Stack = stack,
            Processes = [new ProcessDefinition { Name = "web", Command = ["node", "server.js"], Environment = new Dictionary<string, string> { ["PORT"] = "{port:web}" } }],
            BaseUrl = "web",
            Reset = new ResetDefinition { Strategy = ResetStrategy.None },
        },
        Testability = new TestabilityDefinition { Routes = new RouteSet { Public = ["/"] } },
    };

    /// <summary>The clinic-portal example of testability-architecture.md §5 B, without its Postgres service.</summary>
    public static WebAppContract Clinic() => new()
    {
        Application = new AppDefinition
        {
            Stack = "aspnet-blazor-server",
            Processes =
            [
                new ProcessDefinition
                {
                    Name = "web",
                    Environment = new Dictionary<string, string> { ["TEST_ADMIN_PASS"] = "{secret:TEST_ADMIN_PASS}" },
                },
            ],
            BaseUrl = "web",
            Reset = new ResetDefinition { Strategy = ResetStrategy.Endpoint, Endpoint = "POST /__test/reset" },
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
                Roles = new Dictionary<string, RoleCredentials>
                {
                    ["admin"] = new() { User = "admin@clinic.test", Password = "{secret:TEST_ADMIN_PASS}" },
                    ["patient"] = new() { User = "{secret:TEST_PATIENT_USER}", Password = "{secret:TEST_PATIENT_PASS}" },
                },
            },
            Routes = new RouteSet
            {
                Public = ["/"],
                Protected = new Dictionary<string, IReadOnlyList<string>> { ["admin"] = ["/admin/users"], ["patient"] = ["/my/appointments"] },
                Denied = new Dictionary<string, IReadOnlyList<string>> { ["patient"] = ["/admin/users"] },
            },
        },
    };

    public static string TestAppPath(params string[] parts) =>
        Path.Combine([AppContext.BaseDirectory, "TestApps", .. parts]);
}

/// <summary>A folder under the temp directory, deleted at the end of the test.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "probe-web-tests", Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Write(string relative, string content)
    {
        var full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
