using System.IO.Compression;
using Litos.Probe.Web.Core.Reports;
using Litos.Probe.Web.Profiles;
using Microsoft.Playwright;

namespace Litos.Probe.Web.IntegrationTests;

/// <summary>Runs the engine on a test app the way the CLI does, into a fresh artifact folder.</summary>
internal static class ProbeHarness
{
    private static readonly Lazy<Task<string?>> BrowserProblem = new(FindBrowserProblemAsync);

    public static string TestApps { get; } = FindTestApps();

    public static string App(params string[] parts) => Path.Combine([TestApps, .. parts]);

    /// <summary>Skips the test when this machine can't run it: no Chromium for Playwright, or a missing tool.</summary>
    public static async Task RequireAsync(params string[] tools)
    {
        var problem = await BrowserProblem.Value;
        Skip.If(problem is not null, problem);
        foreach (var tool in tools)
            Skip.If(!OnPath(tool), $"{tool} is not on PATH.");
    }

    public static async Task<(ProbeWebReport Report, string Artifacts)> GateAsync(
        string app, IReadOnlyDictionary<string, string>? secrets = null, GateScope? scope = null, bool smoke = false,
        TimeSpan? timeout = null, TimeSpan? pageTimeout = null, string docker = "docker", IReadOnlyList<Core.Scenarios.WebScenario>? scenarios = null)
    {
        var contract = ContractYaml.Load(Path.Combine(app, ContractYaml.FileName));
        var folder = Path.Combine(Path.GetTempPath(), "probe-web-it", $"{Path.GetFileName(app)}-{Guid.NewGuid().ToString("N")[..8]}");
        var options = new ProbeWebOptions(
            new EnvironmentSecretResolver(secrets ?? new Dictionary<string, string>()),
            new FolderArtifactSink(folder),
            HostEnvironment(),
            timeout ?? TimeSpan.FromMinutes(10))
        {
            PageTimeout = pageTimeout ?? TimeSpan.FromSeconds(30),
            DockerExecutable = docker,
            ServiceOwner = "probe-web-tests",
        };

        var engine = new ProbeWeb(StackProfiles.Embedded());
        var report = smoke
            ? await engine.SmokeAsync(app, contract, options, CancellationToken.None, scenarios)
            : await engine.GateAsync(app, contract, scope ?? GateScope.All, options, CancellationToken.None, scenarios);
        return (report, folder);
    }

    /// <summary>Scenarios on a test app, with its own contract.</summary>
    public static async Task<(ProbeWebReport Report, string Artifacts)> ScenariosAsync(
        string app, IReadOnlyList<Core.Scenarios.WebScenario> scenarios, IReadOnlyDictionary<string, string>? secrets = null)
    {
        var contract = ContractYaml.Load(Path.Combine(app, ContractYaml.FileName));
        var folder = Path.Combine(Path.GetTempPath(), "probe-web-it", $"scenarios-{Path.GetFileName(app)}-{Guid.NewGuid().ToString("N")[..8]}");
        var options = new ProbeWebOptions(
            new EnvironmentSecretResolver(secrets ?? new Dictionary<string, string>()), new FolderArtifactSink(folder), HostEnvironment(), TimeSpan.FromMinutes(10));
        var report = await new ProbeWeb(StackProfiles.Embedded()).ScenariosAsync(app, contract, scenarios, options, CancellationToken.None);
        return (report, folder);
    }

    /// <summary>Skips the test when Docker is not running here.</summary>
    public static async Task RequireDockerAsync()
    {
        var info = await RunDockerAsync("info", "--format", "{{.ServerVersion}}");
        Skip.If(info.ExitCode != 0, "Docker is not running.");
    }

    /// <summary>Runs the Docker CLI and returns its exit code and output.</summary>
    public static async Task<(int ExitCode, string Output)> RunDockerAsync(params string[] arguments)
    {
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in arguments)
                start.ArgumentList.Add(argument);
            using var process = System.Diagnostics.Process.Start(start)!;
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            return (process.ExitCode, output);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (-1, "");
        }
    }

    /// <summary>This test run's environment, with the test secrets taken out so only the resolver supplies them.</summary>
    public static Dictionary<string, string> HostEnvironment()
    {
        var environment = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? "", StringComparer.OrdinalIgnoreCase);
        foreach (var name in environment.Keys.Where(k => k.StartsWith("TEST_", StringComparison.OrdinalIgnoreCase) || k.StartsWith("BROKEN_", StringComparison.OrdinalIgnoreCase)).ToList())
            environment.Remove(name);
        return environment;
    }

    public static CheckResult Check(ProbeWebReport report, string id) =>
        report.Checks.SingleOrDefault(c => c.Id == id)
        ?? throw new Xunit.Sdk.XunitException($"No check '{id}'. Checks: {string.Join(", ", report.Checks.Select(c => $"{c.Id}={c.Status}"))}");

    public static string Describe(ProbeWebReport report) =>
        string.Join("\n", report.Checks.Select(c => $"{c.Id}: {c.Status} {c.Classification} {c.Message}").Concat(report.Findings));

    /// <summary>Every artifact's text, with trace zips opened, for scanning for secrets.</summary>
    public static IEnumerable<(string File, string Text)> ArtifactTexts(string folder)
    {
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            if (file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using var zip = ZipFile.OpenRead(file);
                foreach (var entry in zip.Entries)
                {
                    using var reader = new StreamReader(entry.Open());
                    yield return ($"{file}!{entry.FullName}", reader.ReadToEnd());
                }
            }
            else
            {
                yield return (file, File.ReadAllText(file));
            }
        }
    }

    private static async Task<string?> FindBrowserProblemAsync()
    {
        try
        {
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
            return null;
        }
        catch (PlaywrightException ex)
        {
            return $"Chromium for Playwright is not available: {ex.Message.Split('\n')[0]}";
        }
    }

    private static bool OnPath(string tool) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Any(dir => new[] { "", ".exe", ".cmd" }.Any(ext => File.Exists(Path.Combine(dir.Trim('"'), tool + ext))));

    private static string FindTestApps()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "Litos.Probe.Web.TestApps");
            if (Directory.Exists(candidate))
                return candidate;
        }

        throw new DirectoryNotFoundException("tests/Litos.Probe.Web.TestApps was not found above the test assembly.");
    }
}
